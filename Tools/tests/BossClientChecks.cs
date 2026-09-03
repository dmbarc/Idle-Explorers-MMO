using System;
using System.IO;
using System.Text.RegularExpressions;

namespace IdleExplorersTests
{
    /// <summary>
    /// The claims about the boss fight that no compiler checks.
    ///
    /// ══ WHY THESE ARE SOURCE CHECKS ═══════════════════════════════════════════════
    ///
    /// Because what they assert is the ABSENCE of code, and absence has no type. The
    /// client is not supposed to decide the King's health, roll his loot, run its own
    /// enrage clock, or pick his attacks -- and every one of those is a line somebody
    /// could add back in an afternoon without breaking a single test.
    ///
    /// A source check is a blunt instrument and it is the right one here: the thing
    /// being protected is an architectural rule, and the failure mode it prevents is
    /// somebody re-implementing the server on the client because it was convenient at
    /// the time. That is exactly how the game got into a state where account.json held
    /// the currency.
    /// </summary>
    internal static class BossClientChecks
    {
        internal static void Run(Action<bool, string> check, string root)
        {
            Console.WriteLine("Boss fight, client side");

            string controller = Read(root, "Assets/Scripts/PlayerScripts/BossController.cs");
            string fight      = Read(root, "Assets/Scripts/PlayerScripts/BossFight.cs");

            check(controller.Length > 0, "BossController.cs is where it is expected to be");
            check(fight.Length > 0,      "BossFight.cs is where it is expected to be");

            if (controller.Length == 0 || fight.Length == 0) return;

            string code = Strip(controller);
            string conversation = Strip(fight);

            // ── The client decides nothing ────────────────────────────────────

            check(!Regex.IsMatch(code, @"CurrentHealth\s*(=|-=|\+=)[^=>]"),
                  "BossController never assigns the boss's health -- it reads the server's");

            check(!Regex.IsMatch(conversation, @"BossHealth\s*=\s*[^;]*(?<!tick\.bossHp);|BossHealth\s*-=") ||
                  conversation.Contains("BossHealth = tick.bossHp"),
                  "the only thing that sets health outright is the server's reported value");

            check(!code.Contains("_enrageAt"),
                  "there is no local enrage clock -- a suspended tab would pause it, which is a free win");

            check(code.Contains("Fight.EnrageRemaining"),
                  "the enrage timer is read from the fight, which folds in the server's own elapsed");

            // ── Loot is not rolled here ───────────────────────────────────────

            foreach (string forbidden in new[] { "lootTable", "DropChance", "ItemDrop.Spawn" })
            {
                check(!code.Contains(forbidden),
                      $"BossController does not touch {forbidden} -- loot is the server's");
            }

            // The UNSTRIPPED source, since what is being looked for is a comment. The
            // stripped copy is for everything else, so a check cannot pass on the prose
            // explaining why the code is absent.
            check(controller.Contains("Loot is the SERVER"),
                  "the reason loot is absent is written down where somebody would add it back");

            // ── Attacks come from the schedule ────────────────────────────────

            check(!code.Contains("ChooseAbility"),
                  "the local ability picker is gone -- the schedule arrives at engage");

            check(!code.Contains("_cooldowns"),
                  "there are no local ability cooldowns to disagree with the server's schedule");

            check(code.Contains("Fight.CastsFor"),
                  "attacks are walked from the server's cast list");

            check(Regex.IsMatch(code, @"while\s*\(\s*_nextCast\s*<"),
                  "several casts falling due at once are all played, not collapsed into one");

            // ── What is sent ──────────────────────────────────────────────────

            check(conversation.Contains("sequence"),
                  "an action carries a sequence number");

            foreach (string forbidden in new[] { "damage =", "bossHealth =", "position", "timestamp" })
            {
                bool sends = Regex.IsMatch(conversation,
                    @"BossActionReport\s*\{[^}]*" + Regex.Escape(forbidden), RegexOptions.Singleline);

                check(!sends, $"an action report does not carry {forbidden.TrimEnd(' ', '=')}");
            }

            // ── The prediction uses the shared rules ──────────────────────────
            //
            // Not a stylistic preference. A second damage formula on the client is the
            // formula drift the whole shared assembly exists to make impossible, and it
            // shows up as a health bar that disagrees with itself twice a second.

            check(conversation.Contains("BossEncounter.SwingDamage"),
                  "the client predicts through the same function the server runs");

            check(!Regex.IsMatch(conversation, @"UnityEngine\.Random|Random\.(Range|value)"),
                  "the prediction uses the counter RNG, not Unity's, so it can match the server");

            // ── Death is an answer, not a decision ────────────────────────────

            check(code.Contains("Fight.Finished") || code.Contains("Finished += Ended"),
                  "the fight ends when the server says so");

            check(conversation.Contains("ResolveBossAsync"),
                  "the verdict is asked for rather than assumed");

            check(Regex.IsMatch(code, @"if\s*\(\s*result[^)]*\.won\s*\)"),
                  "whether the King fell is read off the server's result");

            // ── And beating him opens the way out ─────────────────────────────
            //
            // The throne is a one-way door: the only ways out were dying and the
            // travel menu, neither of which reads as having FINISHED the fight.
            //
            // Sliced to Die() rather than searched file-wide, because Enrage() sits
            // directly beneath it and a file-wide match would pass on losing as
            // happily as on winning. Four checks in this project have been fooled by
            // exactly that kind of proximity.
            string died = Body(code, "private void Die()");

            check(died.Length > 0, "BossController.Die is where it is expected to be");
            check(died.Contains("ExitPortal.Open"),
                  "beating the King opens an exit portal -- from Die, not from Enrage");

            string enraged = Body(code, "private void Enrage()");

            check(enraged.Length > 0 && !enraged.Contains("ExitPortal"),
                  "losing to the King does NOT open one");

            SpawnerRefusesBosses(check, root);
            ExitPortalIsReachable(check, root);

            TheKingIsAnEnemyYouCanFight(check, root);
            ThereIsAWayOutThatIsNotDying(check, root);
            AGroupCanDoBothTogether(check, root);
        }

