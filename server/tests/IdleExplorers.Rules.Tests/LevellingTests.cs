using IdleExplorers.Rules;
using Xunit;

namespace IdleExplorers.Rules.Tests
{
    /// <summary>
    /// Experience to levels.
    ///
    /// Level gates recipes, gear, talent points and skill speed, so the server and the
    /// client have to agree on it exactly. They now do by construction — one function —
    /// but the boundaries are worth pinning anyway, because a curve is the kind of
    /// thing somebody eventually "tunes" without realising what reads it.
    /// </summary>
    public class LevellingTests
    {
        // ── The shape ─────────────────────────────────────────────────────────

        [Fact]
        public void NobodyStartsBelowLevelOne()
        {
            Assert.Equal(1, Levelling.CharacterLevel(0));
            Assert.Equal(1, Levelling.SkillLevel(0));
            Assert.Equal(1, Levelling.CharacterLevel(-5_000));
            Assert.Equal(1, Levelling.SkillLevel(-5_000));
        }

        [Fact]
        public void LevelTwoCostsTheFactor()
        {
            Assert.Equal(83L,  Levelling.CharacterXpFor(2));
            Assert.Equal(100L, Levelling.SkillXpFor(2));

            Assert.Equal(2, Levelling.CharacterLevel(83L));
            Assert.Equal(2, Levelling.SkillLevel(100L));
        }

        [Fact]
        public void TheCurveAndItsInverseAgreeAtEveryLevel()
        {
            for (int level = 1; level <= Levelling.MaxLevel; level++)
            {
                Assert.Equal(level, Levelling.CharacterLevel(Levelling.CharacterXpFor(level)));
                Assert.Equal(level, Levelling.SkillLevel(Levelling.SkillXpFor(level)));
            }
        }

        /// <summary>
        /// ══ THE REGRESSION THIS FILE EXISTS FOR ═══════════════════════════════
        ///
        /// One xp short of a level must NOT be that level.
        ///
        /// The curve used to be computed in float. A float holds about 16 million
        /// integers exactly and this curve reaches roughly 82 million, so past a
        /// certain point the rounding rounded UP — granting the level one xp early at
        /// 429 character boundaries from level 451, and 695 skill boundaries from
        /// level 258. Not a rounding curiosity: it is free progression, it compounds
        /// with every gate that reads level, and it would have differed between the
        /// server and the client the moment they used different runtimes.
        /// </summary>
        [Fact]
        public void OneExperienceShortIsNotTheNextLevel()
        {
            for (int level = 2; level <= Levelling.MaxLevel; level++)
            {
                long characterXp = Levelling.CharacterXpFor(level);
                Assert.Equal(level - 1, Levelling.CharacterLevel(characterXp - 1));

                long skillXp = Levelling.SkillXpFor(level);
                Assert.Equal(level - 1, Levelling.SkillLevel(skillXp - 1));
            }
        }

        /// <summary>The exact cases the float version got wrong, named so a regression is obvious.</summary>
        [Theory]
        [InlineData(16_807_499L, 450)]   // one short of character 451
        [InlineData(17_107_627L, 454)]   // one short of character 455
        [InlineData(82_668_331L, 998)]   // one short of the character cap
        public void TheCharacterBoundariesThatFloatGotWrong(long xp, int expected)
        {
            Assert.Equal(expected, Levelling.CharacterLevel(xp));
        }

        [Theory]
        [InlineData(6_604_899L, 257)]    // one short of skill 258
        [InlineData(6_759_999L, 260)]    // one short of skill 261
        [InlineData(99_600_399L, 998)]   // one short of the skill cap
        public void TheSkillBoundariesThatFloatGotWrong(long xp, int expected)
        {
            Assert.Equal(expected, Levelling.SkillLevel(xp));
        }

        [Fact]
        public void EveryLevelIsReachedExactlyOnce()
        {
            // Walking the whole curve rather than sampling: an off-by-one that skips a
            // level shows up as a level nobody can ever be, which is invisible until a
            // player is the one who cannot be it.
            int last = 1;

            for (int level = 2; level <= Levelling.MaxLevel; level++)
            {
                int reached = Levelling.CharacterLevel(Levelling.CharacterXpFor(level));

                Assert.Equal(last + 1, reached);
                last = reached;
            }
        }

        // ── The cap ───────────────────────────────────────────────────────────

        [Fact]
        public void NothingExceedsTheCap()
        {
            Assert.Equal(Levelling.MaxLevel, Levelling.CharacterLevel(long.MaxValue));
            Assert.Equal(Levelling.MaxLevel, Levelling.SkillLevel(long.MaxValue));
        }

        [Fact]
        public void AskingForXpBeyondTheCapReturnsTheCap()
        {
            Assert.Equal(Levelling.CharacterXpFor(Levelling.MaxLevel),
                         Levelling.CharacterXpFor(50_000));

            Assert.Equal(Levelling.CharacterXpFor(1), Levelling.CharacterXpFor(-10));
        }

        [Fact]
        public void ThereIsNothingLeftToEarnAtTheCap()
        {
            Assert.Equal(0L, Levelling.CharacterXpToNext(Levelling.CharacterXpFor(Levelling.MaxLevel)));
            Assert.Equal(0L, Levelling.SkillXpToNext(Levelling.SkillXpFor(Levelling.MaxLevel)));
        }

        // ── Progress toward the next one ──────────────────────────────────────

        [Fact]
        public void ExperienceToNextCountsDownToZero()
        {
            long atFive   = Levelling.CharacterXpFor(5);
            long atSix    = Levelling.CharacterXpFor(6);

            Assert.Equal(atSix - atFive, Levelling.CharacterXpToNext(atFive));
            Assert.Equal(1L,             Levelling.CharacterXpToNext(atSix - 1));
        }

        [Fact]
        public void ExperienceToNextIsNeverNegative()
        {
            Assert.Equal(83L, Levelling.CharacterXpToNext(-500));
            Assert.True(Levelling.SkillXpToNext(-500) > 0L);
        }

        // ── The shared track ──────────────────────────────────────────────────

        /// <summary>
        /// Character xp is a quarter of ALL skill xp. It used to come from combat kills
        /// alone, so a character who only mined, fished and cooked stayed at level 1
        /// forever — and since talent points come from character level, an entire
        /// playstyle earned none.
        /// </summary>
        [Fact]
        public void EverySkillFeedsTheCharacterTrack()
        {
            Assert.Equal(25L, Levelling.CharacterXpFromSkillXp(100L));
            Assert.Equal(0L,  Levelling.CharacterXpFromSkillXp(0L));
            Assert.Equal(0L,  Levelling.CharacterXpFromSkillXp(-100L));
        }

        /// <summary>
        /// The two divisors differ on purpose. The character curve is shallower to
        /// offset the quartered income, so the two tracks do not diverge into a
        /// level 3 character with a level 40 trade.
        /// </summary>
        [Fact]
        public void TheCharacterTrackKeepsPaceWithASingleSkill()
        {
            long skillXp = Levelling.SkillXpFor(40);
            int  character = Levelling.CharacterLevel(Levelling.CharacterXpFromSkillXp(skillXp));

            Assert.InRange(character, 15, 30);
        }
    }
}
