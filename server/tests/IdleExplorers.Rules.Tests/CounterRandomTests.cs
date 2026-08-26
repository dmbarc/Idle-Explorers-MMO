using System.Collections.Generic;
using IdleExplorers.Rules;
using Xunit;

namespace IdleExplorers.Rules.Tests
{
    /// <summary>
    /// The generator the server decides things with.
    ///
    /// Its job is not to be random. Its job is to be REPRODUCIBLE: when a player
    /// disputes a boss kill six weeks later, the encounter's seed and the action's
    /// index have to reproduce the same damage, from a cold start, with no surviving
    /// state. Everything below is a facet of that.
    /// </summary>
    public class CounterRandomTests
    {
        [Fact]
        public void SameSeedProducesTheSameSequence()
        {
            var a = new CounterRandom(20260826);
            var b = new CounterRandom(20260826);

            for (int i = 0; i < 200; i++)
                Assert.Equal(a.Next01(), b.Next01());
        }

        [Fact]
        public void DifferentSeedsDiverge()
        {
            var a = new CounterRandom(1);
            var b = new CounterRandom(2);

            // Not "every draw differs" -- two uniform streams will collide eventually
            // and asserting otherwise would make this test flaky by design.
            int same = 0;
            for (int i = 0; i < 200; i++)
                if (a.Next01() == b.Next01()) same++;

            Assert.True(same < 5, $"streams from different seeds agreed {same} times in 200");
        }

        /// <summary>
        /// The audit property. Reading roll 500 must not require having drawn the
        /// first 499 -- that is what lets a dispute be settled from two numbers in a
        /// database row rather than by replaying an entire fight.
        /// </summary>
        [Fact]
        public void AnyDrawCanBeReproducedWithoutReplayingTheOnesBeforeIt()
        {
            var played = new CounterRandom(777);
            var drawn  = new List<float>();
            for (int i = 0; i < 500; i++) drawn.Add(played.Next01());

            var auditor = new CounterRandom(777);
            for (ulong index = 0; index < 500; index++)
            {
                var atIndex = new CounterRandom(777, index);
                Assert.Equal(drawn[(int)index], atIndex.Next01());
            }

            Assert.Equal(0UL, new CounterRandom(777).Index);
            Assert.Equal(500UL, played.Index);
            Assert.Equal(0UL, auditor.Index);
        }

        [Fact]
        public void IndexAdvancesOncePerDraw()
        {
            var rng = new CounterRandom(5);

            rng.Next01();
            rng.Range(0f, 10f);
            rng.Range(0, 10);
            rng.RangeInclusive(0, 10);

            Assert.Equal(4UL, rng.Index);
        }

        [Fact]
        public void Next01StaysInsideTheUnitInterval()
        {
            var rng = new CounterRandom(99);

            for (int i = 0; i < 20000; i++)
            {
                float v = rng.Next01();
                Assert.True(v >= 0f && v < 1f, $"Next01 returned {v}");
            }
        }

        [Fact]
        public void FloatRangeRespectsItsBounds()
        {
            var rng = new CounterRandom(4242);

            for (int i = 0; i < 20000; i++)
            {
                float v = rng.Range(-3.5f, 7.25f);
                Assert.True(v >= -3.5f && v < 7.25f, $"Range returned {v}");
            }
        }

        [Fact]
        public void IntRangeIsHalfOpenLikeTheFrameworkOne()
        {
            var rng  = new CounterRandom(11);
            var seen = new HashSet<int>();

            for (int i = 0; i < 20000; i++) seen.Add(rng.Range(3, 7));

            Assert.Equal(new HashSet<int> { 3, 4, 5, 6 }, seen);
        }

        /// <summary>
        /// Loot bands in monster_data.json read "minQty 1, maxQty 3" and mean three
        /// possible outcomes. An exclusive upper bound here would quietly make the
        /// best roll on every drop table in the game unreachable.
        /// </summary>
        [Fact]
        public void LongRangeIncludesBothEnds()
        {
            var rng  = new CounterRandom(12345);
            var seen = new HashSet<long>();

            for (int i = 0; i < 20000; i++) seen.Add(rng.RangeInclusive(1, 3));

            Assert.Equal(new HashSet<long> { 1, 2, 3 }, seen);
        }

        [Fact]
        public void EmptyOrInvertedBandsReturnTheLowEndRatherThanThrowing()
        {
            var rng = new CounterRandom(1);

            Assert.Equal(5f, rng.Range(5f, 5f));
            Assert.Equal(5f, rng.Range(5f, 2f));
            Assert.Equal(5,  rng.Range(5, 5));
            Assert.Equal(5,  rng.Range(5, 2));
            Assert.Equal(5L, rng.RangeInclusive(5, 4));
        }

        /// <summary>
        /// Not a proof of quality -- a chi-squared test would be -- but enough to
        /// catch a mixing function that has been broken into producing a narrow band,
        /// which is the realistic failure and one that silently unbalances every drop
        /// in the game.
        /// </summary>
        [Fact]
        public void DrawsSpreadAcrossTheRange()
        {
            var rng     = new CounterRandom(2024);
            var buckets = new int[10];
            const int Draws = 100000;

            for (int i = 0; i < Draws; i++) buckets[(int)(rng.Next01() * 10)]++;

            foreach (int count in buckets)
                Assert.InRange(count, Draws / 10 * 0.9, Draws / 10 * 1.1);
        }
    }
}
