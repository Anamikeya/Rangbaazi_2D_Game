using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim.Tests;

public class SimulationTests
{
    private static readonly MatchRules Rules = new() { Stage = Stages.Graybox };

    private static void Run(GameState s, int frames, params Buttons[] inputs)
    {
        for (int i = 0; i < frames; i++) Simulation.Advance(s, Rules, inputs);
    }

    [Fact]
    public void FighterFallsAndLandsOnMainStage()
    {
        var s = Simulation.CreateInitialState(Rules, 1, 1);
        Run(s, 240, Buttons.None);

        Assert.True(s.Fighters[0].Grounded);
        Assert.Equal(Fix.Zero, s.Fighters[0].Position.Y);
    }

    [Fact]
    public void GroundJumpPlusOneAirJumpThenNoMore()
    {
        var s = Simulation.CreateInitialState(Rules, 1, 1);
        Run(s, 240, Buttons.None);

        Run(s, 1, Buttons.Jump);
        Assert.False(s.Fighters[0].Grounded);
        Run(s, 5, Buttons.None);

        Run(s, 1, Buttons.Jump);
        Assert.Equal(0, s.Fighters[0].AirJumpsLeft);
        Fix vyAfterAirJump = s.Fighters[0].Velocity.Y;
        Assert.True(vyAfterAirJump > Fix.Zero);

        Run(s, 1, Buttons.None);
        Run(s, 1, Buttons.Jump); // third press: no effect, velocity keeps decaying
        Assert.True(s.Fighters[0].Velocity.Y < vyAfterAirJump);
    }

    [Fact]
    public void HoldingJumpDoesNotRepeatJumps()
    {
        var s = Simulation.CreateInitialState(Rules, 1, 1);
        Run(s, 240, Buttons.None);
        Run(s, 30, Buttons.Jump);
        Assert.Equal(1, s.Fighters[0].AirJumpsLeft);
    }

    [Fact]
    public void WalkingOffTheEdgeIsARingOut()
    {
        var s = Simulation.CreateInitialState(Rules, 1, 1);
        for (int i = 0; i < 600 && s.Fighters[0].Stocks == Rules.Stocks; i++) Run(s, 1, Buttons.Right);

        Assert.Equal(Rules.Stocks - 1, s.Fighters[0].Stocks);
        Assert.Equal(FighterAction.Respawning, s.Fighters[0].Action);
        Assert.Equal(0, s.Fighters[0].Josh);
    }

    [Fact]
    public void LosingAllStocksEndsTheMatch()
    {
        var s = Simulation.CreateInitialState(Rules, 2, 1);
        // P1 walks off repeatedly; P2 stands still.
        for (int i = 0; i < 3000 && !s.MatchOver; i++) Simulation.Advance(s, Rules, new[] { Buttons.Left, Buttons.None });

        Assert.True(s.MatchOver);
        Assert.Equal(1, s.WinnerIndex);
        Assert.Equal(FighterAction.Eliminated, s.Fighters[0].Action);
    }

    [Fact]
    public void JabAddsJoshAndKnockbackScalesWithJosh()
    {
        Fix lowJoshSpeed = JabSpeedAt(josh: 0);
        Fix highJoshSpeed = JabSpeedAt(josh: 120);
        Assert.True(highJoshSpeed > lowJoshSpeed, $"{highJoshSpeed} should exceed {lowJoshSpeed}");
    }

    private static Fix JabSpeedAt(int josh)
    {
        var s = Simulation.CreateInitialState(Rules, 2, 1);
        Run(s, 240, Buttons.None, Buttons.None);

        // Place P1 just left of P2, both grounded, P1 facing right.
        s.Fighters[0].Position = new FixVec2(Fix.FromInt(0), Fix.Zero);
        s.Fighters[0].Facing = 1;
        s.Fighters[1].Position = new FixVec2(Fix.FromRatio(4, 5), Fix.Zero);
        s.Fighters[1].Josh = josh;

        Run(s, 1, Buttons.Light, Buttons.None);
        Run(s, Rules.Tuning.LightStartup + 1, Buttons.None, Buttons.None);

        Assert.Equal(josh + Rules.Tuning.LightDamage, s.Fighters[1].Josh);
        Assert.Equal(FighterAction.Hitstun, s.Fighters[1].Action);
        return s.Fighters[1].Velocity.Length;
    }
}
