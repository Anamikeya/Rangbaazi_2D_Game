using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim;

public enum FighterAction : byte
{
    Idle,

    /// <summary>Performing <see cref="Fighter.Move"/>.</summary>
    Attack,

    /// <summary>Dodging: can't be hit for the first frames, and parries a hit in the very first ones.</summary>
    Dodge,

    /// <summary>Knocked back by a hit; can't act until <see cref="Fighter.HitstunFrames"/> runs out.</summary>
    Hitstun,

    /// <summary>Parried: stunned in place, no knockback, for <see cref="Fighter.HitstunFrames"/>.</summary>
    Stunned,

    Respawning,
    Eliminated,
}

/// <summary>
/// One fighter's rollback state. A plain value type: copying it copies everything,
/// which keeps save/load state cheap for the netcode layer.
/// </summary>
public struct Fighter
{
    public FixVec2 Position;   // feet, y-up
    public FixVec2 Velocity;
    public int Facing;         // +1 right, -1 left
    public bool Grounded;
    public int AirJumpsLeft;
    public int Josh;           // knockback percent, 0..300 (GDD B2)
    public int Stocks;
    public FighterAction Action;
    public MoveId Move;        // the move being performed while Action is Attack
    public int ActionFrame;    // frames since the current action started
    public int HitstunFrames;  // frames left in Hitstun or Stunned
    public bool AttackConnected;
    public Buttons PreviousButtons;

    /// <summary>Frames left frozen in hit-stop. Nothing about the fighter changes until it reaches 0.</summary>
    public int HitStop;

    /// <summary>
    /// The current hit-stop comes from a hit this fighter took (knocked back, or soaked by armor),
    /// rather than from its own attack landing or a parry. Cleared when the freeze ends.
    /// </summary>
    public bool FrozenByHit;

    /// <summary>Frames until the next dodge is allowed.</summary>
    public int DodgeCooldown;

    /// <summary>The air dodge is spent until the fighter lands or is hit.</summary>
    public bool AirDodgeUsed;

    /// <summary>The recovery move (up heavy) is spent until the fighter lands or is hit.</summary>
    public bool RecoveryUsed;

    /// <summary>This stock's Guts has been triggered (GDD B2: once per stock).</summary>
    public bool GutsUsed;

    /// <summary>Frames left before Guts pays out; 0 when no Guts is pending.</summary>
    public int GutsTimer;

    /// <summary>Frames of Guts armor left: hits still add Josh but cause no knockback or hitstun.</summary>
    public int ArmorFrames;

    public readonly bool IsActive => Action != FighterAction.Respawning && Action != FighterAction.Eliminated;

    internal readonly void AddTo(ref StateHasher h)
    {
        h.Add(Position.X.Raw);
        h.Add(Position.Y.Raw);
        h.Add(Velocity.X.Raw);
        h.Add(Velocity.Y.Raw);
        h.Add(Facing);
        h.Add(Grounded);
        h.Add(AirJumpsLeft);
        h.Add(Josh);
        h.Add(Stocks);
        h.Add((int)Action);
        h.Add((int)Move);
        h.Add(ActionFrame);
        h.Add(HitstunFrames);
        h.Add(AttackConnected);
        h.Add((int)PreviousButtons);
        h.Add(HitStop);
        h.Add(FrozenByHit);
        h.Add(DodgeCooldown);
        h.Add(AirDodgeUsed);
        h.Add(RecoveryUsed);
        h.Add(GutsUsed);
        h.Add(GutsTimer);
        h.Add(ArmorFrames);
    }
}
