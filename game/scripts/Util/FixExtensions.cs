using Godot;
using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Game.Util;

/// <summary>
/// Fixed-point to float conversion. Lives in the game project on purpose: floats are for
/// rendering only and must never flow back into the simulation.
/// </summary>
public static class FixExtensions
{
    public static float ToFloat(this Fix f) => f.Raw / (float)Fix.OneRaw;

    public static Vector2 ToVector2(this FixVec2 v) => new(v.X.ToFloat(), v.Y.ToFloat());
}
