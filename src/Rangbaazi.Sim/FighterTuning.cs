using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim;

/// <summary>
/// Per-hero movement and attack numbers (GDD B4 "Move-set template"). Values are per frame at 60 Hz.
/// Placeholder numbers for the graybox; the move-set editor will author these per hero later.
/// </summary>
public sealed class FighterTuning
{
    public Fix RunSpeed { get; init; } = Fix.FromRatio(13, 100);        // ~7.8 m/s
    public Fix AirSpeed { get; init; } = Fix.FromRatio(11, 100);
    public Fix GroundAccel { get; init; } = Fix.FromRatio(2, 100);
    public Fix AirAccel { get; init; } = Fix.FromRatio(1, 100);
    public Fix Gravity { get; init; } = Fix.FromRatio(1, 100);
    public Fix MaxFallSpeed { get; init; } = Fix.FromRatio(30, 100);
    public Fix JumpVelocity { get; init; } = Fix.FromRatio(25, 100);
    public Fix AirJumpVelocity { get; init; } = Fix.FromRatio(22, 100);
    public int AirJumps { get; init; } = 1;                            // GDD: jump x2

    public Fix HalfWidth { get; init; } = Fix.FromRatio(2, 5);
    public Fix Height { get; init; } = Fix.FromRatio(9, 5);

    // Placeholder light attack (jab) used to prove hit -> Josh % -> knockback -> ring-out end to end.
    public int LightStartup { get; init; } = 4;
    public int LightActive { get; init; } = 3;
    public int LightRecovery { get; init; } = 8;
    public int LightDamage { get; init; } = 6;                        // Josh % added
    public Fix LightBaseKnockback { get; init; } = Fix.FromRatio(12, 100);
    public Fix LightKnockbackPerJosh { get; init; } = Fix.FromRatio(3, 1000);

    public int LightTotalFrames => LightStartup + LightActive + LightRecovery;

    public static readonly FighterTuning Default = new();
}
