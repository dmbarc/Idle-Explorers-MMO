using System;

namespace IdleExplorers.Rules
{
    /// <summary>How well one timed input went.</summary>
    public enum MinigameGrade
    {
        /// <summary>Nothing was attempted. The default, and the honest one for AFK.</summary>
        None,

        /// <summary>Attempted and missed. Costs nothing -- see Minigame.</summary>
        Miss,

        /// <summary>Inside the window.</summary>
        Good,

        /// <summary>Inside the tight middle of the window.</summary>
        Perfect,
    }

    /// <summary>
    /// What a minigame is worth.
    ///
    /// ══ THE CLIENT REPORTS A GRADE, NEVER A REWARD ════════════════════════════════
    ///
    /// "I hit perfect" is the most a client may say. It may not say "so give me nine
    /// ore" -- the conversion lives here, on both sides, and the server applies it to
    /// its own action count.
    ///
    /// That distinction is the whole design. A scripted client can claim perfect on
    /// every action, and the honest thing to do is state what that buys rather than
    /// pretend it is preventable: exactly the design cap, and not one ore more. Any
    /// timing game has that ceiling; the only choice is whether you know what it is.
    ///
    /// ══ WHY A MISS COSTS NOTHING ══════════════════════════════════════════════════
    ///
    /// A minigame that punishes you for playing badly is a minigame that punishes you
    /// for playing at all -- and this is an idle game, where the baseline is not
    /// playing. Missing yields the AFK rate, which is what you would have got by
    /// leaving. The floor is the thing that makes it optional.
    ///
    /// ══ WHY THE CAP IS WHERE IT IS ════════════════════════════════════════════════
    ///
    /// Sixty per cent over the offline rate is roughly what active play already pays
    /// (1.0 against 0.6). So a perfect minigame run is worth about as much again as
    /// simply being present -- enough that sitting at the rock beats leaving it, not
    /// so much that leaving it feels like a mistake.
    /// </summary>
    public static class Minigame
    {
        /// <summary>The most a perfect run may multiply the ACTIVE rate by.</summary>
        public const float MaxMultiplier = 1.6f;

        /// <summary>What a miss pays, as a fraction of the active rate.</summary>
        public const float MissMultiplier = 1.0f;

        public const float GoodMultiplier    = 1.25f;
        public const float PerfectMultiplier = 1.6f;

        /// <summary>
        /// Most graded actions one report may carry.
        ///
        /// A human strikes a rock every three seconds; a batch of sixty is three
        /// minutes of play. Anything larger is a client claiming more actions than
        /// the window it is reporting could contain, and the server clamps to what it
        /// independently computed anyway -- this only bounds the request.
        /// </summary>
        public const int MaxGradesPerReport = 60;

        /// <summary>The multiplier one grade earns.</summary>
        public static float MultiplierFor(MinigameGrade grade) => grade switch
        {
            MinigameGrade.Perfect => PerfectMultiplier,
            MinigameGrade.Good    => GoodMultiplier,
            MinigameGrade.Miss    => MissMultiplier,

            // Not played at all. Distinct from a miss on purpose: a player who never
            // touched the minigame should not be recorded as having tried and failed.
            _ => MissMultiplier,
        };

        /// <summary>
        /// The bonus actions a run of grades is worth, over what the window already paid.
        ///
        /// ══ WHY IT IS A BONUS RATHER THAN A REPLACEMENT ═══════════════════════════
        ///
        /// Settlement has already decided how many actions the elapsed time produced.
        /// This adds to that, which means the minigame cannot make time run faster --
        /// it can only make a given stretch of time more productive, and only up to
        /// the cap.
        ///
        /// Replacing the count instead would hand the client a way to state its own
        /// action total, which is the one thing it must never do.
        /// </summary>
        /// <param name="actionsSettled">What the window paid, from the server's clock.</param>
        /// <param name="grades">One per action the player graded, in any order.</param>
        public static long BonusActions(long actionsSettled, ReadOnlySpan<MinigameGrade> grades)
        {
            if (actionsSettled <= 0L || grades.Length == 0) return 0L;

            // Never more grades than actions. A client reporting a hundred perfect
            // strikes for a window that produced ten gets ten -- the server's count
            // is the ceiling, always, and the report only says how WELL those ten
            // went.
            int counted = (int)Math.Min(actionsSettled, grades.Length);

            double total = 0d;

            for (int i = 0; i < counted; i++)
                total += MultiplierFor(grades[i]);

            // The graded actions are worth their multiplier; the ungraded remainder is
            // worth what it already was. Only the DIFFERENCE is the bonus, because the
            // base was paid by settlement.
            double bonus = total - counted;

            // Clamped against the cap over the whole run, not per action, so a client
            // cannot exceed it by reporting more grades than it played.
            double ceiling = counted * (MaxMultiplier - 1d);

            return (long)Math.Floor(Math.Max(0d, Math.Min(bonus, ceiling)));
        }

        /// <summary>
        /// Parses a grade from the wire.
        ///
        /// Unknown strings become None rather than throwing. A client sending
        /// "transcendent" is either an old build or a probe, and neither is worth a
        /// five hundred -- it simply earns nothing.
        /// </summary>
        public static MinigameGrade Parse(string grade) => grade switch
        {
            "perfect" => MinigameGrade.Perfect,
            "good"    => MinigameGrade.Good,
            "miss"    => MinigameGrade.Miss,
            _         => MinigameGrade.None,
        };

        // ── The timing windows ────────────────────────────────────────────────
        //
        // Shared because the CLIENT decides the grade from them and the server has to
        // know what a grade means. They are also the numbers that make the game feel
        // fair or unfair, and a player who learns the rhythm on one node should find
        // the same rhythm at the next one.

        /// <summary>Fraction of the action's duration that counts as a hit.</summary>
        public const float GoodWindow = 0.22f;

        /// <summary>Fraction that counts as perfect. Inside the good window.</summary>
        public const float PerfectWindow = 0.08f;

        /// <summary>
        /// Grades an input by how far it landed from the target, as a fraction of the
        /// action's duration.
        ///
        /// Symmetric: early and late are the same mistake. Anything else teaches
        /// players to lean one way, which is a rhythm game's version of a bug.
        /// </summary>
        public static MinigameGrade GradeFor(float distanceFromTarget)
        {
            float off = Math.Abs(distanceFromTarget);

            if (off <= PerfectWindow * 0.5f) return MinigameGrade.Perfect;
            if (off <= GoodWindow * 0.5f)    return MinigameGrade.Good;

            return MinigameGrade.Miss;
        }
    }
}
