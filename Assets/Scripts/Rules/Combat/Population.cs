using System;

namespace IdleExplorers.Rules
{
    /// <summary>
    /// How many monsters a map holds, where they stand, and when they come back.
    ///
    /// ══ WHY THESE NUMBERS ARE SHARED ══════════════════════════════════════════════
    ///
    /// The server decides the population and the client draws it, so on the face of it
    /// only the server needs them. Two reasons they live here anyway:
    ///
    ///   · The client has to know the respawn delay to draw a corpse fading rather
    ///     than a monster blinking out. A second copy of that number on the client is
    ///     a corpse that vanishes early or lingers after its replacement has appeared.
    ///   · The spread is what stops two monsters occupying the same ground, and the
    ///     client's placement — dropping a reported position onto the NavMesh — has to
    ///     agree with the server's about how much room each one needs.
    ///
    /// ══ WHY THE CEILING IS LOW ════════════════════════════════════════════════════
    ///
    /// Every monster in the population is drawn by every client in the map, reported in
    /// every poll, and read on every write. Twelve is what the client-side spawner used
    /// as a per-player cap and it looked busy; as a SHARED population it is now twelve
    /// for the whole map rather than twelve each, which is fewer goblins on screen and
    /// more players to fight over them.
    /// </summary>
    public static class Population
    {
        /// <summary>How many live monsters a map is kept topped up to.</summary>
        public const int PerMap = 14;

        /// <summary>
        /// Nothing may exceed this however the content is authored.
        ///
        /// A population is a per-poll payload and a per-frame draw cost, so a map
        /// authored at 500 is not a busy map, it is an unplayable one.
        /// </summary>
        public const int MaxPerMap = 40;

        /// <summary>
        /// Seconds a corpse lies there before its replacement appears.
        ///
        /// Long enough that clearing an area feels like it did something, short enough
        /// that a farmer is never left standing in an empty field. The client fades a
        /// corpse over this window rather than removing it, so the world does not
        /// blink.
        /// </summary>
        public const double RespawnSeconds = 25d;

        /// <summary>
        /// How far from the middle of a map monsters may be placed, in world units.
        ///
        /// The two playable maps are a little over 120 units across at four units a
        /// tile, so this keeps a spawn inside the walls with room to spare. The client
        /// drops the point onto the NavMesh, which is what makes a rough position land
        /// somewhere walkable.
        /// </summary>
        public const float SpawnExtent = 48f;

        /// <summary>Closest two monsters may be placed to one another.</summary>
        public const float MinSeparation = 4f;

        /// <summary>
        /// Clamps an authored or requested population into what the game will honour.
        /// </summary>
        public static int Clamp(int wanted) =>
            wanted <= 0 ? 0 : (wanted > MaxPerMap ? MaxPerMap : wanted);

        /// <summary>
        /// Whether a monster reported dead this long ago should be back.
        ///
        /// Asked by the server to decide what to revive, and by the client to decide
        /// how faded a corpse should be — one number, so the two cannot disagree about
        /// when something is gone.
        /// </summary>
        public static bool ShouldRespawn(double secondsDead) => secondsDead >= RespawnSeconds;

        /// <summary>
        /// How faded a corpse is, 0 just-died to 1 gone.
        ///
        /// Clamped at both ends: a negative age is a clock disagreeing by a fraction of
        /// a second, and it should read as "just died" rather than as "not yet dead".
        /// </summary>
        public static float CorpseFade(double secondsDead)
        {
            if (secondsDead <= 0d) return 0f;
            if (secondsDead >= RespawnSeconds) return 1f;

            return (float)(secondsDead / RespawnSeconds);
        }

        /// <summary>
        /// The most damage one attacker can have done in a window.
        ///
        /// The boss's ceiling, applied to ordinary monsters for the same reason: damage
        /// is REPORTED by the client, and a report with no ceiling is a client deciding
        /// how fast the world dies. The tolerance covers a poll that arrived late.
        ///
        /// Deliberately generous. Being wrong in the strict direction means a player
        /// whose connection hiccuped watches their hits stop landing, and there is
        /// nothing to gain by cheating here — what a kill PAYS still comes from
        /// settlement.
        /// </summary>
        public static double DamageCeiling(double dps, double elapsedSeconds)
        {
            if (dps <= 0d || elapsedSeconds <= 0d) return 0d;

            return dps * (elapsedSeconds + ToleranceSeconds);
        }

        /// <summary>Slack in the damage ceiling, for a poll that arrived late.</summary>
        public const double ToleranceSeconds = 3d;
    }
}
