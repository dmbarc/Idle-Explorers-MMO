using System;
using IdleExplorers.Rules;
using Xunit;

namespace IdleExplorers.Rules.Tests
{
    /// <summary>
    /// What a minigame is worth, and what a perfect liar is worth.
    ///
    /// The honest premise: a scripted client CAN claim perfect on every action. No
    /// timing game prevents that. What can be guaranteed is the ceiling — so most of
    /// these tests are about the ceiling holding rather than about the arithmetic
    /// being pretty.
    /// </summary>
    public class MinigameTests
    {
        private static MinigameGrade[] All(MinigameGrade grade, int count)
        {
            var grades = new MinigameGrade[count];
            Array.Fill(grades, grade);

            return grades;
        }

        // ── What each grade pays ──────────────────────────────────────────────

        /// <summary>
        /// Missing pays what leaving would have. A minigame that punishes bad play
        /// punishes playing at all, and the baseline here is not playing.
        /// </summary>
        [Fact]
        public void MissingCostsNothing()
        {
            Assert.Equal(0L, Minigame.BonusActions(100, All(MinigameGrade.Miss, 100)));
            Assert.Equal(0L, Minigame.BonusActions(100, All(MinigameGrade.None, 100)));
        }

        [Fact]
        public void PlayingWellPaysMoreThanPlayingBadly()
        {
            long good    = Minigame.BonusActions(100, All(MinigameGrade.Good, 100));
            long perfect = Minigame.BonusActions(100, All(MinigameGrade.Perfect, 100));

            Assert.True(good > 0L);
            Assert.True(perfect > good, $"perfect paid {perfect}, good paid {good}");
        }

        [Fact]
        public void APerfectRunPaysTheDesignCap()
        {
            // 100 actions at 1.6x is 160, of which 100 was already paid.
            Assert.Equal(60L, Minigame.BonusActions(100, All(MinigameGrade.Perfect, 100)));
        }

        [Fact]
        public void AMixedRunLandsBetween()
        {
            var mixed = new[]
            {
                MinigameGrade.Perfect, MinigameGrade.Good, MinigameGrade.Miss, MinigameGrade.Perfect,
            };

            // 1.6 + 1.25 + 1.0 + 1.6 = 5.45, minus the 4 already paid, floored.
            Assert.Equal(1L, Minigame.BonusActions(4, mixed));
        }

        // ══ The ceiling ═══════════════════════════════════════════════════════

        /// <summary>
        /// ══ THE ONE THAT MATTERS ══════════════════════════════════════════════
        ///
        /// A client claiming a thousand perfect strikes for a window that produced
        /// ten gets ten actions' worth. The server's own count is the ceiling; the
        /// report only says how WELL those actions went.
        /// </summary>
        [Fact]
        public void MoreGradesThanActionsBuysNothingExtra()
        {
            long honest = Minigame.BonusActions(10, All(MinigameGrade.Perfect, 10));
            long liar   = Minigame.BonusActions(10, All(MinigameGrade.Perfect, 1000));

            Assert.Equal(honest, liar);
        }

        [Fact]
        public void NoRunCanExceedTheCap()
        {
            foreach (int actions in new[] { 1, 7, 100, 5_000 })
            {
                long bonus = Minigame.BonusActions(actions, All(MinigameGrade.Perfect, actions));
                long most  = (long)(actions * (Minigame.MaxMultiplier - 1f));

                Assert.True(bonus <= most,
                            $"{actions} actions paid {bonus}, cap is {most}");
            }
        }

        /// <summary>
        /// The bonus is added to what the window paid, never substituted for it. A
        /// minigame must not be able to make time run faster -- only to make a given
        /// stretch of it more productive.
        /// </summary>
        [Fact]
        public void TheBonusIsRelativeToWhatTheWindowAlreadyPaid()
        {
            long small = Minigame.BonusActions(10,   All(MinigameGrade.Perfect, 10));
            long large = Minigame.BonusActions(1000, All(MinigameGrade.Perfect, 1000));

            // A hundred times the actions is a hundred times the bonus, and no more.
            Assert.Equal(small * 100L, large);
        }

        [Fact]
        public void AWindowThatPaidNothingPaysNoBonus()
        {
            Assert.Equal(0L, Minigame.BonusActions(0,  All(MinigameGrade.Perfect, 50)));
            Assert.Equal(0L, Minigame.BonusActions(-5, All(MinigameGrade.Perfect, 50)));
        }

        [Fact]
        public void NoGradesMeansNoBonus()
        {
            Assert.Equal(0L, Minigame.BonusActions(500, Array.Empty<MinigameGrade>()));
        }

        // ── Grading ───────────────────────────────────────────────────────────

        [Fact]
        public void DeadCentreIsPerfect()
        {
            Assert.Equal(MinigameGrade.Perfect, Minigame.GradeFor(0f));
        }

        /// <summary>
        /// Early and late are the same mistake. Anything else teaches players to lean
        /// one way, which is a rhythm game's version of a bug.
        /// </summary>
        [Fact]
        public void EarlyAndLateAreGradedIdentically()
        {
            foreach (float off in new[] { 0.02f, 0.05f, 0.09f, 0.3f })
                Assert.Equal(Minigame.GradeFor(off), Minigame.GradeFor(-off));
        }

        [Fact]
        public void TheWindowsNestCorrectly()
        {
            // Just inside perfect, just outside it, and well outside both.
            Assert.Equal(MinigameGrade.Perfect, Minigame.GradeFor(Minigame.PerfectWindow * 0.5f));
            Assert.Equal(MinigameGrade.Good,    Minigame.GradeFor(Minigame.PerfectWindow * 0.5f + 0.001f));
            Assert.Equal(MinigameGrade.Good,    Minigame.GradeFor(Minigame.GoodWindow * 0.5f));
            Assert.Equal(MinigameGrade.Miss,    Minigame.GradeFor(Minigame.GoodWindow * 0.5f + 0.001f));
        }

        [Fact]
        public void PerfectIsHarderThanGood()
        {
            Assert.True(Minigame.PerfectWindow < Minigame.GoodWindow);
        }

        // ── The wire ──────────────────────────────────────────────────────────

        [Theory]
        [InlineData("perfect", MinigameGrade.Perfect)]
        [InlineData("good",    MinigameGrade.Good)]
        [InlineData("miss",    MinigameGrade.Miss)]
        public void GradesParseFromTheWire(string wire, MinigameGrade expected)
        {
            Assert.Equal(expected, Minigame.Parse(wire));
        }

        /// <summary>
        /// A client sending something invented is an old build or a probe. Neither is
        /// worth a five hundred; it simply earns nothing.
        /// </summary>
        [Theory]
        [InlineData("transcendent")]
        [InlineData("PERFECT")]
        [InlineData("")]
        [InlineData(null)]
        public void AnInventedGradeEarnsNothing(string wire)
        {
            Assert.Equal(MinigameGrade.None, Minigame.Parse(wire));
            Assert.Equal(0L, Minigame.BonusActions(100, All(Minigame.Parse(wire), 100)));
        }
    }
}