        /// <summary>
        /// The five things a playtest found wrong with the King, each pinned here.
        ///
        /// ══ WHY SOURCE CHECKS FOR THESE ═══════════════════════════════════════
        ///
        /// Because four of the five are WIRING, and wiring is exactly what a compiler
        /// is happy about and a test suite does not reach. BossController compiled
        /// perfectly while being untargetable, immobile and inescapable: every method
        /// involved existed, every one of them was correct, and nothing called them.
        ///
        /// This project has a name for that failure -- "built and never called" -- and
        /// it has cost four playtests. These are the lines that would have caught it.
        /// </summary>
        private static void TheKingIsAnEnemyYouCanFight(Action<bool, string> check, string root)
        {
            string boss   = Strip(Read(root, "Assets/Scripts/PlayerScripts/BossController.cs"));
            string player = Strip(Read(root, "Assets/Scripts/PlayerScripts/PlayerController.cs"));

            check(File.Exists(Path.Combine(root, "Assets/Scripts/PlayerScripts/ICombatTarget.cs")),
                  "ICombatTarget exists -- the one thing a monster and a boss share");

            // ── He is a target at all ─────────────────────────────────────────
            //
            // He was not. PlayerController held its target as a MonsterController and
            // a boss deliberately is not one, so there was no path from a click, from
            // auto-mode or from an ability to the King. He stood in his arena throwing
            // cones at somebody who could only watch.
            check(Regex.IsMatch(boss, @"class BossController\s*:\s*MonoBehaviour\s*,\s*ICombatTarget"),
                  "BossController is something the player can point at");

            check(Regex.IsMatch(player, @"private ICombatTarget currentTarget"),
                  "the player's target is the interface, not the monster type");

            // ── Every route to a target reaches him ───────────────────────────
            //
            // Three separate routes, checked separately, because they are three
            // separate lines and the bug was that all three named one concrete type.
            string click = Body(player, "private void HandleMouseClick()");

            check(click.Length > 0, "PlayerController.HandleMouseClick is where it is expected to be");
            check(click.Contains("GetComponentInParent<BossController>"),
                  "clicking the King selects him");

            string best = Body(player, "private ICombatTarget GetBestMonster()");

            check(best.Length > 0, "PlayerController.GetBestMonster is where it is expected to be");
            check(best.Contains("FindObjectsByType<BossController>"),
                  "auto-mode considers the King -- an arena with only a boss in it read as empty");

            string miss = Body(player, "private void FindNearMiss(");

            check(miss.Length > 0, "PlayerController.FindNearMiss is where it is expected to be");
            check(miss.Contains("GetComponentInParent<BossController>"),
                  "click-assist brushes past him too, or he is harder to hit than a goblin");

            // ── And he can walk ───────────────────────────────────────────────
            //
            // Chase() has always existed and opens with a null check on an agent
            // nothing ever added: the recipe places a bare GameObject, and EnsureBody
            // destroys the agents that arrive on the donor rig. So the King fought
            // every encounter from one spot.
            check(boss.Contains("EnsureAgent"), "the King is given a NavMeshAgent");

            string start = Body(boss, "private void Start()");

            check(start.Contains("EnsureAgent()"),
                  "and given it on the way in, not in a method nothing calls");

            string update = Body(boss, "private void Update()");

            check(update.Contains("Chase("), "and the fight actually moves him");

            // ── A latecomer does not replay the fight ─────────────────────────
            //
            // RunSchedule consumes every due cast in a while loop, which is right for a
            // frame hitch and catastrophic for somebody joining ninety seconds in --
            // thirty telegraphs in one frame, all landing on the person who just
            // arrived.
            check(boss.Contains("SkipCastsBefore"),
                  "a joiner winds past the casts that already happened rather than replaying them");

            string begin = Body(boss, "private async void Begin()");

            check(begin.Contains("SkipCastsBefore("),
                  "and winds past them at engage, which is the only moment it can");

            check(begin.Contains("ClaimTheGateAsync"),
                  "arriving claims the gate -- a called group never touched the portal");
        }

