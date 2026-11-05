using Rangbaazi.Sim.Diagnostics;
using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim.Tests;

/// <summary>
/// The smoke-test bots must be deterministic and must never write game state, because the Godot
/// smoke test (<c>-- --smoke</c>) compares its run against a pure-sim replay of the same script.
/// </summary>
public class SparringBotTests
{
    private static readonly MatchRules Rules = SmokeScript.CreateRules();

    /// <summary>Two fighters landed and standing on the main stage, P1 on the left facing right.</summary>
    private static GameState Standing(int p1X, int p2X)
    {
        var s = Simulation.CreateInitialState(Rules, 2, 1);
        for (int i = 0; i < 2; i++)
        {
            ref Fighter f = ref s.Fighters[i];
            f.Position = new FixVec2(Fix.FromInt(i == 0 ? p1X : p2X), Fix.Zero);
            f.Velocity = FixVec2.Zero;
            f.Grounded = true;
            f.Facing = i == 0 ? 1 : -1;
        }
        return s;
    }

    [Fact]
    public void SmokeScriptIsReproducible()
    {
        GameState a = SmokeScript.Run(Rules);
        GameState b = SmokeScript.Run(SmokeScript.CreateRules());

        Assert.Equal(SmokeScript.Frames, a.Frame);
        Assert.Equal(a.Checksum(), b.Checksum());
    }

    [Fact]
    public void SmokeScriptProducesAFightBothWays()
    {
        GameState s = Simulation.CreateInitialState(Rules, SmokeScript.Players, SmokeScript.Seed);
        var inputs = new Buttons[SmokeScript.Players];
        var hitsTaken = new int[SmokeScript.Players];
        var lastJosh = new int[SmokeScript.Players];

        for (int f = 0; f < SmokeScript.Frames; f++)
        {
            SmokeScript.Inputs(s, Rules, inputs);
            Simulation.Advance(s, Rules, inputs);
            for (int p = 0; p < SmokeScript.Players; p++)
            {
                if (s.Fighters[p].Josh > lastJosh[p]) hitsTaken[p]++;
                lastJosh[p] = s.Fighters[p].Josh;
            }
        }

        Assert.All(hitsTaken, n => Assert.True(n > 0, "every bot should get hit at least once"));
    }

    [Fact]
    public void ThinkNeverChangesState()
    {
        GameState s = Simulation.CreateInitialState(Rules, 4, 3);
        var inputs = new Buttons[4];
        for (int f = 0; f < 1200; f++)
        {
            ulong before = s.Checksum();
            for (int p = 0; p < inputs.Length; p++) inputs[p] = SparringBot.Think(s, Rules, p);
            Assert.Equal(before, s.Checksum());
            Simulation.Advance(s, Rules, inputs);
        }
    }

    [Fact]
    public void WalksTowardAFarOpponent()
    {
        GameState s = Standing(-5, 5);
        Assert.Equal(Buttons.Right, SparringBot.Think(s, Rules, 0));
        Assert.Equal(Buttons.Left, SparringBot.Think(s, Rules, 1));
    }

    [Fact]
    public void TurnsAroundBeforeJabbing()
    {
        GameState s = Standing(0, 1);
        s.Fighters[0].Facing = -1; // opponent is behind
        Assert.Equal(Buttons.Right, SparringBot.Think(s, Rules, 0));
    }

    [Fact]
    public void JabsOnlyWithFreshPressesWhenInReach()
    {
        GameState s = Standing(0, 1);
        int jabs = 0;
        for (int f = 0; f < 40; f++)
        {
            s.Frame = f;
            Buttons b = SparringBot.Think(s, Rules, 0);
            Assert.True(b == Buttons.None || b == Buttons.Light, $"frame {f}: {b}");
            if (b == Buttons.Light) jabs++;
        }
        Assert.InRange(jabs, 1, 39); // jabs, but hesitates sometimes

        s.Fighters[0].PreviousButtons = Buttons.Light; // still held from last frame: no new press
        for (int f = 0; f < 40; f++)
        {
            s.Frame = f;
            Assert.Equal(Buttons.None, SparringBot.Think(s, Rules, 0));
        }
    }

