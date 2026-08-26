using System;

namespace IdleExplorers.Rules
{
    /// <summary>
    /// How long it takes to kill something, and therefore how many die in an hour.
    ///
    /// ══ WHAT THIS REPLACES ════════════════════════════════════════════════════════
    ///
    /// Offline combat used to pay out on
    ///
    ///     killsPerHour = max(1, characterLevel / monsterLevel * 30)
    ///
    /// which is a placeholder wearing a formula's clothes. It ignores the monster's
    /// health, the character's damage, their weapon, their gear and every stat in the
    /// game — a naked level 20 and a fully-equipped level 20 killed goblins at exactly
    /// the same rate, and levelling past a monster made it faster to kill in a way that
    /// had no upper bound.
    ///
    /// Time-to-kill from real damage against real health is the honest version, and it
    /// is also the version the server can defend: DPS comes from a stat snapshot frozen
    /// at the start of the window, so swapping gear mid-fight changes nothing that has
    /// already been paid.
    ///
    /// ══ WHY THE CLAMPS ════════════════════════════════════════════════════════════
    ///
    /// This function's output divides into a time window. A DPS of zero is an infinite
    /// time-to-kill, which is merely wrong; a monster with zero health is an infinite
    /// KILL RATE, which mints loot. Content is hand-authored JSON and both are one
    /// typo away.
    /// </summary>
    public static class CombatMath
    {
        /// <summary>Weakest damage output that still counts as fighting.</summary>
        public const double MinDps = 0.1d;

        /// <summary>Frailest a monster may be. Guards a missing or zero maxHp.</summary>
        public const double MinMonsterHp = 1d;

        /// <summary>
        /// Walking to the next spawn and waiting for it, per kill.
        ///
        /// Not decoration: without it, a character who out-levels a zone approaches an
        /// infinite kill rate as time-to-kill approaches zero. This is the term that
        /// makes a starter zone stop being worth farming, and it is why the number is
        /// a floor rather than an average.
        /// </summary>
        public const float MinTravelAndRespawnSeconds = 1.5f;

        /// <summary>
        /// The hard ceiling on farming, derived rather than declared.
        ///
        /// It is 2,400 an hour, and it comes entirely from the travel floor above --
        /// however absurd the damage, a kill still costs a walk. Writing a second,
        /// independent ceiling here was tempting and wrong: at any value above 2,400
        /// it can never bind, which makes it a comment pretending to be code, and at
        /// any value below it, it silently overrides the travel model the balance is
        /// actually built on.
        /// </summary>
        public static float MaxKillsPerHour => 3600f / MinTravelAndRespawnSeconds;

        /// <summary>Seconds to bring one monster down, ignoring travel.</summary>
        public static double TimeToKill(double monsterMaxHp, double dps) =>
            Math.Max(MinMonsterHp, monsterMaxHp) / Math.Max(MinDps, dps);

        /// <summary>
        /// Wall-clock seconds one kill occupies: the fight, plus getting to the fight.
        ///
        /// This is deliberately the same shape as a gathering node's secondsPerAction,
        /// so combat settles through the identical integral. A kill is an action.
        /// </summary>
        public static float SecondsPerKill(double monsterMaxHp, double dps,
                                           float travelAndRespawnSeconds)
        {
            double each = TimeToKill(monsterMaxHp, dps)
                        + Math.Max(MinTravelAndRespawnSeconds, travelAndRespawnSeconds);

            return (float)each;
        }

        /// <summary>Kills an hour at this damage against this monster.</summary>
        public static float KillsPerHour(double monsterMaxHp, double dps,
                                         float travelAndRespawnSeconds) =>
            3600f / SecondsPerKill(monsterMaxHp, dps, travelAndRespawnSeconds);
    }
}