        /// <summary>
        /// There is a way out that is not dying.
        ///
        /// ══ WHY THIS IS TWO CHECKS AND NOT ONE ════════════════════════════════
        ///
        /// Because moving the player is the easy half and the half that does not
        /// matter. Walking out leaves a live encounter row behind, and until that row
        /// is closed the server refuses every future engage as "already fighting" --
        /// which, from inside the client, is an arena with no boss in it.
        ///
        /// An exit that travelled without telling the server would have fixed the room
        /// and kept the lockout, and it would have looked completely correct.
        /// </summary>
        private static void ThereIsAWayOutThatIsNotDying(Action<bool, string> check, string root)
        {
            string leave = Strip(Read(root, "Assets/Scripts/UI/Core/LeaveRoomButton.cs"));

            check(leave.Length > 0, "LeaveRoomButton.cs is where it is expected to be");
            if (leave.Length == 0) return;

            check(leave.Contains("FleeBossAsync"), "leaving tells the server the fight is over for you");
            check(leave.Contains("EnterMap"),      "and then actually moves you");

            check(leave.Contains("portalOnly"),
                  "it appears in a room you were let into, which is the map's own word for it");

            // Built and never called, for the fifth time. The button is created by the
            // HUD or it does not exist.
            string hud = Strip(Read(root, "Assets/Scripts/UI/Screens/GameHUD.cs"));

            check(hud.Contains("LeaveRoomButton.Attach"),
                  "the HUD builds it -- a button nothing constructs is not an exit");

            // ── Dying closes the fight too ────────────────────────────────────
            //
            // Nothing did. The fail condition is the enrage clock, so the server had no
            // idea the player had fallen over, and the row stayed live with their name
            // on it. That single open row is most of what "the boss is broken" meant.
            string player = Strip(Read(root, "Assets/Scripts/PlayerScripts/PlayerController.cs"));

            // ══ THE CALL SITE, NOT THE METHOD ═════════════════════════════════
            //
            // The first version of this looked for the NAME, and the name is in the
            // file whether or not anything invokes it -- which is the "built and never
            // called" trap the check exists to catch, reproduced inside the check
            // itself. It passed with the call deleted.
            check(Count(player, "LeaveEncounterQuietly") >= 2,
                  "dying in a boss room closes the encounter behind you -- the method " +
                  "exists AND something calls it");
        }

