using System;
using System.Collections.Generic;
using System.Reflection;
using IdleExplorers.Rules;

namespace IdleExplorersTests
{
    /// <summary>
    /// The export format, and the arithmetic done to it.
    ///
    /// ══ WHY THE SHAPE IS CHECKED BY REFLECTION ════════════════════════════════════
    ///
    /// TelemetryRecord is written by System.Text.Json on the server and read by
    /// JsonUtility in the editor. JsonUtility's failure mode is the dangerous one: given
    /// a field it cannot handle it does not throw, it returns the default. A Dictionary
    /// field would load as empty on every row of every export, forever, and look exactly
    /// like a playtest where nobody did anything.
    ///
    /// So the type is checked against what JsonUtility can actually carry, rather than
    /// against what somebody remembered when they wrote it. This is the same class of
    /// bug the API wire shapes were designed around, in the one place the API's own
    /// tests do not reach.
    /// </summary>
    internal static class TelemetryChecks
    {
        internal static void Run(Action<bool, string> check)
        {
            Console.WriteLine("Telemetry export");

            Shape(check);
            Funnel(check);
            Skills(check);
            Boss(check);
            Classes(check);
            Filtering(check);
        }

        // ── The wire shape ────────────────────────────────────────────────────

        /// <summary>
        /// Types JsonUtility round-trips. Anything else is a silent default.
        ///
        /// Deliberately short. The point is not to enumerate everything Unity supports,
        /// it is to keep the export to the boring subset that cannot surprise anybody.
        /// </summary>
        private static readonly HashSet<Type> Carryable = new()
        {
            typeof(string), typeof(long), typeof(int), typeof(bool), typeof(float), typeof(double),
        };

        private static void Shape(Action<bool, string> check)
        {
            FieldInfo[] fields = typeof(TelemetryRecord)
                .GetFields(BindingFlags.Public | BindingFlags.Instance);

            check(fields.Length > 0, "TelemetryRecord exposes public fields, which is what JsonUtility reads");

            foreach (FieldInfo field in fields)
            {
                Type type = field.FieldType;

                bool ok = Carryable.Contains(type) || type == typeof(TelemetryPair[]);

                check(ok, $"TelemetryRecord.{field.Name} is a type JsonUtility can carry " +
                          $"(it is {type.Name})");
            }

            // Properties are invisible to JsonUtility. Having some is fine -- Get and
            // OccurredAtUtc are conveniences -- but a field turned into a property is a
            // column that silently stops being exported.
            foreach (string required in new[] { "stream", "id", "eventName", "accountId",
                                                "characterId", "characterName", "occurredAt",
                                                "payload" })
            {
                check(typeof(TelemetryRecord).GetField(required) != null,
                      $"TelemetryRecord.{required} is a FIELD, not a property");
            }

            foreach (FieldInfo field in typeof(TelemetryPair)
                         .GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                check(field.FieldType == typeof(string),
                      $"TelemetryPair.{field.Name} is a string");
            }

            // Every funnel stage names an event, and the constants are the only place
            // those names are written. A stage matching nothing reads as a stage nobody
            // reached, which is the one wrong answer that looks right.
            foreach (var stage in TelemetryEvents.Funnel)
            {
                check(!string.IsNullOrEmpty(stage.Event),
                      $"funnel stage '{stage.Label}' names an event");

                check(!string.IsNullOrEmpty(stage.Label),
                      "every funnel stage has a label to print");
            }
        }

        // ── The arithmetic ────────────────────────────────────────────────────

        private static TelemetryRecord Row(string stream, string eventName, string characterId,
                                           params (string, string)[] payload)
        {
            var pairs = new TelemetryPair[payload.Length];

            for (int i = 0; i < payload.Length; i++)
                pairs[i] = new TelemetryPair { k = payload[i].Item1, v = payload[i].Item2 };

            return new TelemetryRecord
            {
                stream        = stream,
                id            = 1L,
                eventName     = eventName,
                accountId     = "account-" + characterId,
                characterId   = characterId,
                characterName = characterId.ToUpperInvariant(),
                occurredAt    = "2026-08-27T12:00:00.0000000Z",
                payload       = pairs,
            };
        }

        private static int Reached(FunnelResult[] funnel, string label)
        {
            foreach (var stage in funnel)
                if (stage.Label == label) return stage.Characters;

            return -1;
        }

        private static void Funnel(Action<bool, string> check)
        {
            var records = new List<TelemetryRecord>
            {
                Row("gameplay", TelemetryEvents.CharacterCreated, "ann", ("classId", "warrior")),
                Row("gameplay", TelemetryEvents.CharacterCreated, "bob", ("classId", "mage")),
                Row("gameplay", TelemetryEvents.CharacterCreated, "cat", ("classId", "warrior")),

                // Ann mines twice. Two rows, one character -- the whole reason the
                // funnel counts characters rather than events.
                Row("gameplay", TelemetryEvents.Settled, "ann", ("kind", "gather"), ("elapsed", "3600")),
                Row("gameplay", TelemetryEvents.Settled, "ann", ("kind", "gather"), ("elapsed", "3600")),
                Row("gameplay", TelemetryEvents.Settled, "bob", ("kind", "gather"), ("elapsed", "1800")),

                // Only Ann crafts.
                Row("gameplay", TelemetryEvents.Settled, "ann", ("kind", "craft"), ("elapsed", "600")),

                Row("gameplay", TelemetryEvents.BossUnlocked, "ann"),
            };

            FunnelResult[] funnel = TelemetrySummary.Funnel(records);

            check(Reached(funnel, "Created")     == 3, "three characters were created");
            check(Reached(funnel, "Gathered")    == 2, "two of them gathered, counted once each");
            check(Reached(funnel, "Crafted")     == 1, "one of them crafted");
            check(Reached(funnel, "Portal open") == 1, "one of them opened the portal");
            check(Reached(funnel, "Boss engaged") == 0, "nobody engaged the boss");

            check(funnel.Length == TelemetryEvents.Funnel.Length,
                  "the funnel reports every stage, including the empty ones");

            // The stage filter is the load-bearing part: without it, "Crafted" would
            // match every settled row and report two.
            check(Reached(funnel, "Crafted") != Reached(funnel, "Gathered"),
                  "gather and craft are told apart by the payload, not the event name");

            check(TelemetrySummary.Funnel(null).Length == TelemetryEvents.Funnel.Length,
                  "a null list still reports every stage at zero");
        }

