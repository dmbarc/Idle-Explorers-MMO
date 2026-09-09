using System;

namespace IdleExplorers.Backend
{
    /// <summary>
    /// The shapes the telemetry endpoint expects.
    ///
    /// ══ WHY THE PAYLOAD IS AN ARRAY OF PAIRS ══════════════════════════════════════
    ///
    /// Because JsonUtility cannot serialise a Dictionary and does not say so — it
    /// writes an empty object and the whole payload arrives blank. Every wire shape in
    /// this project is built around that, and this one is written down again here
    /// because a telemetry payload is exactly the kind of thing somebody would reach
    /// for a dictionary to express.
    ///
    /// The server rebuilds it into a jsonb object on arrival, so the stored row is an
    /// ordinary JSON object that SQL can query with -&gt;&gt;.
    /// </summary>
    public static class Telemetry
    {
        /// <summary>One field of a payload.</summary>
        [Serializable]
        public class Field
        {
            public string k;
            public string v;
        }

        /// <summary>
        /// One thing that happened.
        ///
        /// The field is named `event` on the wire, which is a C# keyword — hence the
        /// escape. Renaming it would be a rename on both sides, and the server's name
        /// is the one the database column uses.
        /// </summary>
        [Serializable]
        public class Event
        {
            public string  @event;
            public string  characterId;
            public Field[] payload;
        }

        /// <summary>A batch. The server caps this at 64 and the client sends no more.</summary>
        [Serializable]
        public class Batch
        {
            public Event[] events;
        }

        /// <summary>What the server did with a batch.</summary>
        [Serializable]
        public class Receipt
        {
            public int accepted;
            public int rejected;
        }

        // ── The names the server will accept ──────────────────────────────────
        //
        // Anything outside this list is refused AND written to security_event, on the
        // reasoning that a client reporting "boss_defeated" is either a bug worth
        // finding or somebody testing what the endpoint takes. Kept here as constants
        // so a typo is a compile error on this side rather than a security row on the
        // other.

        public const string SessionStart = "session_start";
        public const string SessionEnd   = "session_end";
        public const string ScreenOpen   = "screen_open";
        public const string ScreenClose  = "screen_close";
        public const string MapEnter     = "map_enter";
        public const string TutorialStep = "tutorial_step";
        public const string MinigameEnd  = "minigame_result";
        public const string ClientError  = "client_error";
        public const string SettingSet   = "setting_changed";
    }
}
