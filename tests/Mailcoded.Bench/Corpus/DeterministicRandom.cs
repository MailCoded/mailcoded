namespace Mailcoded.Bench.Corpus;

/// <summary>splitmix64, hand-rolled because <c>System.Random</c> guarantees no cross-version
/// stability and the corpus claim is that one seed reproduces one corpus forever.</summary>
public sealed class DeterministicRandom
{
    private ulong _state;

    public DeterministicRandom(int seed) => _state = unchecked((ulong)seed * 0x9E3779B97F4A7C15UL) ^ 0xD1B54A32D192ED03UL;

    public ulong NextUInt64()
    {
        unchecked
        {
            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>Uniform in [0, exclusiveMax) with no modulo bias.</summary>
    public int Next(int exclusiveMax)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(exclusiveMax, 1);

        var bound = (ulong)exclusiveMax;
        var limit = ulong.MaxValue - (ulong.MaxValue % bound) - 1;

        ulong value;
        do
        {
            value = NextUInt64();
        }
        while (value > limit);

        return (int)(value % bound);
    }

    public int Next(int inclusiveMin, int exclusiveMax) => inclusiveMin + Next(exclusiveMax - inclusiveMin);

    /// <summary>Uniform in [0,1) from the top 53 bits, which is exactly representable.</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);

    public bool Chance(double probability) => NextDouble() < probability;

    public T Pick<T>(IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items[Next(items.Count)];
    }
}