        /// <summary>
        /// The group goes in together, and argues about the loot afterwards.
        ///
        /// Both arrive on the presence poll rather than on polls of their own -- which
        /// is the property worth protecting, because the obvious implementation of
        /// each is a timer, and three timers per client is how a 300-player idle game
        /// becomes a chat server.
        /// </summary>
        private static void AGroupCanDoBothTogether(Action<bool, string> check, string root)
        {
            string call = Strip(Read(root, "Assets/Scripts/UI/Screens/ThroneCallModal.cs"));
            string roll = Strip(Read(root, "Assets/Scripts/UI/Screens/LootRollModal.cs"));

            check(call.Length > 0, "ThroneCallModal.cs is where it is expected to be");
            check(roll.Length > 0, "LootRollModal.cs is where it is expected to be");

            if (call.Length == 0 || roll.Length == 0) return;

            // ── The call ──────────────────────────────────────────────────────
            check(call.Contains("OnPartyChanged"),
                  "the countdown arrives on the party poll, which every client already does");

            // ══ IN BOTH PLACES ════════════════════════════════════════════════
            //
            // Twice, and the count matters. The dialog tick consults it, and so does
            // the poll handler -- because a client whose tab was suspended through the
            // whole countdown never sees the dialog at all, and would be the one person
            // left behind. Checking for the word alone passed with the tick removed,
            // which is exactly the half a watching player experiences.
            check(Count(call, "travelNow") >= 2,
                  "the server's go-now is honoured both while watching the countdown " +
                  "and by a client that missed it entirely");

            check(call.Contains("_handledToken"),
                  "a call is remembered once -- otherwise declining is asked again every poll");

            string presence = Strip(Read(root, "Assets/Scripts/Backend/PresenceSync.cs"));

            check(presence.Contains("FirePartyChanged"),
                  "and something actually raises it");

            // ── The rolls ─────────────────────────────────────────────────────
            check(roll.Contains("AnswerLootRollAsync"),
                  "a choice reaches the server");

            check(!Regex.IsMatch(roll, @"Random\.(Range|value)|UnityEngine\.Random"),
                  "the client does not roll its own dice -- the number comes back from the server");

            check(roll.Contains("OnBossDefeated"),
                  "the window opens off the boss dying rather than a poll running all session");

            foreach (string choice in new[] { "LootRoll.Need", "LootRoll.Greed", "LootRoll.Pass" })
                check(roll.Contains(choice), $"the window offers {choice} from the shared rules");
        }


