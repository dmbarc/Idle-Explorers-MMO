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

        private static string Read(string root, string relative)
        {
            string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
    }
}