    [Fact]
    public void SteersBackAndAirJumpsWhenOffStage()
    {
        GameState s = Standing(-5, 5);
        ref Fighter me = ref s.Fighters[0];
        me.Position = new FixVec2(Fix.FromInt(11), Fix.FromInt(-1));
        me.Velocity = new FixVec2(Fix.Zero, Fix.FromRatio(-1, 10));
        me.Grounded = false;

        Assert.Equal(Buttons.Left | Buttons.Jump, SparringBot.Think(s, Rules, 0));

        me.PreviousButtons = Buttons.Left | Buttons.Jump; // jump still held: keep steering, no re-press
        Assert.Equal(Buttons.Left, SparringBot.Think(s, Rules, 0));
    }

    [Fact]
    public void SavesTheAirJumpWhileAboveTheStageTop()
    {
        GameState s = Standing(-5, 5);
        ref Fighter me = ref s.Fighters[0];
        me.Position = new FixVec2(Fix.FromInt(11), Fix.FromInt(3)); // off the side, above the top
        me.Velocity = new FixVec2(Fix.Zero, Fix.FromRatio(-1, 10));
        me.Grounded = false;

        Assert.Equal(Buttons.Left, SparringBot.Think(s, Rules, 0));

        me.Position = new FixVec2(Fix.FromInt(11), Fix.FromRatio(-1, 10)); // just fell below it
        Assert.Equal(Buttons.Left | Buttons.Jump, SparringBot.Think(s, Rules, 0));
    }

    [Fact]
    public void WaitsAtTheEdgeForAnOpponentOffTheSide()
    {
        GameState s = Standing(6, 5);
        ref Fighter op = ref s.Fighters[1];
        op.Position = new FixVec2(Fix.FromInt(12), Fix.FromInt(-1)); // recovering, out of reach
        op.Grounded = false;
        s.Fighters[0].Facing = 1;

        Assert.Equal(Buttons.Right, SparringBot.Think(s, Rules, 0)); // walks toward the edge...

        s.Fighters[0].Position = new FixVec2(Fix.FromInt(8), Fix.Zero);
        Assert.Equal(Buttons.None, SparringBot.Think(s, Rules, 0)); // ...and stops 1 m before it

        s.Fighters[0].Position = new FixVec2(Fix.FromRatio(17, 2), Fix.Zero); // overshot: back off
        Assert.Equal(Buttons.Left, SparringBot.Think(s, Rules, 0));
    }

    [Fact]
    public void StillJabsAnOpponentStandingAtTheEdge()
    {
        GameState s = Standing(8, 9); // P2 right on the edge, 1 m away
        int jabs = 0;
        for (int f = 0; f < 20; f++)
        {
            s.Frame = f;
            if (SparringBot.Think(s, Rules, 0) == Buttons.Light) jabs++;
        }
        Assert.True(jabs > 0);
    }

