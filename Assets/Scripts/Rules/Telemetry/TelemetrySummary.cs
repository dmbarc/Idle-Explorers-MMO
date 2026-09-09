using System;
using System.Collections.Generic;

namespace IdleExplorers.Rules
{
    /// <summary>
    /// The questions an export is opened to answer.
    ///
    /// ══ WHY THIS IS IN THE RULES TREE AND NOT IN THE EDITOR WINDOW ════════════════
    ///
    /// Because it is arithmetic over data, and arithmetic that lives in an EditorWindow
    /// can only be checked by opening Unity and looking at it. Every one of these
    /// numbers is a pure function of a list of records, so every one of them is
    /// testable in milliseconds -- and a funnel that quietly reads zero because a stage
    /// name has a typo is exactly the bug nobody notices by looking.
    ///
    /// The window does layout. This does counting.
    ///
    /// ══ WHY EVERYTHING COUNTS CHARACTERS, NOT ROWS ════════════════════════════════
    ///
    /// A funnel stage counting rows says "there were 40,000 settlements", which measures
    /// how long people idled. Counting distinct characters says "eleven people crafted
    /// something", which is the question. One heavy player must not be able to look like
    /// a retained cohort.
    /// </summary>
    public static class TelemetrySummary
    {
        /// <summary>How many distinct characters reached each stage, in order.</summary>
        public static FunnelResult[] Funnel(IReadOnlyList<TelemetryRecord> records)
        {
            var stages = TelemetryEvents.Funnel;
            var result = new FunnelResult[stages.Length];

            for (int i = 0; i < stages.Length; i++)
            {
                var reached = new HashSet<string>(StringComparer.Ordinal);

                foreach (var record in Safe(records))
                {
                    if (!stages[i].Matches(record)) continue;

                    string who = Who(record);
                    if (who.Length > 0) reached.Add(who);
                }

                result[i] = new FunnelResult(stages[i].Label, reached.Count);
            }

            return result;
        }

        /// <summary>
        /// Which classes people actually pick.
        ///
        /// Counted from character creation rather than from the characters that still
        /// exist, because a class people try and delete is a finding, and a snapshot of
        /// live characters hides it perfectly.
        /// </summary>
        public static Tally[] ClassDistribution(IReadOnlyList<TelemetryRecord> records)
        {
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);

            foreach (var record in Safe(records))
            {
                if (record.eventName != TelemetryEvents.CharacterCreated) continue;

                string classId = record.Get("classId");

                Bump(counts, classId.Length == 0 ? "(none)" : classId, 1L);
            }

            return Ranked(counts);
        }

        /// <summary>
        /// Hours of settled time per skill.
        ///
        /// From the server's own elapsed, which is the span the payout was computed
        /// over -- so this is time the game actually paid for, rather than time a client
        /// claimed to have had a window open.
        /// </summary>
        public static Tally[] SecondsPerSkill(IReadOnlyList<TelemetryRecord> records)
        {
            var seconds = new Dictionary<string, long>(StringComparer.Ordinal);

            foreach (var record in Safe(records))
            {
                if (record.eventName != TelemetryEvents.Settled) continue;

                // Falls back to the activity kind, so a settlement that predates the
                // skill field still lands somewhere countable rather than vanishing.
                string skill = record.Get("skill");
                if (skill.Length == 0) skill = record.Get("kind");
                if (skill.Length == 0) continue;

                Bump(seconds, skill, record.GetLong("elapsed"));
            }

            return Ranked(seconds);
        }

        /// <summary>
        /// How the boss fights went.
        ///
        /// Mean clear time counts CLEARS ONLY. Averaging wipes in would make a boss
        /// nobody beats look fast, because a wipe ends at the enrage timer and a clear
        /// can take longer than one.
        /// </summary>
        public static BossResult BossFights(IReadOnlyList<TelemetryRecord> records)
        {
            long attempts = 0L, clears = 0L, clearSeconds = 0L;

            foreach (var record in Safe(records))
            {
                if (record.eventName != TelemetryEvents.BossEnded) continue;

                attempts++;

                if (record.Get("won") != "true") continue;

                clears++;
                clearSeconds += record.GetLong("seconds");
            }

            double mean = clears > 0L ? (double)clearSeconds / clears : 0d;

            return new BossResult(attempts, clears, mean);
        }

