using System;

namespace Rangbaazi.Sim;

/// <summary>
/// Seeded SplitMix64 generator. Its whole state is one ulong stored in <see cref="GameState"/>,
/// so saving/loading state for rollback also rewinds the RNG (e.g. Prop Box drops, GDD B5).
/// </summary>
public struct SimRandom
{
    public ulong State;

    public SimRandom(ulong seed) => State = seed;

    public ulong NextULong()
    {
        ulong z = State += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public uint NextUInt() => (uint)(NextULong() >> 32);

    /// <summary>Uniform integer in [minInclusive, maxExclusive) using multiply-shift (no modulo bias loop).</summary>
    public int Range(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive) throw new ArgumentOutOfRangeException(nameof(maxExclusive));
        ulong span = (ulong)((long)maxExclusive - minInclusive);
        return (int)(minInclusive + (long)((NextUInt() * span) >> 32));
    }

    /// <summary>True with probability numerator/denominator.</summary>
    public bool Chance(int numerator, int denominator) => Range(0, denominator) < numerator;
}