        /// <summary>
        /// One Goblin King, not a field of them.
        ///
        /// ══ WHAT WENT WRONG ══════════════════════════════════════════════════
        ///
        /// The throne map's defaultMonsterId is goblin_king, because that is what a
        /// player fights there. MonsterSpawner read it as an instruction and produced
        /// Kings without end -- pooled, respawning and population-capped, which is
        /// three things a boss must not be.
        ///
        /// The check is not "the file mentions isBoss". It is that the refusal sits
        /// inside the method the spawn path actually gates on, which is the
        /// difference between a guard and a comment.
        /// </summary>
        private static void SpawnerRefusesBosses(Action<bool, string> check, string root)
        {
            string spawner = Strip(Read(root, "Assets/Scripts/PlayerScripts/MonsterSpawner.cs"));

            check(spawner.Length > 0, "MonsterSpawner.cs is where it is expected to be");
            if (spawner.Length == 0) return;

            string resolve = Body(spawner, "private bool ResolveMonster(");

            check(resolve.Length > 0, "MonsterSpawner.ResolveMonster is where it is expected to be");
            check(Regex.IsMatch(resolve, @"isBoss[^;]*\breturn false\b"),
                  "ResolveMonster refuses a boss");

            // And the spawn path really is gated on it. Without this the guard could
            // be perfect and sit in a method nothing calls.
            string spawn = Body(spawner, "void SpawnMonster()");

            check(spawn.Length > 0, "MonsterSpawner.SpawnMonster is where it is expected to be");
            check(Regex.IsMatch(spawn, @"if\s*\(\s*!\s*ResolveMonster\s*\([^)]*\)\s*\)\s*return"),
                  "SpawnMonster gives up when ResolveMonster refuses");
        }

        /// <summary>
        /// Somebody constructs the exit portal, and somebody can walk through it.
        ///
        /// The "built and never called" rule, written down: Session.SignedInChanged,
        /// BossPortalController.TryEnterAsync and BossHealthBar each looked finished
        /// and were unreachable, and each cost a playtest to discover.
        /// </summary>
        private static void ExitPortalIsReachable(Action<bool, string> check, string root)
        {
            string portal = Strip(Read(root, "Assets/Scripts/PlayerScripts/ExitPortal.cs"));

            check(portal.Length > 0, "ExitPortal.cs is where it is expected to be");
            if (portal.Length == 0) return;

            check(portal.Contains("EnterMap"), "walking through an ExitPortal changes map");
            check(portal.Contains("OnTriggerEnter"), "walking INTO one is enough");

            string player = Strip(Read(root, "Assets/Scripts/PlayerScripts/PlayerController.cs"));

            check(player.Contains("GetComponentInParent<ExitPortal>"),
                  "clicking one works too -- PlayerController resolves it from the ray");
        }

        /// <summary>
        /// The body of one method, by brace matching from its signature.
        ///
        /// A fixed-size window around a match is not good enough and this project has
        /// the scars: a 600-character window once reached into the block above and
        /// passed on a neighbour's guard.
        /// </summary>
        private static string Body(string source, string signature)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            if (at < 0) return "";

            int open = source.IndexOf('{', at);
            if (open < 0) return "";

            int depth = 0;

            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(open, i - open + 1);
            }

            return "";
        }

        /// <summary>
        /// Source with its comments removed.
        ///
        /// Every one of these files argues at length about the thing it does not do,
        /// so a check that could not tell prose from code would pass on the comment
        /// explaining why the code is absent -- which is precisely backwards.
        /// </summary>
        private static string Strip(string source)
        {
            source = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
            source = Regex.Replace(source, @"^\s*///.*$", "", RegexOptions.Multiline);
            source = Regex.Replace(source, @"^\s*//.*$",  "", RegexOptions.Multiline);

            return source;
        }

        /// <summary>
        /// How many times a fragment appears.
        ///
        /// Because "the file mentions it" is the weakest possible check and this
        /// project has been fooled by it repeatedly: a declaration and a call site read
        /// identically to Contains, so a check for the name passes on a method nothing
        /// invokes.
        /// </summary>
        private static int Count(string haystack, string needle)
        {
            int found = 0;

            for (int at = haystack.IndexOf(needle, StringComparison.Ordinal); at >= 0;
                 at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            {
                found++;
            }

            return found;
        }

        private static string Read(string root, string relative)
        {
            string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
    }
}