        /// <summary>Every event name and how many rows carry it. The orientation view.</summary>
        public static Tally[] EventCounts(IReadOnlyList<TelemetryRecord> records)
        {
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);

            foreach (var record in Safe(records))
                Bump(counts, record.eventName ?? "", 1L);

            return Ranked(counts);
        }

        /// <summary>
        /// What the security stream saw, worst first.
        ///
        /// Separate from EventCounts on purpose: mixed together, the security signal is
        /// a thousandth of the volume and sorts off the bottom of the screen.
        /// </summary>
        public static Tally[] SecurityCounts(IReadOnlyList<TelemetryRecord> records)
        {
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);

            foreach (var record in Safe(records))
            {
                if (record.stream != TelemetryStreams.Security) continue;

                Bump(counts, record.eventName ?? "", 1L);
            }

            return Ranked(counts);
        }

        /// <summary>
        /// Every distinct character in the export, for the filter dropdown.
        ///
        /// Name and id together, because two people will call a character Bricta and a
        /// filter that silently merges them is worse than one that shows both.
        /// </summary>
        public static string[] Characters(IReadOnlyList<TelemetryRecord> records)
        {
            var seen = new SortedSet<string>(StringComparer.Ordinal);

            foreach (var record in Safe(records))
            {
                if (string.IsNullOrEmpty(record.characterId)) continue;

                string name = string.IsNullOrEmpty(record.characterName)
                    ? "(unnamed)"
                    : record.characterName;

                seen.Add($"{name} · {Short(record.characterId)}");
            }

            var list = new string[seen.Count];
            seen.CopyTo(list);

            return list;
        }

        /// <summary>First eight characters of an id. Enough to disambiguate, short enough to read.</summary>
        public static string Short(string id) =>
            string.IsNullOrEmpty(id) ? "" : (id.Length <= 8 ? id : id.Substring(0, 8));

        // ── Machinery ─────────────────────────────────────────────────────────

        /// <summary>
        /// Who a row is about.
        ///
        /// The character where there is one, the account otherwise. Falling back to the
        /// account matters for the security stream, which is mostly account-level -- a
        /// funnel dropping those rows would report nobody was ever refused anything.
        /// </summary>
        private static string Who(TelemetryRecord record) =>
            !string.IsNullOrEmpty(record.characterId) ? record.characterId
                                                      : record.accountId ?? "";

        private static IEnumerable<TelemetryRecord> Safe(IReadOnlyList<TelemetryRecord> records)
        {
            if (records == null) yield break;

            foreach (var record in records)
                if (record != null) yield return record;
        }

        private static void Bump(Dictionary<string, long> into, string key, long by) =>
            into[key] = into.TryGetValue(key, out long running) ? running + by : by;

        /// <summary>
        /// Biggest first, then alphabetically.
        ///
        /// The tiebreak is not cosmetic: without it two runs over the same data can
        /// order equal counts differently, and a table that reshuffles between refreshes
        /// is one nobody trusts.
        /// </summary>
        private static Tally[] Ranked(Dictionary<string, long> counts)
        {
            var list = new List<Tally>(counts.Count);

            foreach (var entry in counts) list.Add(new Tally(entry.Key, entry.Value));

            list.Sort((a, b) => a.Count != b.Count ? b.Count.CompareTo(a.Count)
                                                   : string.CompareOrdinal(a.Key, b.Key));

            return list.ToArray();
        }
    }

    public readonly struct Tally
    {
        public readonly string Key;
        public readonly long   Count;

        public Tally(string key, long count) { Key = key; Count = count; }
    }

    public readonly struct FunnelResult
    {
        public readonly string Label;
        public readonly int    Characters;

        public FunnelResult(string label, int characters) { Label = label; Characters = characters; }
    }

    public readonly struct BossResult
    {
        public readonly long   Attempts;
        public readonly long   Clears;
        public readonly double MeanClearSeconds;

        public BossResult(long attempts, long clears, double meanClearSeconds)
        {
            Attempts         = attempts;
            Clears           = clears;
            MeanClearSeconds = meanClearSeconds;
        }

        /// <summary>Clears over attempts, 0-1. Zero when nobody has tried.</summary>
        public double WinRate => Attempts > 0L ? (double)Clears / Attempts : 0d;
    }
}
