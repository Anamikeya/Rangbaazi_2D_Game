using System;

namespace Rangbaazi.Sim.FixedMath;

/// <summary>2D vector of <see cref="Fix"/>. Simulation space is y-up, units are metres.</summary>
public struct FixVec2 : IEquatable<FixVec2>
{
    public Fix X;
    public Fix Y;

    public FixVec2(Fix x, Fix y)
    {
        X = x;
        Y = y;
    }

    public static FixVec2 Zero => default;

    public static FixVec2 FromInts(int x, int y) => new(Fix.FromInt(x), Fix.FromInt(y));

    public static FixVec2 operator +(FixVec2 a, FixVec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static FixVec2 operator -(FixVec2 a, FixVec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static FixVec2 operator -(FixVec2 a) => new(-a.X, -a.Y);
    public static FixVec2 operator *(FixVec2 a, Fix s) => new(a.X * s, a.Y * s);
    public static FixVec2 operator *(Fix s, FixVec2 a) => new(a.X * s, a.Y * s);
    public static bool operator ==(FixVec2 a, FixVec2 b) => a.X == b.X && a.Y == b.Y;
    public static bool operator !=(FixVec2 a, FixVec2 b) => !(a == b);

    public readonly Fix LengthSquared => X * X + Y * Y;
    public readonly Fix Length => Fix.Sqrt(LengthSquared);

    public readonly bool Equals(FixVec2 other) => this == other;
    public override readonly bool Equals(object? obj) => obj is FixVec2 v && this == v;
    public override readonly int GetHashCode() => unchecked(X.Raw.GetHashCode() * 31 + Y.Raw.GetHashCode());
    public override readonly string ToString() => $"({X}, {Y})";
}
