using System;

namespace IdleExplorers.Rules
{
    /// <summary>
    /// One line of a telemetry export.
    ///
    /// ══ WHY THIS TYPE IS SHARED ═══════════════════════════════════════════════════
    ///
    /// The server writes these lines with System.Text.Json and the Unity explorer reads
    /// them with JsonUtility. Two serialisers, two hand-written shapes, and the failure
    /// mode is the one JsonUtility specialises in: it does not throw on a shape it
    /// cannot read, it hands back an object with every field at its default. An export
    /// that silently loads as ten thousand blank rows is indistinguishable from an
    /// export of ten thousand blank rows.
    ///
    /// So both ends compile the same class, exactly as they compile the same rules. A
    /// field renamed on one side is a field renamed on both.
    ///
    /// ══ WHY EVERY VALUE IS A STRING ═══════════════════════════════════════════════
    ///
    /// The payload of a `settled` event has longs in it; the payload of a `map_enter`
    /// has a map name. JsonUtility has no variant type and no way to express one, so
    /// the choice is a string or a different class per event.
    ///
    /// Strings, and the explorer parses the few numeric fields it actually charts. An
    /// export is not a hot path -- it is read by a person, once, after a playtest.
    ///
    /// ══ WHY NOT A DICTIONARY ══════════════════════════════════════════════════════
    ///
    /// JsonUtility cannot deserialise one, and does not say so. Same constraint that
    /// shaped every API response in this project.
    /// </summary>
    [Serializable]
    public class TelemetryRecord
    {
        /// <summary>"gameplay" or "security". The two streams share a file, never a table.</summary>
        public string stream;

        /// <summary>
        /// The row's own id, within its stream.
        ///
        /// Carried so an export can resume: the next one asks for rows after the
        /// highest id already written, which is exact in a way a timestamp is not --
        /// two rows can share a microsecond, and a re-export that overlaps by a
        /// timestamp double-counts them.
        /// </summary>
        public long id;

        /// <summary>
        /// What happened.
        ///
        /// Spelled `eventName` rather than `event` because `event` is a C# keyword, and
        /// a field only reachable as `@event` is one every future caller has to
        /// remember. The database column is still `event`; this is the wire.
        /// </summary>
        public string eventName;

        public string accountId;
        public string characterId;

        /// <summary>
        /// The character's name, denormalised into every row.
        ///
        /// The explorer has no database to join against, and "filter by character" is
        /// useless when the only handle is a uuid. Costs a few bytes a row and turns a
        /// column nobody can read into the one people filter on first.
        /// </summary>
        public string characterName;

        /// <summary>ISO-8601, UTC. A string because JsonUtility cannot parse a DateTime.</summary>
        public string occurredAt;

        public TelemetryPair[] payload;

        /// <summary>The value for a key, or empty. Linear, because payloads have five fields.</summary>
        public string Get(string key)
        {
            if (payload == null) return "";

            foreach (var pair in payload)
                if (pair != null && pair.k == key) return pair.v ?? "";

            return "";
        }

        public long GetLong(string key) =>
            long.TryParse(Get(key), out long value) ? value : 0L;

        public double GetDouble(string key) =>
            double.TryParse(Get(key), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double value)
                ? value : 0d;

        /// <summary>
        /// When it happened, or DateTime.MinValue if the line did not say.
        ///
        /// Parsed on demand rather than stored, because most rows are never asked --
        /// an explorer filtering to one event out of forty parses forty times fewer
        /// timestamps than a constructor would.
        /// </summary>
        public DateTime OccurredAtUtc =>
            DateTime.TryParse(occurredAt, System.Globalization.CultureInfo.InvariantCulture,
                              System.Globalization.DateTimeStyles.AdjustToUniversal |
                              System.Globalization.DateTimeStyles.AssumeUniversal,
                              out DateTime parsed)
                ? parsed : DateTime.MinValue;
    }

    /// <summary>
    /// The two streams an export can carry.
    ///
    /// One file rather than two, because the interesting question after a playtest is
    /// often "what was this account doing when it got refused", and answering it across
    /// two files means writing a join by hand. They stay separate TABLES, which is where
    /// the separation actually pays -- this is one column in one export.
    /// </summary>
    public static class TelemetryStreams
    {
        public const string Gameplay = "gameplay";
        public const string Security = "security";
    }

    /// <summary>One payload field. An array of these rather than an object -- see above.</summary>
    [Serializable]
    public class TelemetryPair
    {
        public string k;
        public string v;
    }

    /// <summary>
    /// The event names the explorer's summaries are built on.
    ///
    /// Named here rather than as string literals in two codebases, because a funnel
    /// stage that silently matches nothing looks exactly like a funnel stage nobody
    /// reached -- and the second is a finding while the first is a typo.
    /// </summary>
    public static class TelemetryEvents
    {
        public const string CharacterCreated = "char_create";
        public const string Settled          = "settled";
        public const string BossUnlocked     = "boss_unlock";
        public const string BossEngaged      = "boss_engage";
        public const string BossEnded        = "boss_end";
        public const string MinigameGraded   = "minigame_graded";

        /// <summary>A player spent an item whose effect the server owns -- a mystic gem.</summary>
        public const string ItemUsed         = "item_used";

        /// <summary>
        /// The funnel, in order.
        ///
        /// A player counts at a stage if they EVER reached it, not if they reached it
        /// in this order. Ordering the check would drop anyone who crafted before
        /// their first settled gather, which is everybody handed a starting item.
        ///
        /// The last two stages read zero until the boss encounter endpoints exist.
        /// Listed anyway, because a funnel that grows a stage later cannot be compared
        /// against the exports taken before it did.
        /// </summary>
        public static readonly FunnelStage[] Funnel =
        {
            new("Created",      CharacterCreated),
            new("Gathered",     Settled,       "kind", "gather"),
            new("Crafted",      Settled,       "kind", "craft"),
            new("Fought",       Settled,       "kind", "combat"),
            new("Portal open",  BossUnlocked),
            new("Boss engaged", BossEngaged),
            new("Boss cleared", BossEnded,     "won",  "true"),
        };
    }

    /// <summary>
    /// One step of the funnel: an event, and optionally one payload field it must carry.
    ///
    /// The filter exists because the interesting stages are not distinct events. Every
    /// payout is a `settled`, and what separates "has mined" from "has smithed" is the
    /// `kind` field inside it. Emitting `settled_gather` and `settled_craft` instead
    /// would put an enum in the event name, which is how event tables become
    /// unqueryable.
    /// </summary>
    public readonly struct FunnelStage
    {
        public readonly string Label;
        public readonly string Event;

        /// <summary>Payload key that must match, or empty for "any payload".</summary>
        public readonly string RequireKey;
        public readonly string RequireValue;

        public FunnelStage(string label, string @event, string key = "", string value = "")
        {
            Label        = label;
            Event        = @event;
            RequireKey   = key;
            RequireValue = value;
        }

        public bool Matches(TelemetryRecord record)
        {
            if (record == null || record.eventName != Event) return false;

            return RequireKey.Length == 0 || record.Get(RequireKey) == RequireValue;
        }
    }
}