        private static void Skills(Action<bool, string> check)
        {
            var records = new List<TelemetryRecord>
            {
                Row("gameplay", TelemetryEvents.Settled, "ann", ("skill", "mining"),   ("elapsed", "3600")),
                Row("gameplay", TelemetryEvents.Settled, "bob", ("skill", "mining"),   ("elapsed", "1800")),
                Row("gameplay", TelemetryEvents.Settled, "ann", ("skill", "smithing"), ("elapsed", "600")),

                // No skill field: falls back to the activity kind rather than vanishing.
                Row("gameplay", TelemetryEvents.Settled, "cat", ("kind", "combat"),    ("elapsed", "120")),

                // Not a settlement. Must not be counted as time.
                Row("gameplay", "map_enter", "ann", ("map", "goblin_camp"), ("elapsed", "99999")),
            };

            Tally[] skills = TelemetrySummary.SecondsPerSkill(records);

            check(skills.Length == 3, "three things were worked");
            check(skills[0].Key == "mining" && skills[0].Count == 5400L,
                  "mining sums across characters and sorts first");

            long total = 0L;
            foreach (var skill in skills) total += skill.Count;

            check(total == 6120L, "only settled time is counted");
        }

        private static void Boss(Action<bool, string> check)
        {
            var records = new List<TelemetryRecord>
            {
                Row("gameplay", TelemetryEvents.BossEnded, "ann", ("won", "true"),  ("seconds", "200")),
                Row("gameplay", TelemetryEvents.BossEnded, "bob", ("won", "true"),  ("seconds", "400")),

                // The wipes are DELIBERATELY not 300s. An earlier version of this test
                // used 300 for both, which made "mean of the clears" and "mean of every
                // attempt" arrive at the same number -- so the assertion below passed
                // against an implementation that averaged the wipes in. A test whose
                // data cannot tell two formulas apart is not testing either of them.
                Row("gameplay", TelemetryEvents.BossEnded, "ann", ("won", "false"), ("seconds", "60")),
                Row("gameplay", TelemetryEvents.BossEnded, "bob", ("won", "false"), ("seconds", "60")),
            };

            BossResult boss = TelemetrySummary.BossFights(records);

            check(boss.Attempts == 4L, "four attempts");
            check(boss.Clears   == 2L, "two clears");
            check(Math.Abs(boss.WinRate - 0.5d) < 0.0001d, "half of them cleared");

            // Clears only: (200 + 400) / 2 = 300. Averaging every attempt would give
            // (200 + 400 + 60 + 60) / 4 = 180, which is the wrong answer this assertion
            // exists to catch.
            check(Math.Abs(boss.MeanClearSeconds - 300d) < 0.0001d,
                  "mean clear time averages clears only (200 and 400, not the 60s wipes)");

            check(TelemetrySummary.BossFights(new List<TelemetryRecord>()).WinRate == 0d,
                  "no attempts is a win rate of zero, not a divide by zero");
        }

        private static void Classes(Action<bool, string> check)
        {
            var records = new List<TelemetryRecord>
            {
                Row("gameplay", TelemetryEvents.CharacterCreated, "ann", ("classId", "warrior")),
                Row("gameplay", TelemetryEvents.CharacterCreated, "bob", ("classId", "warrior")),
                Row("gameplay", TelemetryEvents.CharacterCreated, "cat", ("classId", "mage")),
                Row("gameplay", TelemetryEvents.CharacterCreated, "dan"),
            };

            Tally[] classes = TelemetrySummary.ClassDistribution(records);

            check(classes.Length == 3, "three distinct choices, counting no class as one");
            check(classes[0].Key == "warrior" && classes[0].Count == 2L, "warrior leads with two");

            bool sawNone = false;
            foreach (var entry in classes) if (entry.Key == "(none)") sawNone = true;

            check(sawNone, "a character created with no class is still counted, not dropped");
        }

        private static void Filtering(Action<bool, string> check)
        {
            var records = new List<TelemetryRecord>
            {
                Row("gameplay", "map_enter",              "ann", ("map", "goblin_camp")),
                Row("security", "telemetry_not_reportable", "", ("event", "boss_defeated")),
                Row("security", "telemetry_not_reportable", "", ("event", "grant_gold")),
            };

            Tally[] security = TelemetrySummary.SecurityCounts(records);

            check(security.Length == 1, "the security stream is counted on its own");
            check(security[0].Count == 2L, "both refusals are counted");

            Tally[] all = TelemetrySummary.EventCounts(records);
            check(all.Length == 2, "the event count spans both streams");

            // Security rows have no character. Dropping them from Characters() is
            // correct -- the dropdown filters characters -- but they must still be
            // reachable through the account in the funnel.
            check(TelemetrySummary.Characters(records).Length == 1,
                  "only rows with a character offer a character to filter by");

            check(TelemetrySummary.Short("0123456789abcdef") == "01234567",
                  "an id shortens to eight characters");

            check(TelemetrySummary.Short("") == "" && TelemetrySummary.Short(null) == "",
                  "shortening nothing is nothing rather than a crash");
        }
    }
}
