using System;

namespace IdleExplorers.Rules
{
    /// <summary>
    /// Experience to levels, for characters and for skills.
    ///
    /// ══ WHY THIS IS THE FIRST THING THE SERVER NEEDS ══════════════════════════════
    ///
    /// Level gates almost everything: which recipes are craftable, which gear is
    /// wearable, how many talent points exist, how fast a skill works. So the server
    /// has to compute it, and the client has to compute the same answer or every
    /// tooltip in the game is a guess.
    ///
    /// ══ WHY LEVEL IS NEVER STORED ═════════════════════════════════════════════════
    ///
    /// It is derived, and derived values that get stored get stale.
    /// SaveManager.BackfillCharacterXP exists in this project precisely because level
    /// and xp were both persisted and drifted apart. The database stores xp alone and
    /// this function answers the rest.
    ///
    /// That decision also has a schema consequence worth naming: it would be tidier to
    /// make level a GENERATED column in Postgres, and that would mean writing this
    /// curve a second time in plpgsql. Two implementations of a progression formula is
    /// the exact drift the shared rules tree exists to prevent, so the column does not
    /// exist and the API computes it.
    ///
    /// ══ THE CURVE ═════════════════════════════════════════════════════════════════
    ///
    /// xp for level L is (L-1)^2 * k, inverted as L = 1 + floor(sqrt(xp / k)). An
    /// approximation of the OSRS curve, stretched to a 999 cap.
    ///
    /// Characters and skills use DIFFERENT k -- 83 and 100 -- which is deliberate and
    /// was previously an accident of the two functions living in two files. Character
    /// xp is a quarter of all skill xp, so the shallower character curve keeps the two
    /// tracks from diverging into a character who is level 3 with a level 40 trade.
    /// </summary>
    public static class Levelling
    {
        /// <summary>The cap. Both tracks share it.</summary>
        public const int MaxLevel = 999;

        /// <summary>Divisor for the character curve.</summary>
        public const long CharacterFactor = 83L;

        /// <summary>Divisor for every skill curve.</summary>
        public const long SkillFactor = 100L;

        /// <summary>
        /// Fraction of skill xp that also becomes character xp.
        ///
        /// One rule applied wherever skill xp lands. It used to come from combat kills
        /// alone, so a character who only mined, fished and cooked stayed at level 1
        /// forever -- and since talent points come from character level, an entire
        /// playstyle earned none.
        /// </summary>
        public const long CharacterXpDivisor = 4L;

        public static int  CharacterLevel(long totalXp) => LevelFor(totalXp, CharacterFactor);
        public static long CharacterXpFor(int level)    => XpFor(level, CharacterFactor);

        public static int  SkillLevel(long totalXp) => LevelFor(totalXp, SkillFactor);
        public static long SkillXpFor(int level)    => XpFor(level, SkillFactor);

        /// <summary>Xp still needed for the next character level. Zero at the cap.</summary>
        public static long CharacterXpToNext(long totalXp) =>
            XpToNext(totalXp, CharacterFactor);

        /// <summary>Xp still needed for the next skill level. Zero at the cap.</summary>
        public static long SkillXpToNext(long totalXp) =>
            XpToNext(totalXp, SkillFactor);

        /// <summary>Character xp earned alongside a given amount of skill xp.</summary>
        public static long CharacterXpFromSkillXp(long skillXp) =>
            skillXp <= 0L ? 0L : skillXp / CharacterXpDivisor;

        // ── The curve, once ───────────────────────────────────────────────────

        private static int LevelFor(long totalXp, long factor)
        {
            if (totalXp <= 0L) return 1;

            // double rather than float: at the top of a 999 cap the xp figure is around
            // 82 million, and a float has 24 bits of mantissa -- roughly 16 million.
            // Past that, sqrt lands on the wrong level, which is a level-up that never
            // fires or fires twice.
            double level = 1d + Math.Floor(Math.Sqrt(totalXp / (double)factor));

            return level >= MaxLevel ? MaxLevel : (int)level;
        }

        private static long XpFor(int level, long factor)
        {
            int clamped = (int)RulesMath.Clamp(level, 1L, MaxLevel);
            long steps  = clamped - 1L;

            return steps * steps * factor;
        }

        private static long XpToNext(long totalXp, long factor)
        {
            int level = LevelFor(totalXp, factor);
            if (level >= MaxLevel) return 0L;

            return Math.Max(0L, XpFor(level + 1, factor) - Math.Max(0L, totalXp));
        }
    }
}
