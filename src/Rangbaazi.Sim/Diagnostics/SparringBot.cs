using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim.Diagnostics;

/// <summary>
/// A very simple CPU opponent for smoke tests, soak tests and demos. Not the real AI.
///
/// It is a pure function of (state, player): it reads <see cref="GameState"/> and returns
/// <see cref="Buttons"/>, and it never writes state or draws from the match RNG. Running it on
/// every peer therefore gives identical inputs, so it is safe to use inside rollback tests.
///
/// Behaviour: walk to the nearest opponent, turn to face them, jab when in reach, jump after
/// them if they are on a platform above, and steer and air-jump back when off the stage.
/// A per-frame hash (not the match RNG) adds some hesitation so mirrored bots don't trade forever.
/// </summary>
public static class SparringBot
{
    /// <summary>Stop walking at this distance; the jab hits out to ~1.5 m between centres.</summary>
    private static readonly Fix JabRange = Fix.FromRatio(6, 5);
    private static readonly Fix ChaseHeight = Fix.FromRatio(3, 2);
    private static readonly Fix ChaseDistance = Fix.FromInt(4);

    public static Buttons Think(GameState state, MatchRules rules, int self)
    {
        Fighter[] fighters = state.Fighters;
        ref readonly Fighter me = ref fighters[self];
        if (me.Action != FighterAction.Idle) return Buttons.None; // attacking, in hitstun or out

        Buttons last = me.PreviousButtons; // jump and light only fire on a fresh press
        bool canJump = (last & Buttons.Jump) == 0;
        bool canJab = (last & Buttons.Light) == 0;

        // Recovery: off the sides of the main stage, or below its top surface.
        FixRect stage = rules.Stage.Platforms[0];
        bool offStage = me.Position.X < stage.MinX || me.Position.X > stage.MaxX
            || (!me.Grounded && me.Position.Y < stage.MaxY);
        if (offStage)
        {
            Buttons home = me.Position.X < Fix.Zero ? Buttons.Right : Buttons.Left;
            if (me.Velocity.Y < Fix.Zero && canJump) home |= Buttons.Jump;
            return home;
        }

        int target = NearestOpponent(fighters, self);
        if (target < 0) return Buttons.None;
        ref readonly Fighter op = ref fighters[target];

        Fix dx = op.Position.X - me.Position.X;
        Fix dy = op.Position.Y - me.Position.Y;
        int dir = dx >= Fix.Zero ? 1 : -1;
        Buttons toward = dir > 0 ? Buttons.Right : Buttons.Left;

        Buttons b = Buttons.None;
        if (Fix.Abs(dx) > JabRange || me.Facing != dir)
        {
            b |= toward;
        }
        else if (canJab && Fix.Abs(dy) < Fix.One && !Hesitates(state.Frame, self))
        {
            b |= Buttons.Light;
        }

        if (me.Grounded && canJump && dy > ChaseHeight && Fix.Abs(dx) < ChaseDistance) b |= Buttons.Jump;
        return b;
    }

    private static int NearestOpponent(Fighter[] fighters, int self)
    {
        FixVec2 at = fighters[self].Position;
        int best = -1;
        Fix bestDist = Fix.Zero;
        for (int i = 0; i < fighters.Length; i++)
        {
            if (i == self || !fighters[i].IsActive) continue;
            Fix d = (fighters[i].Position - at).LengthSquared;
            if (best < 0 || d < bestDist)
            {
                best = i;
                bestDist = d;
            }
        }
        return best;
    }

    /// <summary>Holds back from jabbing on about 1 frame in 2, differently per player.</summary>
    private static bool Hesitates(int frame, int self)
    {
        var hash = new SimRandom(((ulong)(uint)frame << 8) ^ (ulong)(uint)self ^ 0xA5A5_5A5A_C3C3_3C3CUL);
        return hash.Chance(1, 2);
    }
}
