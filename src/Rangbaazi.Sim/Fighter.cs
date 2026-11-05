using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim;

public enum FighterAction : byte
{
    Idle,
    LightAttack,
    Hitstun,
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
    public int ActionFrame;
    public int HitstunFrames;
    public bool AttackConnected;
    public Buttons PreviousButtons;

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
        h.Add(ActionFrame);
        h.Add(HitstunFrames);
        h.Add(AttackConnected);
        h.Add((int)PreviousButtons);
    }
}
