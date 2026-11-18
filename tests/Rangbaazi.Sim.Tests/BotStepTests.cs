using Rangbaazi.Sim.Diagnostics;
using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim.Tests;

/// <summary>
/// The CPU and low steps. Found by playing: with the opponent standing on a step between 1 m and
/// 1.5 m up, the bot stood underneath doing nothing (too high to attack, too low to jump after).
/// </summary>
public class BotStepTests
{
    [Theory]
    [InlineData(CpuLevel.Easy)]
    [InlineData(CpuLevel.Normal)]
    [InlineData(CpuLevel.Hard)]
    public void TheBotJumpsAfterAnOpponentOnALowStep(CpuLevel level)
    {
        var rules = new MatchRules { Stage = Stages.Rooftops };
        // Rooftops' right-hand jump step: x 12 to 15 m, its top 1.375 m above the floor.
        FixRect step = rules.Stage.Platforms.First(p => p.MaxY == Fix.FromRatio(11, 8) && p.MinX == Fix.FromInt(12));
        var s = Simulation.CreateInitialState(rules, 2, 1);
        ref Fighter bot = ref s.Fighters[0];
        bot.Position = new FixVec2(Fix.FromInt(13), Fix.Zero);
        bot.Velocity = FixVec2.Zero;
        bot.Grounded = true;
        bot.Facing = 1;
        ref Fighter op = ref s.Fighters[1];
        op.Position = new FixVec2(Fix.FromInt(13), step.MaxY);
        op.Velocity = FixVec2.Zero;
        op.Grounded = true;

        Buttons pressed = SparringBot.Think(s, rules, 0, level);
        Assert.True((pressed & Buttons.Jump) != 0, $"the bot pressed {pressed} under an opponent {step.MaxY} m up");
    }
}
