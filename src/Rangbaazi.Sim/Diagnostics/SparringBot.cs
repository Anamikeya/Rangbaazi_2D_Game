using System;
using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim.Diagnostics;

/// <summary>How quickly a <see cref="SparringBot"/> jabs once an opponent is in reach.</summary>
public enum CpuLevel : byte
{
    /// <summary>Jabs on about 1 frame in 16 while in reach: waits ~0.25 s on average, often less.</summary>
    Easy,

    /// <summary>Jabs on about 1 frame in 6 while in reach: waits ~0.1 s on average.</summary>
    Normal,

    /// <summary>Jabs on about 1 frame in 2 while in reach: almost at once. The smoke-test bot.</summary>
    Hard,
}

/// <summary>
/// A very simple CPU opponent for smoke tests, soak tests, demos and the graybox's CPU players.
/// Not the real AI.
///
/// It is a pure function of (state, player, level): it reads <see cref="GameState"/> and returns
/// <see cref="Buttons"/>, and it never writes state or draws from the match RNG. Running it on
/// every peer therefore gives identical inputs, so it is safe to use inside rollback tests.
///
/// Behaviour: walk to the nearest opponent, turn to face them, jab when in reach, jump after
/// them if they are on a platform above, walk off a floating platform if they are below, and
/// steer and air-jump back when off the stage. It never walks past the edges of the main stage
/// after an opponent who is off the side (it waits at the edge instead), and never jabs in the
/// air when the drift would carry it off the stage. A per-frame hash (not the match RNG) adds
/// some hesitation before each jab, so mirrored bots don't trade forever; <see cref="CpuLevel"/>
/// sets how long it hesitates. Only the jab timing depends on the level: walking, chasing and
/// recovery are the same at every level.
/// </summary>
public static class SparringBot
{
    /// <summary>Stop walking at this distance; the jab hits out to ~1.5 m between centres.</summary>
    private static readonly Fix JabRange = Fix.FromRatio(6, 5);
    private static readonly Fix ChaseHeight = Fix.FromRatio(3, 2);
    private static readonly Fix ChaseDistance = Fix.FromInt(4);

    /// <summary>
    /// When chasing, aim no closer than this to either end of the main stage. Leaves room to stop
    /// (running stops in ~0.4 m, drifting in the air in ~0.6 m) and still reaches an opponent at
    /// the very edge with the jab, because EdgeMargin + StopDistance is less than JabRange: the
    /// bot can come to rest at most that far from the edge.
    /// </summary>
    private static readonly Fix EdgeMargin = Fix.One;

    /// <summary>Close enough to the chase point to stop pressing a direction.</summary>
    private static readonly Fix StopDistance = Fix.FromRatio(1, 8);

    /// <summary>An air jab must leave the bot at least this far inside the stage edge.</summary>
    private static readonly Fix LandingMargin = Fix.Half;

    /// <summary>
    /// The bot's buttons for this frame. <paramref name="level"/> defaults to
    /// <see cref="CpuLevel.Hard"/>, which is what <see cref="SmokeScript"/> plays.
    /// </summary>
    public static Buttons Think(GameState state, MatchRules rules, int self, CpuLevel level = CpuLevel.Hard)
    {
        int jabOdds = JabOdds(level); // validate first, so a bad level fails even when idle-gated

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
            // Save the air jump until falling below the stage top: jumping while still above it
            // wastes the jump (a falling fighter gains the most from one), and a bot that drifted
            // out above the stage would have nothing left by the time it needs it.
            if (me.Velocity.Y < Fix.Zero && me.Position.Y < stage.MaxY && canJump) home |= Buttons.Jump;
            return home;
        }

        int target = NearestOpponent(fighters, self);
        if (target < 0) return Buttons.None;
        ref readonly Fighter op = ref fighters[target];

        Fix dx = op.Position.X - me.Position.X;
        Fix dy = op.Position.Y - me.Position.Y;
        int dir = dx >= Fix.Zero ? 1 : -1;

