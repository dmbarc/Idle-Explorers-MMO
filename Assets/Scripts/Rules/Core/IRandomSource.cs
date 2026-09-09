using System;

namespace IdleExplorers.Rules
{
    /// <summary>
    /// Randomness the rules can use in either host.
    ///
    /// UnityEngine.Random is a global, unseeded, unreproducible generator. That is
    /// fine for a puff of smoke and unacceptable for anything the server decides: a
    /// boss fight has to be re-derivable from its audit log months later, or "the
    /// server said you did 4,700 damage" is an assertion rather than a fact.
    /// </summary>
    public interface IRandomSource
    {
        /// <summary>Uniform in [0, 1).</summary>
        float Next01();

        /// <summary>Uniform in [min, max). Returns min when the band is empty.</summary>
        float Range(float min, float max);

        /// <summary>Uniform in [minInclusive, maxExclusive).</summary>
        int Range(int minInclusive, int maxExclusive);

        /// <summary>Uniform in [minInclusive, maxInclusive] -- inclusive, for loot rolls.</summary>
        long RangeInclusive(long minInclusive, long maxInclusive);
    }

    /// <summary>
    /// A counter-based generator: every value is a pure function of (seed, index).
    ///
    /// Counter-based rather than sequential state, because the server needs to answer
    /// "what did roll number 47 of encounter X produce?" long after the encounter
    /// ended, without having kept the generator alive or replayed the first 46 draws.
    /// Recording the index alongside the damage makes the whole fight reproducible
    /// from two numbers.
    ///
    /// The mixing function is SplitMix64, chosen because it is a handful of lines,
    /// has no lookup tables, passes the usual statistical batteries, and produces
    /// identical output on every platform -- which matters when the client predicts a
    /// number the server also computes.
    /// </summary>
    public sealed class CounterRandom : IRandomSource
    {
        private readonly ulong _seed;

        /// <summary>How many values have been drawn. Record this to reproduce a roll.</summary>
        public ulong Index { get; private set; }

        public CounterRandom(long seed, ulong startIndex = 0)
        {
            _seed = unchecked((ulong)seed);
            Index = startIndex;
        }

        /// <summary>The raw 64 bits at a given index, without advancing anything.</summary>
        public ulong BitsAt(ulong index)
        {
            unchecked
            {
                ulong z = _seed + (index + 1) * 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        private ulong NextBits() => BitsAt(Index++);

        /// <summary>
        /// Uniform in [0, 1).
        ///
        /// Built from the top 24 bits rather than a modulo, so every representable
        /// float in the range is equally likely and the result can never round to 1.
        /// </summary>
        public float Next01() => (NextBits() >> 40) * (1.0f / 16777216.0f);

        public float Range(float min, float max) =>
            max <= min ? min : min + Next01() * (max - min);

        public int Range(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive) return minInclusive;
            ulong span = (ulong)((long)maxExclusive - minInclusive);
            return minInclusive + (int)(NextBits() % span);
        }

        public long RangeInclusive(long minInclusive, long maxInclusive)
        {
            if (maxInclusive <= minInclusive) return minInclusive;
            ulong span = (ulong)(maxInclusive - minInclusive) + 1UL;
            return minInclusive + (long)(NextBits() % span);
        }
    }
}
