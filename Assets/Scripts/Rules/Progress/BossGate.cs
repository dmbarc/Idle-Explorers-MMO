namespace IdleExplorers.Rules
{
    /// <summary>
    /// What it costs to reach the Goblin King.
    ///
    /// ══ WHY THIS NUMBER IS IN THE SHARED TREE ═════════════════════════════════════
    ///
    /// Because it was in two places and they were free to disagree. The client read a
    /// constant in LocalBackend and the server read one in BossEndpoints — same value,
    /// no connection between them, and nothing that would fail if somebody changed one.
    ///
    /// The failure that arrangement produces is quiet and specific: a portal that says
    /// OPEN and an engage call that answers 403, or a portal that says "412 / 1000"
    /// while the server has already decided you may pass. Both read as a broken portal
    /// rather than as two numbers drifting apart.
    ///
    /// One constant, compiled into both, is the whole point of the shared rules tree.
    ///
    /// ══ WHY ACTIVE KILLS ONLY ═════════════════════════════════════════════════════
    ///
    /// AFK kills are tracked and do not count. The gate exists so that somebody who
    /// reaches the King has actually played the game rather than left it running, and
    /// an idle game that lets you idle past its own gate has no gate.
    /// </summary>
    public static class BossGate
    {
        /// <summary>The monster whose active kills open the portal.</summary>
        public const string Monster = "goblin";

        /// <summary>
        /// How many active kills the portal wants.
        ///
        /// A hundred, not a thousand. The thousand was chosen before anyone had played
        /// the loop end to end; measured against the real kill rate it put the first
        /// boss attempt out of reach of a first session, which is the wrong place for
        /// the only piece of content the playtest is meant to reach.
        /// </summary>
        public const long RequiredActiveKills = 100L;

        /// <summary>Whether this many active kills is enough.</summary>
        public static bool IsOpen(long activeKills) => activeKills >= RequiredActiveKills;

        /// <summary>How many more are needed. Never negative.</summary>
        public static long Remaining(long activeKills) =>
            activeKills >= RequiredActiveKills ? 0L : RequiredActiveKills - activeKills;
    }
}