        Buttons b = Buttons.None;
        int floor = me.Grounded ? StandingOn(in me, rules.Stage) : -1;
        if (floor > 0 && dy < -Fix.One)
        {
            // On a floating platform with them below. There is no drop-through, so walk off the end
            // on their side. Waiting would freeze the bot (it can't jab that far down and only
            // chases upwards), giving a person a safe spot under the platform.
            FixRect p = rules.Stage.Platforms[floor];
            b |= op.Position.X * 2 < p.MinX + p.MaxX ? Buttons.Left : Buttons.Right;
        }
        else if (Fix.Abs(dx) <= JabRange && SafeToAttack(in me, stage, rules.Tuning))
        {
            if (me.Facing != dir) b |= dir > 0 ? Buttons.Right : Buttons.Left; // turn to face them
            else if (canJab && Fix.Abs(dy) < Fix.One && !Hesitates(state.Frame, self, jabOdds)) b |= Buttons.Light;
        }
        else
        {
            // Walk after them, but only as far as the edge margin: following an opponent who is
            // off the side is how the bot used to walk and drift off the stage itself.
            Fix goal = Fix.Clamp(op.Position.X, stage.MinX + EdgeMargin, stage.MaxX - EdgeMargin);
            Fix toGoal = goal - me.Position.X;
            if (toGoal > StopDistance) b |= Buttons.Right;
            else if (toGoal < -StopDistance) b |= Buttons.Left;
        }

        // Jump after them only when they stand on a platform above. Jumping at someone who is just
        // in the air above makes two bots take turns jumping at each other, with every jab whiffing
        // because the height gap changes faster than the jab comes out.
        if (me.Grounded && canJump && op.Grounded && dy > ChaseHeight && Fix.Abs(dx) < ChaseDistance) b |= Buttons.Jump;
        return b;
    }

    /// <summary>
    /// A jab locks out steering until it ends, and in the air the fighter keeps drifting. On the
    /// ground friction stops it; in the air, only attack if the drift (through the whole attack,
    /// then braking by steering) still ends over the main stage, or the bot can jab itself off the
    /// side and below the reach of its air jump. Above the stage top with x over the stage at both
    /// ends of the drift, it lands on the stage whenever it comes down.
    /// </summary>
    private static bool SafeToAttack(in Fighter me, FixRect stage, FighterTuning t)
    {
        if (me.Grounded) return true;
        Fix vx = me.Velocity.X;
        Fix brake = vx * Fix.Abs(vx) / (t.AirAccel * 2);
        Fix endX = me.Position.X + vx * t.LightTotalFrames + brake;
        return endX >= stage.MinX + LandingMargin && endX <= stage.MaxX - LandingMargin;
    }

    /// <summary>Index of the platform the fighter is standing on, or -1. Platform 0 is the main stage.</summary>
    private static int StandingOn(in Fighter me, StageDefinition stage)
    {
        var platforms = stage.Platforms;
        for (int i = 0; i < platforms.Count; i++)
        {
            FixRect p = platforms[i];
            if (me.Position.Y == p.MaxY && me.Position.X >= p.MinX && me.Position.X <= p.MaxX) return i;
        }
        return -1;
    }

    /// <summary>The bot jabs on about 1 eligible frame in this many.</summary>
    internal static int JabOdds(CpuLevel level) => level switch
    {
        CpuLevel.Easy => 16,
        CpuLevel.Normal => 6,
        CpuLevel.Hard => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown CPU level."),
    };

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

    /// <summary>Holds back from jabbing on (odds - 1) frames in odds, differently per player.</summary>
    private static bool Hesitates(int frame, int self, int odds)
    {
        var hash = new SimRandom(((ulong)(uint)frame << 8) ^ (ulong)(uint)self ^ 0xA5A5_5A5A_C3C3_3C3CUL);
        return hash.Chance(odds - 1, odds);
    }
}
