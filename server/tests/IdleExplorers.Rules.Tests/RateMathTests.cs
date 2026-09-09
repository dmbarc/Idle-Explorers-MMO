using IdleExplorers.Rules;
using Xunit;

namespace IdleExplorers.Rules.Tests
{
    /// <summary>
    /// The rate math settlement is built on.
    ///
    /// These numbers decide what a player earns for a day away, so the tests are
    /// mostly about the boundaries: a hand-authored JSON file can supply a zero or a
    /// negative anywhere, and an unclamped rate in a server-authoritative economy
    /// mints currency rather than merely looking wrong.
    /// </summary>
    public class RateMathTests
    {
        // ── AdjustedSeconds ───────────────────────────────────────────────────

        [Fact]
        public void WithNoBonusesTheAuthoredIntervalSurvivesUntouched()
        {
            Assert.Equal(3f, RateMath.AdjustedSeconds(3f, 1f, 1f), 5);
        }

        [Fact]
        public void SpeedTalentsShortenTheIntervalAndAffinityDividesIt()
        {
            Assert.Equal(2.4f, RateMath.AdjustedSeconds(3f, 0.8f, 1f), 5);
            Assert.Equal(2.0f, RateMath.AdjustedSeconds(3f, 1f, 1.5f), 5);
            Assert.Equal(1.6f, RateMath.AdjustedSeconds(3f, 0.8f, 1.5f), 5);
        }

        /// <summary>
        /// Affinity divides, so an affinity near zero is an action that never finishes.
        /// The floor turns the worst possible class into four times slower rather than
        /// permanently unable.
        /// </summary>
        [Fact]
        public void AZeroAffinityCannotStallTheCharacterForever()
        {
            Assert.Equal(12f, RateMath.AdjustedSeconds(3f, 1f, 0f), 5);
            Assert.Equal(12f, RateMath.AdjustedSeconds(3f, 1f, -5f), 5);
        }

        [Fact]
        public void NoAmountOfSpeedMakesAnActionInstant()
        {
            Assert.Equal(RateMath.MinSecondsPerAction, RateMath.AdjustedSeconds(3f, 0f, 1f), 5);
            Assert.Equal(RateMath.MinSecondsPerAction, RateMath.AdjustedSeconds(3f, 1f, 1000f), 5);
            Assert.Equal(RateMath.MinSecondsPerAction, RateMath.AdjustedSeconds(0f, 1f, 1f), 5);
            Assert.Equal(RateMath.MinSecondsPerAction, RateMath.AdjustedSeconds(-3f, 1f, 1f), 5);
        }

        // ── ActionsPerHour ────────────────────────────────────────────────────

        /// <summary>
        /// The measured baseline the smithing rebalance is priced against: a three
        /// second node at rate 1.0 is 1,200 actions an hour.
        /// </summary>
        [Fact]
        public void TheBaselineNodeYieldsTwelveHundredAnHour()
        {
            Assert.Equal(1200f, RateMath.ActionsPerHour(3f, 1f), 2);
        }

        [Fact]
        public void RateMultipliersScaleTheHourlyLinearly()
        {
            Assert.Equal(1320f, RateMath.ActionsPerHour(3f, 1.1f), 2);   // Fading Hollow
            Assert.Equal(1440f, RateMath.ActionsPerHour(3f, 1.2f), 2);   // Iron Works
            Assert.Equal(600f,  RateMath.ActionsPerHour(3f, 0.5f), 2);
        }

        [Fact]
        public void DegenerateRatesDoNotDivideByZero()
        {
            Assert.True(RateMath.ActionsPerHour(3f, 0f) > 0f);
            Assert.True(RateMath.ActionsPerHour(3f, -1f) > 0f);
            Assert.True(RateMath.ActionsPerHour(0f, 1f) > 0f);
        }

        // ── EffectiveAfkRate ──────────────────────────────────────────────────

        [Fact]
        public void DiligenceRaisesTheOfflineRate()
        {
            Assert.Equal(0.6f, RateMath.EffectiveAfkRate(0.6f, 0f), 5);
            Assert.Equal(0.66f, RateMath.EffectiveAfkRate(0.6f, 0.1f), 5);
        }

        [Fact]
        public void NegativeInputsCannotProduceNegativeAccrual()
        {
            Assert.Equal(0f, RateMath.EffectiveAfkRate(-1f, 0.5f), 5);
            Assert.Equal(0.6f, RateMath.EffectiveAfkRate(0.6f, -2f), 5);
        }

        // ── ActionsIn ─────────────────────────────────────────────────────────

        [Fact]
        public void AWindowYieldsItsWholeActionsAndKeepsTheRest()
        {
            long actions = RateMath.ActionsIn(10d, 3f, 1f, out double left);

            Assert.Equal(3L, actions);
            Assert.Equal(1d, left, 6);
        }

        [Fact]
        public void EmptyOrNegativeWindowsYieldNothing()
        {
            Assert.Equal(0L, RateMath.ActionsIn(0d, 3f, 1f, out _));
            Assert.Equal(0L, RateMath.ActionsIn(-500d, 3f, 1f, out _));
        }

        /// <summary>
        /// ══ THE PROPERTY THE WHOLE DESIGN RESTS ON ═════════════════════════════
        ///
        /// Settlement runs whenever the client asks. If rewards depended on how often
        /// it asked, a chatty client would earn less than a quiet one, and a client
        /// settling every second would earn nothing at all — no single second ever
        /// completes a three second action.
        ///
        /// Carrying the remainder makes the outcome depend only on wall-clock time.
        /// </summary>
        [Fact]
        public void SettlingOftenEarnsExactlyAsMuchAsSettlingOnce()
        {
            const double Window = 3600d;

            long inOneGo = RateMath.ActionsIn(Window, 3f, 1f, out _);

            long piecemeal = 0L;
            double carried = 0d;
            for (int second = 0; second < 3600; second++)
            {
                piecemeal += RateMath.ActionsIn(1d + carried, 3f, 1f, out carried);
            }

            Assert.Equal(1200L, inOneGo);
            Assert.Equal(inOneGo, piecemeal);
        }

        [Fact]
        public void TheSameHoldsForAnAwkwardIntervalThatNeverDividesEvenly()
        {
            const double Window = 5000d;
            const float  Each   = 7.3f;

            long inOneGo = RateMath.ActionsIn(Window, Each, 1f, out _);

            long piecemeal = 0L;
            double carried = 0d;
            for (int chunk = 0; chunk < 500; chunk++)
                piecemeal += RateMath.ActionsIn(10d + carried, Each, 1f, out carried);

            Assert.Equal(inOneGo, piecemeal);
        }

        /// <summary>
        /// The measured figure the economy is balanced against: a full day offline on
        /// the goblin camp tin rock, at rate 1.0 and afk 0.6.
        /// </summary>
        [Fact]
        public void ADayOnTheStarterRockYieldsTheBalancedFigure()
        {
            long actions = RateMath.ActionsIn(24 * 3600d, 3f, 1f * 0.6f, out _);

            Assert.Equal(17_280L, actions);
        }

        /// <summary>
        /// A corrupt interval of a millionth of a second over a day is 86 billion
        /// actions. That overflows an int, and cast blindly it lands in the inventory
        /// as a negative quantity — an item duplication bug wearing a rounding error's
        /// clothes.
        /// </summary>
        [Fact]
        public void AnAbsurdRateSaturatesRatherThanOverflowing()
        {
            long actions = RateMath.ActionsIn(24 * 3600d, 0.000001f, 1_000_000f, out _);

            Assert.True(actions > 0L, "an absurd rate produced a non-positive count");
        }
    }
}
