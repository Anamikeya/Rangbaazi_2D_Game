namespace Rangbaazi.Sim.FixedMath;

/// <summary>Axis-aligned box used for hitboxes, hurtboxes and platforms. Min is bottom-left (y-up).</summary>
public readonly struct FixRect
{
    public readonly Fix MinX;
    public readonly Fix MinY;
    public readonly Fix MaxX;
    public readonly Fix MaxY;

    public FixRect(Fix minX, Fix minY, Fix maxX, Fix maxY)
    {
        MinX = Fix.Min(minX, maxX);
        MaxX = Fix.Max(minX, maxX);
        MinY = Fix.Min(minY, maxY);
        MaxY = Fix.Max(minY, maxY);
    }

    public bool Overlaps(in FixRect other) =>
        MinX < other.MaxX && other.MinX < MaxX && MinY < other.MaxY && other.MinY < MaxY;

    public bool Contains(FixVec2 p) => p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY;

    public override string ToString() => $"[{MinX},{MinY} .. {MaxX},{MaxY}]";
}
