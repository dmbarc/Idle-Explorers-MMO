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
