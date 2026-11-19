using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim.Tests;

/// <summary>
/// The arenas' sides are the edges of the screen (the camera never shows past the 32 m of painted
/// arena), so the side blast zones sit just beyond them: a fighter is out once their body has gone
/// off the side by its own width, at 17.2 m from the middle.
/// </summary>
public class ScreenEdgeTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void AFighterJustOffTheSideOfTheScreenIsOut(int side)
    {
        var rules = new MatchRules { Stage = Stages.Rooftops };
        var s = Simulation.CreateInitialState(rules, 2, 1);
        ref Fighter f = ref s.Fighters[0];
        f.Position = new FixVec2(Fix.FromRatio(173 * side, 10), Fix.FromInt(3)); // 17.3 m out
        f.Velocity = FixVec2.Zero;
        f.Grounded = false;

        Simulation.Advance(s, rules, new Buttons[2]);
        Assert.Equal(1, s.Fighters[0].Falls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void AFighterStillPartlyOnScreenIsNotOut(int side)
    {
        var rules = new MatchRules { Stage = Stages.Rooftops };
        var s = Simulation.CreateInitialState(rules, 2, 1);
        ref Fighter f = ref s.Fighters[0];
        f.Position = new FixVec2(Fix.FromRatio(163 * side, 10), Fix.FromInt(3)); // 16.3 m: an edge of the body shows
        f.Velocity = FixVec2.Zero;
        f.Grounded = false;

        Simulation.Advance(s, rules, new Buttons[2]);
        Assert.Equal(0, s.Fighters[0].Falls);
    }

    [Fact]
    public void EveryArenaHasTheSameSidesJustPastItsFloor()
    {
        Fix edge = Fix.FromRatio(86, 5); // 16 + 0.4 (half a body) + 0.8 (a body)
        foreach (StageDefinition stage in ArenaStages.All)
        {
            Assert.Equal(edge, stage.BlastZone.MaxX);
            Assert.Equal(-edge, stage.BlastZone.MinX);
            foreach (FixRect p in stage.Platforms)
                Assert.True(p.MinX >= Fix.FromInt(-16) && p.MaxX <= Fix.FromInt(16), $"{stage.Id}: a platform runs off the screen");
        }
    }
}