    [Fact]
    public void NoAirJabThatWouldDriftOffTheEdge()
    {
        // Airborne near the edge, drifting outward, opponent in reach: a jab would lock out
        // steering for the whole attack and carry it off the stage. Steer back in instead.
        GameState s = Standing(8, 9);
        ref Fighter me = ref s.Fighters[0];
        ref Fighter op = ref s.Fighters[1];
        me.Position = new FixVec2(Fix.FromRatio(17, 2), Fix.FromInt(1));
        me.Velocity = new FixVec2(Fix.FromRatio(8, 100), Fix.FromRatio(-1, 10));
        me.Grounded = false;
        op.Position = new FixVec2(Fix.FromRatio(19, 2), Fix.FromInt(1)); // 1 m away, facing it
        op.Grounded = false;

        for (int f = 0; f < 40; f++)
        {
            s.Frame = f;
            Assert.Equal(Buttons.Left, SparringBot.Think(s, Rules, 0));
        }

        // Airborne over the stage and not drifting: the jab ends over the stage, so it attacks.
        me.Position = new FixVec2(Fix.FromInt(7), Fix.FromInt(1));
        me.Velocity = new FixVec2(Fix.Zero, Fix.FromRatio(-1, 10));
        op.Position = new FixVec2(Fix.FromInt(8), Fix.FromInt(1));
        Assert.True(JabsWithin40Frames(s));

        // Away from the edge, flying outward at full air speed (0.11 m/frame): the jab carries it
        // 1.65 m and braking another 0.6 m. From 6.5 m that ends at ~8.75, past the 8.5 m limit...
        me.Velocity = new FixVec2(Fix.FromRatio(11, 100), Fix.FromRatio(-1, 10));
        me.Position = new FixVec2(Fix.FromRatio(13, 2), Fix.FromInt(2));
        op.Position = new FixVec2(Fix.FromRatio(15, 2), Fix.FromInt(2));
        for (int f = 0; f < 40; f++)
        {
            s.Frame = f;
            Assert.Equal(Buttons.None, SparringBot.Think(s, Rules, 0) & Buttons.Light);
        }

        // ...from 6.2 m it ends at ~8.45, inside it, so the bot jabs.
        me.Position = new FixVec2(Fix.FromRatio(31, 5), Fix.FromInt(2));
        op.Position = new FixVec2(Fix.FromRatio(36, 5), Fix.FromInt(2));
        Assert.True(JabsWithin40Frames(s));
    }

    private static bool JabsWithin40Frames(GameState s)
    {
        for (int f = 0; f < 40; f++)
        {
            s.Frame = f;
            if (SparringBot.Think(s, Rules, 0) == Buttons.Light) return true;
        }
        return false;
    }

    [Fact]
    public void WalksOffAFloatingPlatformTowardAnOpponentBelow()
    {
        GameState s = Standing(0, 0);
        ref Fighter me = ref s.Fighters[0];
        me.Position = new FixVec2(Fix.FromRatio(7, 2), Fix.FromRatio(11, 4)); // right platform, x 2..6
        me.Facing = -1;
        ref Fighter op = ref s.Fighters[1];

        op.Position = new FixVec2(Fix.FromRatio(14, 5), Fix.Zero); // below, left of the platform centre
        Assert.Equal(Buttons.Left, SparringBot.Think(s, Rules, 0));

        op.Position = new FixVec2(Fix.FromRatio(9, 2), Fix.Zero); // below, right of the centre
        Assert.Equal(Buttons.Right, SparringBot.Think(s, Rules, 0));
    }

    [Fact]
    public void JumpsAfterAnOpponentOnAPlatformButNotOneInTheAir()
    {
        GameState s = Standing(3, 4);
        ref Fighter op = ref s.Fighters[1];
        op.Position = new FixVec2(Fix.FromInt(4), Fix.FromRatio(11, 4)); // standing on the right platform
        op.Grounded = true;
        Assert.Equal(Buttons.Jump, SparringBot.Think(s, Rules, 0) & Buttons.Jump);

        op.Grounded = false; // same height, but just jumping
        Assert.Equal(Buttons.None, SparringBot.Think(s, Rules, 0) & Buttons.Jump);
    }

    [Fact]
    public void DoesNothingWhileItCannotAct()
    {
        GameState s = Standing(-5, 5);
        s.Fighters[0].Action = FighterAction.Hitstun;
        Assert.Equal(Buttons.None, SparringBot.Think(s, Rules, 0));

        s.Fighters[0].Action = FighterAction.Idle;
        s.Fighters[1].Action = FighterAction.Eliminated; // no opponent left
        Assert.Equal(Buttons.None, SparringBot.Think(s, Rules, 0));
    }
}
