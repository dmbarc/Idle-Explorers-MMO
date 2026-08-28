using System;
using System.IO;
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
