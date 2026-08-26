/// <summary>
/// UnityEngine.Random behind the rules' IRandomSource interface.
///
/// This is for PREDICTION ONLY. Every number it produces is something the client is
/// drawing while it waits for the server to say what actually happened -- a damage
/// number floating over a goblin, a swing that looks like it landed. None of it is
/// persisted and none of it is believed.
///
/// Anything the server decides uses CounterRandom instead, seeded and indexed so the
/// roll can be re-derived from an audit log. Reaching for this class in code that
/// grants, spends or records something is the mistake to watch for.
/// </summary>
public sealed class UnityRandomSource : IdleExplorers.Rules.IRandomSource
{
    /// <summary>Shared instance. Stateless, because UnityEngine.Random is a global.</summary>
    public static readonly UnityRandomSource Instance = new UnityRandomSource();

    public float Next01() => UnityEngine.Random.value;

    public float Range(float min, float max) =>
        max <= min ? min : UnityEngine.Random.Range(min, max);

    public int Range(int minInclusive, int maxExclusive) =>
        maxExclusive <= minInclusive ? minInclusive
                                     : UnityEngine.Random.Range(minInclusive, maxExclusive);

    public long RangeInclusive(long minInclusive, long maxInclusive)
    {
        if (maxInclusive <= minInclusive) return minInclusive;

        // Random.Range takes ints. Loot quantities are longs because the economy runs
        // to billions, so fall back to a double roll across the span rather than
        // truncating the band to int range and silently capping a large drop.
        double span = (double)maxInclusive - minInclusive;
        return minInclusive + (long)System.Math.Round(UnityEngine.Random.value * span);
    }
}
