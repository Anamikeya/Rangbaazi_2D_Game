using System;
using System.Globalization;

namespace Rangbaazi.Sim.FixedMath;

/// <summary>
/// Signed fixed-point number with 16 fractional bits stored in a 64-bit integer (Q47.16).
/// All gameplay math uses this type so every machine computes bit-identical results,
/// which rollback netcode depends on. Resolution is 1/65536 (~0.000015).
/// Convert to float only in the presentation layer (see Rangbaazi.Game FixExtensions).
/// </summary>
public readonly struct Fix : IEquatable<Fix>, IComparable<Fix>
{
    public const int FractionalBits = 16;
    public const long OneRaw = 1L << FractionalBits;

    public readonly long Raw;

    private Fix(long raw) => Raw = raw;

    public static readonly Fix Zero = new(0);
    public static readonly Fix One = new(OneRaw);
    public static readonly Fix Half = new(OneRaw / 2);
    public static readonly Fix MaxValue = new(long.MaxValue);
    public static readonly Fix MinValue = new(long.MinValue);

    public static Fix FromRaw(long raw) => new(raw);

    public static Fix FromInt(int value) => new((long)value << FractionalBits);

    /// <summary>Exact way to write fractional constants without floats, e.g. FromRatio(3, 10) == 0.3.</summary>
    public static Fix FromRatio(long numerator, long denominator)
    {
        if (denominator == 0) throw new DivideByZeroException();
        return new((long)(((Int128)numerator << FractionalBits) / denominator));
    }

    /// <summary>Rounds toward negative infinity.</summary>
    public int FloorToInt() => (int)(Raw >> FractionalBits);

    public static Fix operator +(Fix a, Fix b) => new(a.Raw + b.Raw);
    public static Fix operator -(Fix a, Fix b) => new(a.Raw - b.Raw);
    public static Fix operator -(Fix a) => new(-a.Raw);
    public static Fix operator *(Fix a, Fix b) => new((long)(((Int128)a.Raw * b.Raw) >> FractionalBits));
    public static Fix operator *(Fix a, int b) => new(a.Raw * b);
    public static Fix operator *(int a, Fix b) => new(a * b.Raw);

    public static Fix operator /(Fix a, Fix b)
    {
        if (b.Raw == 0) throw new DivideByZeroException();
        return new((long)(((Int128)a.Raw << FractionalBits) / b.Raw));
    }

    public static Fix operator /(Fix a, int b)
    {
        if (b == 0) throw new DivideByZeroException();
        return new(a.Raw / b);
    }

    public static bool operator ==(Fix a, Fix b) => a.Raw == b.Raw;
    public static bool operator !=(Fix a, Fix b) => a.Raw != b.Raw;
    public static bool operator <(Fix a, Fix b) => a.Raw < b.Raw;
    public static bool operator >(Fix a, Fix b) => a.Raw > b.Raw;
    public static bool operator <=(Fix a, Fix b) => a.Raw <= b.Raw;
    public static bool operator >=(Fix a, Fix b) => a.Raw >= b.Raw;

    public static Fix Abs(Fix a) => a.Raw < 0 ? new(-a.Raw) : a;
    public static Fix Min(Fix a, Fix b) => a.Raw <= b.Raw ? a : b;
    public static Fix Max(Fix a, Fix b) => a.Raw >= b.Raw ? a : b;
    public static Fix Clamp(Fix v, Fix min, Fix max) => v.Raw < min.Raw ? min : (v.Raw > max.Raw ? max : v);
    public static int Sign(Fix a) => a.Raw > 0 ? 1 : (a.Raw < 0 ? -1 : 0);

    /// <summary>Integer square root on the raw value; exact floor, fully deterministic.</summary>
    public static Fix Sqrt(Fix a)
    {
        if (a.Raw < 0) throw new ArgumentOutOfRangeException(nameof(a), "Sqrt of negative Fix.");
        if (a.Raw == 0) return Zero;
        // sqrt(raw / 2^16) * 2^16 == sqrt(raw * 2^16)
        return new((long)ISqrt((UInt128)(ulong)a.Raw << FractionalBits));
    }

    /// <summary>Moves value toward target by at most maxDelta (handy for acceleration/friction).</summary>
    public static Fix MoveToward(Fix value, Fix target, Fix maxDelta)
    {
        if (value < target) return Min(value + maxDelta, target);
        if (value > target) return Max(value - maxDelta, target);
        return value;
    }

    private static ulong ISqrt(UInt128 n)
    {
        UInt128 result = 0;
        UInt128 bit = (UInt128)1 << 126;
        while (bit > n) bit >>= 2;
        while (bit != 0)
        {
            if (n >= result + bit)
            {
                n -= result + bit;
                result = (result >> 1) + bit;
            }
            else
            {
                result >>= 1;
            }
            bit >>= 2;
        }
        return (ulong)result;
    }

    public bool Equals(Fix other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is Fix f && f.Raw == Raw;
    public override int GetHashCode() => Raw.GetHashCode();
    public int CompareTo(Fix other) => Raw.CompareTo(other.Raw);

    /// <summary>Debug formatting with 4 decimal places, computed with integer math only.</summary>
    public override string ToString()
    {
        long raw = Raw;
        bool negative = raw < 0;
        UInt128 abs = negative ? (UInt128)(-(Int128)raw) : (UInt128)(ulong)raw;
        UInt128 whole = abs >> FractionalBits;
        UInt128 frac = ((abs & (OneRaw - 1)) * 10000) >> FractionalBits;
        return string.Create(CultureInfo.InvariantCulture, $"{(negative ? "-" : "")}{whole}.{frac:D4}");
    }
}
