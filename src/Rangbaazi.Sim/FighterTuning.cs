using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim;

/// <summary>
/// Per-hero movement, dodge and attack numbers (GDD B4 "Move-set template"). Values are per frame
/// at 60 Hz. Placeholder numbers for the graybox; the move-set editor will author these per hero
/// later. docs/design/combat-spec-v0.1.md explains them.
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

    // Dodge (GDD B2 combat verbs): 0.3 s invincible, one air dodge per trip into the air, 1 s cooldown.
    public int DodgeIntangibleFrames { get; init; } = 18;
    public int DodgeTotalFrames { get; init; } = 24;                   // the last 6 frames can be hit
    public int DodgeCooldownFrames { get; init; } = 60;                // counted from the end of the dodge
    public Fix GroundDodgeSpeed { get; init; } = Fix.FromRatio(16, 100); // with a direction; none = spot dodge
    public Fix AirDodgeSpeed { get; init; } = Fix.FromRatio(16, 100);    // any of 8 directions; none = hang still

    // Parry (GDD B2): a dodge timed into a hit. A hit in the first ParryWindowFrames of a dodge is
    // parried: the attacker is stunned and the defender can act at once.
    public int ParryWindowFrames { get; init; } = 6;
    public int ParryStunFrames { get; init; } = 36;

    public MoveSet Moves { get; init; } = MoveSet.AllRounder;

    public static readonly FighterTuning Default = new();
}
