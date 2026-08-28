using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace IdleExplorersTests
{
    /// <summary>
    /// Whether the game actually uses the server it was rebuilt around.
    ///
    /// ══ WHY THIS FILE EXISTS ══════════════════════════════════════════════════════
    ///
    /// Twice now, a finished and tested seam turned out to have no callers. First
    /// nothing invoked GameBackend.Configure, so the whole backend was unreachable.
    /// Then nothing invoked the backend itself for the core loop, so gathering,
    /// crafting, settlement, experience, inventory and currency all still ran on the
    /// client and were written to a file the player owns -- the exact thing the
    /// re-architecture existed to stop.
    ///
    /// Both times everything compiled and every test passed. A type checker cannot see
    /// the difference between code that is called and code that merely could be, and
    /// neither can a test suite that never runs the game.
    ///
    /// So these check for CALLS, and for the absence of the local paths that would
    /// quietly pay twice if they ran alongside the server.
    /// </summary>
    internal static class AuthorityChecks
    {
        internal static void Run(Action<bool, string> check, string root)
        {
            Console.WriteLine("Server authority");

            TheCoreLoopCallsTheServer(check, root);
            TheClientStopsPayingItself(check, root);
            TheRestGoesToTheServerToo(check, root);
            TheSyncLoopRuns(check, root);
        }

        // ══ THE CALLS EXIST ═══════════════════════════════════════════════════

        private static void TheCoreLoopCallsTheServer(Action<bool, string> check, string root)
        {
            string state = Strip(Read(root, "Assets/Scripts/Backend/ServerState.cs"));

            check(state.Length > 0, "something exists whose job is to hold server state");

            if (state.Length == 0) return;

            // Every call the core loop depends on. A missing one is a whole subsystem
            // still running locally, which is invisible until somebody audits it.
            foreach (string call in new[]
                     {
                         "GetAccountAsync", "GetCharacterAsync",
                         "SettleAsync", "HeartbeatAsync",
                         "SetGatheringAsync", "SetCraftingAsync", "SetFightingAsync",
                     })
            {
                check(state.Contains(call), $"ServerState calls {call}");
            }

            // And the calls have to be reached from the GAME, not just defined here.
            // That distinction is the whole point of this file.
            var callers = new (string File, string Call, string Why)[]
            {
                ("Assets/Scripts/PlayerScripts/SkillNodeController.cs", "SetGatheringAsync",
                 "working a node tells the server"),
                ("Assets/Scripts/PlayerScripts/SkillNodeController.cs", "SetCraftingAsync",
                 "starting a recipe tells the server"),
                ("Assets/Scripts/Managers/ActivityManager.cs", "SetFightingAsync",
                 "entering combat tells the server"),
                ("Assets/Scripts/Managers/CharacterManager.cs", "PullCharacterAsync",
                 "selecting a character loads its state from the server"),
                ("Assets/Scripts/UI/Screens/LoginScreen.cs", "PullAccountAsync",
                 "signing in loads the account from the server"),
            };

            foreach (var (file, call, why) in callers)
                check(Strip(Read(root, file)).Contains(call), why);
        }

        // ══ AND THE LOCAL ONES DO NOT RUN ALONGSIDE ═══════════════════════════

        /// <summary>
        /// The client must not grant what the server is also granting.
        ///
        /// Paying twice is the obvious harm, but the subtler one is worse: the local
        /// half is the half a cheat can edit, so leaving it running keeps the exploit
        /// alive underneath a server that looks authoritative.
        ///
        /// A gathering tick that granted locally would also make the bar run ahead and
        /// then jump backwards on the next settle, which players read as the game
        /// losing their items.
        /// </summary>
        private static void TheClientStopsPayingItself(Action<bool, string> check, string root)
        {
            string node     = Strip(Read(root, "Assets/Scripts/PlayerScripts/SkillNodeController.cs"));
            string activity = Strip(Read(root, "Assets/Scripts/Managers/ActivityManager.cs"));

            // ══ NO DIRECT GRANTS AT ALL ═══════════════════════════════════════
            //
            // Not "every grant is guarded" -- "there are no grants here to guard". The
            // guard lives inside LocalRewards, so this is an exact thing to look for
            // rather than a judgement about whether some nearby if-statement covers
            // this particular line.
            //
            // Two earlier versions of this check were proximity heuristics, and both
            // passed after the guard was deliberately removed: the first because an
            // alternation matched the OTHER grant site, the second because a 600
            // character window reached back into the block above. A heuristic tuned
            // until it happens to pass is not a check.
            //
            // Making the invariant structural is what made it checkable.
            foreach (Match grant in Regex.Matches(node, @"GameManager\.(Inventory|Skills)\??\.Add\w+"))
            {
                check(false,
                      $"SkillNodeController grants directly ('{grant.Value}') instead of " +
                      "through LocalRewards, where the authority check lives");
            }

            check(!Regex.IsMatch(node, @"GameManager\.(Inventory|Skills)\??\.Add\w+"),
                  "gameplay code pays through LocalRewards, never directly");

            check(node.Contains("LocalRewards."),
                  "SkillNodeController actually uses LocalRewards");

            string rewards = Strip(Read(root, "Assets/Scripts/Managers/LocalRewards.cs"));

            check(rewards.Contains("IsAuthoritative"),
                  "LocalRewards is the one place that asks who is paying");

            // ══ EACH METHOD'S OWN BODY, NOT THE FILE'S ════════════════════════
            //
            // "The file contains a guard" passed after the guard was deleted from one
            // method, because the other two still had theirs. Third time this suite has
            // been fooled by a match belonging to a neighbour.
            //
            // Split on the signatures and check each chunk separately. The property
            // that DEFINES ClientPays is skipped -- it is the answer, not a caller.
            string[] methods = rewards.Split(new[] { "public static" }, StringSplitOptions.None);

            for (int i = 1; i < methods.Length; i++)
            {
                string body = methods[i];

                // Skip the class declaration and the property that DEFINES ClientPays:
                // neither is a payer, and both match "public static".
                if (body.TrimStart().StartsWith("class"))  continue;
                if (body.Contains("bool ClientPays"))      continue;

                string name = body.Split('(')[0].Trim();

                check(body.Contains("ClientPays"),
                      $"LocalRewards.{name} asks who is paying before it pays");
            }

            // The 665-line offline payout. It reads a timestamp the player's machine
            // wrote and integrates it against rates the player's machine holds -- it is
            // the exploit, not a step towards fixing it.
            check(Regex.IsMatch(activity,
                      @"ProcessAFKRewards[\s\S]{0,900}?IsAuthoritative[\s\S]{0,120}?return null"),
                  "ProcessAFKRewards refuses to pay when the server is authoritative");
        }

        // ══ EQUIPMENT, BANK AND TALENTS ═══════════════════════════════════════
        //
        // The last three things still decided on the client after the core loop moved.
        // Each is Critical tier: a worn item is a stat, a banked item is an item, and a
        // talent point is a stat -- all three feed damage per second.
        //
        // Same structural pattern as LocalRewards, for the same reason: an invariant
        // spread across if-statements is only checkable by proximity, and proximity
        // checks were fooled three times on the gathering path.
        private static void TheRestGoesToTheServerToo(Action<bool, string> check, string root)
        {
            string actions = Strip(Read(root, "Assets/Scripts/Managers/ServerActions.cs"));

            check(actions.Length > 0, "ServerActions exists");

            if (actions.Length == 0) return;

            foreach (string call in new[]
                     { "EquipAsync", "UnequipAsync", "DepositAsync", "WithdrawAsync", "SpendTalentAsync" })
            {
                check(actions.Contains(call), $"ServerActions calls {call}");
            }

            // Each entry point asks who decides before doing anything. Per-method, not
            // per-file -- a guard in a neighbouring method is not a guard on this one,
            // which is a mistake this suite has already made.
            string[] methods = actions.Split(new[] { "public static bool" }, StringSplitOptions.None);

            for (int i = 1; i < methods.Length; i++)
            {
                string body = methods[i];

                if (body.Contains("ClientDecides =>")) continue;   // the definition

                string name = body.Split('(')[0].Trim();

                check(body.Contains("ClientDecides"),
                      $"ServerActions.{name} defers to the server when there is one");
            }

            // And the managers actually call it, rather than deciding for themselves.
            var wired = new (string File, string Call, string Why)[]
            {
                ("Assets/Scripts/Managers/EquipmentManager.cs", "ServerActions.Equip",
                 "equipping goes to the server"),
                ("Assets/Scripts/Managers/EquipmentManager.cs", "ServerActions.Unequip",
                 "unequipping goes to the server"),
                ("Assets/Scripts/Managers/BankManager.cs", "ServerActions.Deposit",
                 "depositing goes to the server, which holds the account lock"),
                ("Assets/Scripts/Managers/BankManager.cs", "ServerActions.Withdraw",
                 "withdrawing goes to the server"),
                ("Assets/Scripts/Managers/TalentManager.cs", "ServerActions.SpendTalent",
                 "spending a talent point goes to the server"),
            };

            foreach (var (file, call, why) in wired)
                check(Strip(Read(root, file)).Contains(call), why);

            // ══ EACH HOOK MUST SHORT-CIRCUIT ═════════════════════════════════
            //
            // Otherwise the local path runs as well, and the player sees the client's
            // answer flicker and then be replaced -- with the client's answer being the
            // one a cheat could have changed.
            //
            // Checked by what IMMEDIATELY follows the call rather than by a character
            // window. A window wide enough for the bank's multi-line `if` is also wide
            // enough to match a `return true` that has nothing to do with the call, and
            // this suite has already been fooled by exactly that kind of slack.
            //
            // Stripping only whitespace, ) and { leaves the very next statement, which
            // must be the return.
            foreach (var (file, call, _) in wired)
            {
                string text = Strip(Read(root, file));
                int at = text.IndexOf(call, StringComparison.Ordinal);

                if (at < 0) continue;

                string after = text.Substring(at + call.Length);

                // Past the call's own argument list, then past any closing punctuation.
                int open = after.IndexOf(')');
                if (open >= 0) after = after.Substring(open + 1);

                string next = new string(after.Where(c => !char.IsWhiteSpace(c)
                                                       && c != ')' && c != '{').ToArray());

                check(next.StartsWith("returntrue", StringComparison.Ordinal),
                      $"{call} is immediately followed by a return, not by the local path");
            }
        }

        // ══ SOMETHING KEEPS IT IN STEP ════════════════════════════════════════

        private static void TheSyncLoopRuns(Action<bool, string> check, string root)
        {
            string sync = Strip(Read(root, "Assets/Scripts/Backend/ServerSync.cs"));

            check(sync.Length > 0, "a sync loop exists");

            if (sync.Length == 0) return;

            check(sync.Contains("HeartbeatAsync"),
                  "the loop heartbeats, which is the only evidence of a player at the keyboard");

            check(sync.Contains("SettleAsync"),
                  "the loop settles, so the numbers on screen move without opening a panel");

            // A settle takes the character lock and writes rows. Two in flight is the
            // second one paying nothing while holding the row.
            check(Regex.IsMatch(sync, @"_busy"),
                  "overlapping settles are prevented");

            // Nothing at all offline or in shadow mode, where the local managers are
            // the game and server answers are deliberately unused.
            check(sync.Contains("IsAuthoritative"),
                  "the loop is inert unless the server is authoritative");

            // Attached from somewhere, or it is a component nobody adds.
            check(Strip(Read(root, "Assets/Scripts/Managers/CharacterManager.cs")).Contains("ServerSync.Attach"),
                  "the loop is actually attached when a character is selected");
        }

        // ── Machinery ─────────────────────────────────────────────────────────

        private static string Read(string root, string relative)
        {
            string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

            return File.Exists(path) ? File.ReadAllText(path) : "";
        }

        /// <summary>
        /// Comments removed, so prose cannot answer for code.
        ///
        /// Every one of these files explains at length what it no longer does, and an
        /// unstripped check passes on the explanation. That trap has now caught this
        /// suite four separate times.
        /// </summary>
        private static string Strip(string source)
        {
            source = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
            source = Regex.Replace(source, @"^\s*///.*$", "", RegexOptions.Multiline);
            source = Regex.Replace(source, @"^\s*//.*$",  "", RegexOptions.Multiline);

            return source;
        }
    }
}
