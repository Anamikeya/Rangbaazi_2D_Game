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

    /// <summary>P2 (index 1) mid-move at the given frame, 1 m from P1 and facing it.</summary>
    private static GameState Attacking(MoveId move, int actionFrame, int frame)
    {
        GameState s = Standing(0, 1);
        s.Frame = frame;
        ref Fighter op = ref s.Fighters[1];
        op.Action = FighterAction.Attack;
        op.Move = move;
        op.ActionFrame = actionFrame;
        return s;
    }

    [Theory]
    [InlineData(CpuLevel.Easy, 4)]
    [InlineData(CpuLevel.Normal, 3)]
    [InlineData(CpuLevel.Hard, 2)]
    public void DodgesAboutOneHeavyInItsOddsTimedToParry(CpuLevel level, int odds)
    {
        MoveData heavy = Rules.Tuning.Moves.NeutralHeavy;
        int pressAt = heavy.Startup - 3;
        int dodges = 0;
        const int attacks = 1200;
        for (int start = 0; start < attacks; start++)
        {
            // Each frame of one attack (it started on frame `start`), as the bot would see it if it
            // hadn't dodged yet. The first frame it dodges on must be the one that parries.
            for (int a = 0; a < heavy.Startup + heavy.Active; a++)
            {
                Buttons b = SparringBot.Think(Attacking(MoveId.NeutralHeavy, a, start + a), Rules, 0, level);
                if ((b & Buttons.Dodge) == 0) continue;
                Assert.Equal(Buttons.Dodge, b); // a spot dodge: nothing else
                Assert.True(a == pressAt, $"{level} first dodged on attack frame {a}, not {pressAt}");
                dodges++;
                break;
            }
        }
        Assert.InRange(dodges, attacks / odds * 3 / 4, attacks / odds * 5 / 4);
    }

    [Fact]
    public void OnlyHardIsQuickEnoughToParryTheJab()
    {
        int hard = 0;
        for (int start = 0; start < 200; start++)
        {
            for (int a = 0; a < 7; a++)
            {
                GameState s = Attacking(MoveId.Jab, a, start + a);
                Assert.Equal(Buttons.None, SparringBot.Think(s, Rules, 0, CpuLevel.Easy) & Buttons.Dodge);
                Assert.Equal(Buttons.None, SparringBot.Think(s, Rules, 0, CpuLevel.Normal) & Buttons.Dodge);
                if ((SparringBot.Think(s, Rules, 0, CpuLevel.Hard) & Buttons.Dodge) != 0)
                {
                    Assert.Equal(3, a);
                    hard++;
                }
            }
        }
        Assert.InRange(hard, 70, 130);
    }

    [Fact]
    public void IgnoresAttacksThatCannotReachIt()
    {
        for (int start = 0; start < 200; start++)
        {
            GameState s = Attacking(MoveId.NeutralHeavy, Rules.Tuning.Moves.NeutralHeavy.Startup - 3, start);
            s.Fighters[1].Position = new FixVec2(Fix.FromInt(5), Fix.Zero);
            Assert.Equal(Buttons.None, SparringBot.Think(s, Rules, 0) & Buttons.Dodge);
        }
    }

    [Fact]
    public void WaitsOutTheDodgeCooldown()
    {
        for (int start = 0; start < 200; start++)
        {
            GameState s = Attacking(MoveId.NeutralHeavy, Rules.Tuning.Moves.NeutralHeavy.Startup - 3, start);
            s.Fighters[0].DodgeCooldown = 1;
            Assert.Equal(Buttons.None, SparringBot.Think(s, Rules, 0) & Buttons.Dodge);
        }
    }

    [Fact]
    public void PunishesAStunnedOpponentWithANeutralHeavy()
    {
        GameState s = Standing(0, 1);
        s.Fighters[1].Action = FighterAction.Stunned;
        s.Fighters[1].HitstunFrames = 30;
        int heavies = 0;
        for (int f = 0; f < 40; f++)
        {
            s.Frame = f;
            Buttons b = SparringBot.Think(s, Rules, 0);
            Assert.True(b == Buttons.None || b == Buttons.Heavy, $"frame {f}: {b}");
            if (b == Buttons.Heavy) heavies++;
        }
        Assert.True(heavies > 0);
    }

    [Fact]
    public void PunishesAWhiffedHeavyButNotAWhiffedJab()
    {
        MoveData side = Rules.Tuning.Moves.SideHeavy;
        MoveData jab = Rules.Tuning.Moves.Jab;
        int punishes = 0;
        for (int f = 0; f < 40; f++)
        {
            GameState s = Attacking(MoveId.SideHeavy, side.Startup + side.Active, f); // just whiffed
            Buttons b = SparringBot.Think(s, Rules, 0);
            Assert.True(b == Buttons.None || b == (Buttons.Heavy | Buttons.Right), $"frame {f}: {b}");
            if (b != Buttons.None) punishes++;

            GameState j = Attacking(MoveId.Jab, jab.Startup + jab.Active, f); // too little recovery to punish
            Assert.Equal(Buttons.None, SparringBot.Think(j, Rules, 0) & Buttons.Heavy);
        }
        Assert.True(punishes > 0);
    }

    [Fact]
    public void ThrowsHeavyFinishersOnlyAtHighJoshAndNeverLungesOffTheEdge()
    {
        GameState low = Standing(0, 1);
        low.Fighters[1].Josh = SparringBot.FinisherJosh - 1;
        GameState high = Standing(0, 1);
        high.Fighters[1].Josh = SparringBot.FinisherJosh;
        GameState edge = Standing(8, 9);
        edge.Fighters[1].Josh = 150;

        int sideHeavies = 0, edgeHeavies = 0;
        for (int f = 0; f < 600; f++)
        {
            low.Frame = high.Frame = edge.Frame = f;
            Assert.Equal(Buttons.None, SparringBot.Think(low, Rules, 0) & Buttons.Heavy);

            Buttons b = SparringBot.Think(high, Rules, 0);
            if ((b & Buttons.Heavy) != 0)
            {
                Assert.Equal(Buttons.Heavy | Buttons.Right, b); // room to lunge: side heavy
                sideHeavies++;
            }

            Buttons e = SparringBot.Think(edge, Rules, 0);
            if ((e & Buttons.Heavy) != 0)
            {
                Assert.Equal(Buttons.Heavy, e); // a lunge would carry it off: neutral heavy
                edgeHeavies++;
            }
        }

        // About 1 attack in 2 frames (Hard), and 1 attack in FinisherOdds is a heavy.
        int expected = 600 / 2 / SparringBot.FinisherOdds;
        Assert.InRange(sideHeavies, expected / 2, expected * 3 / 2);
        Assert.InRange(edgeHeavies, expected / 2, expected * 3 / 2);
    }

    [Fact]
    public void RecoversWithTheUpHeavyThenAnAirDodge()
    {
        GameState s = Standing(-5, 5);
        ref Fighter me = ref s.Fighters[0];
        me.Position = new FixVec2(Fix.FromInt(11), Fix.FromInt(-1));
        me.Velocity = new FixVec2(Fix.Zero, Fix.FromRatio(-1, 10));
        me.Grounded = false;
        me.AirJumpsLeft = 0;

        Assert.Equal(Buttons.Left | Buttons.Up | Buttons.Heavy, SparringBot.Think(s, Rules, 0));
        me.PreviousButtons = Buttons.Left | Buttons.Up | Buttons.Heavy; // still held: no re-press
        Assert.Equal(Buttons.Left, SparringBot.Think(s, Rules, 0));

        me.PreviousButtons = Buttons.Left;
        me.RecoveryUsed = true;
        Assert.Equal(Buttons.Left | Buttons.Up | Buttons.Dodge, SparringBot.Think(s, Rules, 0));
        me.AirDodgeUsed = true;
        Assert.Equal(Buttons.Left, SparringBot.Think(s, Rules, 0));
    }

    [Fact]
    public void SteersHomeDuringTheUpHeavy()
    {
        GameState s = Standing(-5, 5);
        ref Fighter me = ref s.Fighters[0];
        me.Position = new FixVec2(Fix.FromRatio(-21, 2), Fix.FromInt(-2));
        me.Grounded = false;
        me.Action = FighterAction.Attack;
        me.Move = MoveId.UpHeavy;
        me.ActionFrame = 10;
        Assert.Equal(Buttons.Right, SparringBot.Think(s, Rules, 0));

        me.Position = new FixVec2(Fix.Zero, Fix.FromInt(2)); // over the stage: no steering needed
        Assert.Equal(Buttons.None, SparringBot.Think(s, Rules, 0));

        me.Move = MoveId.SideHeavy; // no air control: nothing to steer
        me.Position = new FixVec2(Fix.FromRatio(-21, 2), Fix.FromInt(-2));
        Assert.Equal(Buttons.None, SparringBot.Think(s, Rules, 0));
    }

    [Fact]
    public void DoesNothingWhileFrozenInHitStop()
    {
        GameState s = Standing(0, 1);
        s.Fighters[0].HitStop = 3;
        for (int f = 0; f < 20; f++)
        {
            s.Frame = f;
            Assert.Equal(Buttons.None, SparringBot.Think(s, Rules, 0));
        }
    }

    [Fact]
    public void DoesNotLungeOffTheEndOfAFloatingPlatform()
    {
        // On the right platform (x 2..6), near its outer end, facing out at an opponent at high Josh.
        GameState s = Standing(0, 0);
        ref Fighter me = ref s.Fighters[0];
        me.Position = new FixVec2(Fix.FromRatio(11, 2), Fix.FromRatio(11, 4));
        me.Facing = 1;
        ref Fighter op = ref s.Fighters[1];
        op.Position = new FixVec2(Fix.FromRatio(59, 10), Fix.FromRatio(11, 4));
        op.Grounded = true;
        op.Josh = 150;

        int heavies = 0;
        for (int f = 0; f < 600; f++)
        {
            s.Frame = f;
            Buttons b = SparringBot.Think(s, Rules, 0);
            if ((b & Buttons.Heavy) == 0) continue;
            Assert.Equal(Buttons.Heavy, b); // neutral heavy: no lunge off the platform
            heavies++;
        }
        Assert.True(heavies > 0);
    }

    [Theory]
    [InlineData(0, 5, false)]
    [InlineData(0, 14, false)] // the heavy (startup 14) would land just as the stun ends: dodgeable
    [InlineData(0, 15, true)]
    [InlineData(4, 10, false)] // hit-stop counts as stun too
    [InlineData(4, 11, true)]
    public void PunishesAStunWithAHeavyOnlyIfItLandsBeforeTheStunEnds(int hitStop, int stun, bool heavy)
    {
        GameState s = Standing(0, 1);
        s.Fighters[1].Action = FighterAction.Stunned;
        s.Fighters[1].HitStop = hitStop;
        s.Fighters[1].HitstunFrames = stun;
        int heavies = 0, jabs = 0;
        for (int f = 0; f < 40; f++)
        {
            s.Frame = f;
            Buttons b = SparringBot.Think(s, Rules, 0);
            if (b == Buttons.Heavy) heavies++;
            if (b == Buttons.Light) jabs++;
        }
        Assert.True(heavy ? heavies > 0 && jabs == 0 : heavies == 0 && jabs > 0, $"heavies {heavies}, jabs {jabs}");
    }

    [Fact]
    public void GetsOneDodgeRollPerAttackEvenIfTheAttackerFreezesMidWindow()
    {
        // After the press frame has passed, a later frame of the same attack (as after a freeze
        // that moved the roll's key) never brings a dodge.
        MoveData heavy = Rules.Tuning.Moves.NeutralHeavy;
        int pressAt = heavy.Startup - 3;
        for (int start = 0; start < 300; start++)
        for (int a = pressAt + 1; a < heavy.Startup; a++)
        {
            GameState s = Attacking(MoveId.NeutralHeavy, a, start + a);
            Assert.Equal(Buttons.None, SparringBot.Think(s, Rules, 0) & Buttons.Dodge);
        }
    }

    [Fact]
    public void WaitsForAnAttackerFrozenInHitStop()
    {
        for (int start = 0; start < 200; start++)
        {
            GameState s = Attacking(MoveId.NeutralHeavy, Rules.Tuning.Moves.NeutralHeavy.Startup - 3, start);
            s.Fighters[1].HitStop = 2;
            Assert.Equal(Buttons.None, SparringBot.Think(s, Rules, 0) & Buttons.Dodge);
        }
    }

    /// <summary>P2 hanging in a ground slam's startup (or plunging) at (x, y); P1 standing at (meX, meY).</summary>
    private static GameState Slamming(int xCm, int yCm, int meXCm, int meYCm, int actionFrame, int frame)
    {
        GameState s = Standing(0, 0);
        s.Frame = frame;
        ref Fighter me = ref s.Fighters[0];
        me.Position = new FixVec2(Fix.FromRatio(meXCm, 100), Fix.FromRatio(meYCm, 100));
        ref Fighter op = ref s.Fighters[1];
        op.Position = new FixVec2(Fix.FromRatio(xCm, 100), Fix.FromRatio(yCm, 100));
        op.Grounded = false;
        op.Action = FighterAction.Attack;
        op.Move = MoveId.GroundSlam;
        op.ActionFrame = actionFrame;
        return s;
    }

    [Fact]
    public void IgnoresASlamThatWillLandOnAPlatformAboveIt()
    {
        // The right platform (top 2.75 m) is between a slam dropping from 6 m and the bot standing
        // under it. The same slam with the bot out in the open, at x = 0, is dodged: that shows
        // these states do reach the press window, so it's the platform that makes the difference.
        MoveData slam = Rules.Tuning.Moves.GroundSlam;
        foreach (CpuLevel level in new[] { CpuLevel.Easy, CpuLevel.Normal, CpuLevel.Hard })
        {
            int underPlatform = 0, inTheOpen = 0;
            for (int start = 0; start < 100; start++)
            for (int a = 0; a < slam.Startup + 8; a++) // still above the platform
            {
                int y = a < slam.Startup ? 600 : 600 - 40 * (a - slam.Startup + 1);
                if ((SparringBot.Think(Slamming(400, y, 400, 0, a, start + a), Rules, 0, level) & Buttons.Dodge) != 0) underPlatform++;
                if ((SparringBot.Think(Slamming(0, y, 0, 0, a, start + a), Rules, 0, level) & Buttons.Dodge) != 0) inTheOpen++;
            }
            Assert.Equal(0, underPlatform);
            Assert.True(inTheOpen > 0, $"{level} never dodged the open slam either");
        }
    }

    [Fact]
    public void ACpuUnderAPlatformDoesNotDodgeASlamThatLandsOnIt()
    {
        // End to end: the slam drops from 6 m onto the right platform; the Hard CPU stands under it.
        for (int start = 0; start < 60; start++)
        {
            GameState s = Standing(0, 0);
            s.Frame = start;
            s.Fighters[0].Position = new FixVec2(Fix.FromInt(4), Fix.Zero);
            ref Fighter op = ref s.Fighters[1];
            op.Position = new FixVec2(Fix.FromInt(4), Fix.FromInt(6));
            op.Grounded = false;
            var inputs = new Buttons[2];
            inputs[1] = Buttons.Heavy | Buttons.Down;
            bool landedOnPlatform = false;
            for (int f = 0; f < 40 && s.Fighters[1].Action == FighterAction.Attack || f == 0; f++)
            {
                inputs[0] = SparringBot.Think(s, Rules, 0, CpuLevel.Hard);
                Assert.Equal(Buttons.None, inputs[0] & Buttons.Dodge);
                Simulation.Advance(s, Rules, inputs);
                inputs[1] = Buttons.None;
                if (s.Fighters[1].Grounded && s.Fighters[1].Position.Y == Fix.FromRatio(11, 4)) landedOnPlatform = true;
            }
            Assert.True(landedOnPlatform, "the slam should land on the platform");
            Assert.Equal(0, s.Fighters[0].Josh);
        }
    }

    [Fact]
    public void IgnoresASlamThatStartsBelowIt()
    {
        // The bot stands on the right platform; the slam starts underneath it at 1 m and can only go down.
        foreach (CpuLevel level in new[] { CpuLevel.Easy, CpuLevel.Normal, CpuLevel.Hard })
        for (int start = 0; start < 100; start++)
        for (int a = 0; a < 20; a++)
        {
            GameState s = Slamming(400, 100, 400, 275, a, start + a);
            Assert.Equal(Buttons.None, SparringBot.Think(s, Rules, 0, level) & Buttons.Dodge);
        }
    }

    [Fact]
    public void TimesItsDodgeToWhenAHighSlamArrives()
    {
        // From 6 m straight above: its hitbox (0.4 m below its feet) reaches the bot's head (1.8 m)
        // after 3.8 m of plunging, on attack frame 10 + floor(3.8 / 0.4) = 19. The parry press is 3 frames earlier.
        MoveData slam = Rules.Tuning.Moves.GroundSlam;
        int arrives = slam.Startup + 9;
        foreach (CpuLevel level in new[] { CpuLevel.Easy, CpuLevel.Normal, CpuLevel.Hard })
        {
            int dodges = 0;
            for (int start = 0; start < 300; start++)
            {
                for (int a = 0; a < arrives + 5; a++)
                {
                    // While plunging the slammer has dropped (a - startup + 1) frames' worth.
                    int y = a < slam.Startup ? 600 : 600 - 40 * (a - slam.Startup + 1);
                    GameState s = Slamming(0, y, 0, 0, a, start + a);
                    if ((SparringBot.Think(s, Rules, 0, level) & Buttons.Dodge) == 0) continue;
                    Assert.True(a == arrives - 3, $"{level} first dodged on slam frame {a}");
                    dodges++;
                    break;
                }
            }
            Assert.True(dodges > 0, $"{level} never dodged");
        }
    }

    [Fact]
    public void AHighSlamThatTheHardCpuDodgesIsParried()
    {
        // End to end: the slammer drops from 6 m onto the CPU; whenever the CPU dodges, it parries.
        int parries = 0, dodgedButHit = 0;
        for (int start = 0; start < 120; start++)
        {
            GameState s = Standing(0, 0);
            s.Frame = start;
            ref Fighter op = ref s.Fighters[1];
            op.Position = new FixVec2(Fix.Zero, Fix.FromInt(6));
            op.Grounded = false;
            var inputs = new Buttons[2];
            inputs[1] = Buttons.Heavy | Buttons.Down;
            bool dodged = false;
            for (int f = 0; f < 60; f++)
            {
                inputs[0] = SparringBot.Think(s, Rules, 0, CpuLevel.Hard);
                if ((inputs[0] & Buttons.Dodge) != 0) dodged = true;
                Simulation.Advance(s, Rules, inputs);
                inputs[1] = Buttons.None;
                if (s.Fighters[1].Action == FighterAction.Stunned) { parries++; break; }
                if (s.Fighters[0].Josh > 0) { if (dodged) dodgedButHit++; break; }
            }
        }
        Assert.True(parries > 0);
        Assert.Equal(0, dodgedButHit);
    }

    [Fact]
    public void WhichAttacksEachLevelCanDodge()
    {
        // Easy (10-frame reaction) and Normal (6) can't react to the up heavy (startup 6) or the jab (4); Hard (3) can.
        var cases = new (MoveId Move, bool Easy, bool Normal, bool Hard)[]
        {
            (MoveId.Jab, false, false, true),
            (MoveId.UpHeavy, false, false, true),
            (MoveId.NeutralHeavy, true, true, true),
            (MoveId.SideHeavy, true, true, true),
            (MoveId.DownHeavy, true, true, true),
        };
        foreach (var c in cases)
        {
            MoveData m = Rules.Tuning.Moves[c.Move];
            foreach ((CpuLevel level, bool expected) in new[] { (CpuLevel.Easy, c.Easy), (CpuLevel.Normal, c.Normal), (CpuLevel.Hard, c.Hard) })
            {
                bool ever = false;
                for (int start = 0; start < 100 && !ever; start++)
                    for (int a = 0; a < m.Startup && !ever; a++)
                    {
                        GameState s = Attacking(c.Move, a, start + a);
                        if (c.Move == MoveId.UpHeavy) s.Fighters[1].Position = new FixVec2(Fix.FromRatio(1, 2), Fix.Zero);
                        ever = (SparringBot.Think(s, Rules, 0, level) & Buttons.Dodge) != 0;
                    }
                Assert.True(ever == expected, $"{level} vs {c.Move}: dodges = {ever}");
            }
        }

        // Ground slams: Easy needs the slammer to start ~0.8 m or more above its head (1.8 m tall).
        foreach ((int y, bool easy) in new[] { (230, false), (280, true) })
        foreach ((CpuLevel level, bool expected) in new[] { (CpuLevel.Easy, easy), (CpuLevel.Normal, true), (CpuLevel.Hard, true) })
        {
            MoveData slam = Rules.Tuning.Moves.GroundSlam;
            bool ever = false;
            for (int start = 0; start < 100 && !ever; start++)
                for (int a = 0; a < slam.Startup + 4 && !ever; a++)
                {
                    int at = a < slam.Startup ? y : y - 40 * (a - slam.Startup + 1);
                    GameState s = Slamming(30, at, 0, 0, a, start + a);
                    // Once the plunging hitbox overlaps the bot it has already hit: no later state exists.
                    if (a >= slam.Startup && Simulation.MoveHitbox(in s.Fighters[1], slam).Overlaps(Simulation.Hurtbox(in s.Fighters[0], Rules.Tuning))) break;
                    ever = (SparringBot.Think(s, Rules, 0, level) & Buttons.Dodge) != 0;
                }
            Assert.True(ever == expected, $"{level} vs a slam from {y} cm: dodges = {ever}");
        }
    }

    [Fact]
    public void HeavyPunishesALandedSlamFromItsLastActiveFrame()
    {
        // The state just after a slam landed and missed: on its last active frame, standing 1.1 m
        // in front of the bot. 15 frames until it can act: the neutral heavy (14) fits, the side heavy (16) doesn't.
        MoveData slam = Rules.Tuning.Moves.GroundSlam;
        GameState s = Attacking(MoveId.GroundSlam, slam.Startup + slam.Active - 1, 0);
        s.Fighters[1].Position = new FixVec2(Fix.FromRatio(11, 10), Fix.Zero);
        int heavies = 0;
        for (int f = 0; f < 40; f++)
        {
            s.Frame = f;
            Buttons b = SparringBot.Think(s, Rules, 0);
            Assert.True(b == Buttons.None || b == Buttons.Heavy, $"frame {f}: {b}");
            if (b == Buttons.Heavy) heavies++;
        }
        Assert.True(heavies > 0);

        // One frame later only 14 frames are left: a heavy pressed now would be dodged, so it jabs.
        s.Fighters[1].ActionFrame++;
        for (int f = 0; f < 40; f++)
        {
            s.Frame = f;
            Assert.Equal(Buttons.None, SparringBot.Think(s, Rules, 0) & Buttons.Heavy);
        }
    }

    [Fact]
    public void DodgesAHangingSlamItIsRunningInto()
    {
        // End to end: a slam hangs at 4 m while the Hard CPU runs in from 3.2 m away. When it decides
        // (once), it is still outside the slam's reach, but it will have run into it by the time the
        // slam comes down; it should still dodge about 1 slam in 2.
        int dodged = 0, runs = 0;
        for (int start = 0; start < 200; start++)
        {
            GameState s = Standing(0, 0);
            s.Frame = start;
            s.Fighters[0].Position = new FixVec2(Fix.FromRatio(-16, 5), Fix.Zero);
            ref Fighter op = ref s.Fighters[1];
            op.Position = new FixVec2(Fix.Zero, Fix.FromInt(4));
            op.Grounded = false;
            var inputs = new Buttons[2];
            inputs[1] = Buttons.Heavy | Buttons.Down;
            bool dodge = false;
            for (int f = 0; f < 40 && s.Fighters[1].Action == FighterAction.Attack || f == 0; f++)
            {
                inputs[0] = SparringBot.Think(s, Rules, 0, CpuLevel.Hard);
                if ((inputs[0] & Buttons.Dodge) != 0) dodge = true;
                Simulation.Advance(s, Rules, inputs);
                inputs[1] = Buttons.None;
            }
            runs++;
            if (dodge) dodged++;
        }
        Assert.InRange(dodged, runs / 4, runs * 3 / 4);
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
