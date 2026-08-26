using System;

namespace IdleExplorers.Rules
{
    /// <summary>
    /// How fast a character gathers or crafts, and how much they got done.
    ///
    /// ══ WHY THIS IS ONE PLACE ══════════════════════════════════════════════════
    ///
    /// The live tick and offline accrual used to compute this separately, and they
    /// disagreed: AFK gathering ran sixty times worse than its own multiplier claimed,
    /// for a long time, because two formulas in two files had no way to notice they
    /// had drifted. Collapsing them into one definition is what fixed it.
    ///
    /// The server-authoritative design makes that lesson structural. Settlement
    /// integrates elapsed time against these functions to decide what a player
    /// actually earned; the client calls the same functions to animate a progress bar
    /// and predict the next drop. They cannot drift, because there is one copy.
    ///
    /// ══ EVERY CLAMP HERE IS LOAD-BEARING ═══════════════════════════════════════
    ///
    /// The floors are not defensive noise. A zero or negative seconds-per-action is a
    /// division by zero that becomes an infinite reward; an affinity of zero is a
    /// character who never finishes an action. Content is hand-authored JSON, so any
    /// of those numbers can arrive wrong, and the failure mode of an unclamped rate in
    /// a server-authoritative economy is minting.
    /// </summary>
    public static class RateMath
    {
        /// <summary>Slowest an action may be claimed to take. Guards against 0 and negatives.</summary>
        public const float MinSecondsPerAction = 0.05f;

        /// <summary>Floor on any rate multiplier, so a zero cannot divide.</summary>
        public const float MinRateMultiplier = 0.01f;

        /// <summary>
        /// Floor on class skill affinity.
        ///
        /// Affinity DIVIDES the interval, so an affinity approaching zero approaches an
        /// infinite time per action. 0.25 means the worst possible class is four times
        /// slower at a skill, never permanently stuck at it.
        /// </summary>
        public const float MinAffinity = 0.25f;

        /// <summary>
        /// Seconds per action after speed talents and class affinity.
        ///
        /// Both multipliers arrive already resolved, because working them out needs a
        /// talent tree and a stat block that only the host has. Passing the results in
        /// is what keeps this function pure enough to run on the server.
        /// </summary>
        /// <param name="baseSeconds">The authored figure: a node's tick, or a recipe's craft time.</param>
        /// <param name="speedTalentMultiplier">Talent speed factor. Below 1 is faster.</param>
        /// <param name="skillAffinityMultiplier">Class affinity factor. Above 1 is faster.</param>
        public static float AdjustedSeconds(float baseSeconds,
                                            float speedTalentMultiplier,
                                            float skillAffinityMultiplier)
        {
            float seconds  = Math.Max(MinSecondsPerAction, baseSeconds * speedTalentMultiplier);
            float affinity = Math.Max(MinAffinity, skillAffinityMultiplier);

            return Math.Max(MinSecondsPerAction, seconds / affinity);
        }

        /// <summary>Actions completed per hour at this interval and rate.</summary>
        public static float ActionsPerHour(float secondsPerAction, float rateMultiplier)
        {
            float effective = Math.Max(MinRateMultiplier, secondsPerAction)
                            / Math.Max(MinRateMultiplier, rateMultiplier);

            return 3600f / Math.Max(MinRateMultiplier, effective);
        }

        /// <summary>
        /// Wall-clock seconds one action actually takes, after the rate multiplier.
        ///
        /// The inverse of <see cref="ActionsPerHour"/>, exposed because settlement
        /// accumulates PROGRESS rather than seconds. A window can span a change of
        /// rate -- the player closes the tab halfway through, and the second half
        /// accrues at the offline rate -- and carrying leftover seconds across that
        /// boundary would silently value them at the wrong rate. Carrying a fraction
        /// of an action instead is rate-agnostic and cannot drift.
        /// </summary>
        public static double SecondsEach(float secondsPerAction, float rateMultiplier) =>
            3600d / ActionsPerHour(secondsPerAction, rateMultiplier);

        /// <summary>
        /// The offline multiplier after everything that improves idle rate.
        ///
        /// Diligence rather than the AFK talent directly: the stat block already folds
        /// afkRatePercent into diligence, and reading both would pay it twice.
        /// </summary>
        public static float EffectiveAfkRate(float afkRateMultiplier, float diligence) =>
            Math.Max(0f, afkRateMultiplier) * (1f + Math.Max(0f, diligence));

        /// <summary>
        /// How many whole actions fit into a window.
        ///
        /// ══ THE REMAINDER IS THE WHOLE POINT ═══════════════════════════════════
        ///
        /// Settlement runs whenever the client asks, which may be every twenty seconds
        /// or once a day. If each call floored the elapsed time and threw away the
        /// leftover, a player whose client settled often would earn strictly less than
        /// one who settled rarely — and a player settling every second would earn
        /// nothing at all, forever, because no single second completes a three-second
        /// action.
        ///
        /// So the caller carries the remainder forward: this returns both the completed
        /// count and the unspent seconds, and settlement stores the latter. Rewards
        /// then depend only on wall-clock time, never on how chatty the client is.
        /// </summary>
        /// <param name="elapsedSeconds">Window length, plus any seconds carried over.</param>
        /// <param name="secondsPerAction">Already through <see cref="AdjustedSeconds"/>.</param>
        /// <param name="rateMultiplier">Node or recipe rate, times the AFK or active rate.</param>
        /// <param name="remainderSeconds">Seconds left unspent, to carry into the next call.</param>
        public static long ActionsIn(double elapsedSeconds,
                                     float secondsPerAction,
                                     float rateMultiplier,
                                     out double remainderSeconds)
        {
            remainderSeconds = 0d;

            if (elapsedSeconds <= 0d) return 0L;

            float perHour = ActionsPerHour(secondsPerAction, rateMultiplier);
            if (perHour <= 0f) return 0L;

            double secondsEach = 3600d / perHour;
            double completed   = Math.Floor(elapsedSeconds / secondsEach);

            // Guard the cast, not just the arithmetic. A corrupt secondsPerAction of a
            // millionth of a second over a day is 86 billion actions, which overflows
            // an int and, cast blindly, becomes a negative quantity in the inventory.
            if (completed >= long.MaxValue)
            {
                remainderSeconds = 0d;
                return long.MaxValue;
            }

            long actions = (long)completed;
            remainderSeconds = elapsedSeconds - actions * secondsEach;

            return actions;
        }
    }
}
