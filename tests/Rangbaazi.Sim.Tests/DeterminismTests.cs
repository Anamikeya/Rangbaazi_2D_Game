namespace Rangbaazi.Sim.Tests;

/// <summary>
/// The guarantees rollback netcode needs: same inputs => same state, and save/load/re-simulate
/// reproduces the original timeline exactly.
/// </summary>
public class DeterminismTests
{
    private static readonly MatchRules Rules = new() { Stage = Stages.Graybox };
    private const int Players = 4;
    private const int Frames = 60 * 60; // one minute of play

    /// <summary>Pseudo-random but reproducible inputs (mash buttons, change direction often).</summary>
    private static Buttons[] InputsFor(int frame, ulong seed)
    {
        var rng = new SimRandom(seed ^ ((ulong)frame * 0x9E3779B97F4A7C15UL));
        var inputs = new Buttons[Players];
        for (int p = 0; p < Players; p++) inputs[p] = (Buttons)(rng.NextUInt() & 0x7FF);
        return inputs;
    }

    [Fact]
    public void SameInputsProduceIdenticalChecksumsEveryFrame()
    {
        var a = Simulation.CreateInitialState(Rules, Players, 42);
        var b = Simulation.CreateInitialState(Rules, Players, 42);

        for (int f = 0; f < Frames; f++)
        {
            var inputs = InputsFor(f, 1);
            Simulation.Advance(a, Rules, inputs);
            Simulation.Advance(b, Rules, inputs);
            Assert.Equal(a.Checksum(), b.Checksum());
        }
    }

    [Fact]
    public void DifferentInputsDiverge()
    {
        var a = Simulation.CreateInitialState(Rules, Players, 42);
        var b = Simulation.CreateInitialState(Rules, Players, 42);

        for (int f = 0; f < 600; f++)
        {
            Simulation.Advance(a, Rules, InputsFor(f, 1));
            Simulation.Advance(b, Rules, InputsFor(f, 2));
        }
        Assert.NotEqual(a.Checksum(), b.Checksum());
    }

    [Fact]
    public void RollbackResimulationMatchesOriginalTimeline()
    {
        // Timeline A: play straight through.
        var reference = Simulation.CreateInitialState(Rules, Players, 7);
        var checksums = new ulong[Frames];
        for (int f = 0; f < Frames; f++)
        {
            Simulation.Advance(reference, Rules, InputsFor(f, 3));
            checksums[f] = reference.Checksum();
        }

        // Timeline B: every 10 frames, mispredict 8 frames ahead, then roll back and correct.
        var live = Simulation.CreateInitialState(Rules, Players, 7);
        const int rollback = 8;
        for (int f = 0; f < Frames; f++)
        {
            if (f % 10 == 0 && f + rollback < Frames)
            {
                GameState saved = live.Clone();
                for (int k = 0; k < rollback; k++) Simulation.Advance(live, Rules, InputsFor(f + k, 999)); // wrong guess
                live.CopyFrom(saved);
            }
            Simulation.Advance(live, Rules, InputsFor(f, 3));
            Assert.Equal(checksums[f], live.Checksum());
        }
    }

    [Fact]
    public void CloneIsIndependent()
    {
        var a = Simulation.CreateInitialState(Rules, 2, 1);
        var b = a.Clone();
        Simulation.Advance(a, Rules, new[] { Buttons.Right, Buttons.Left });
        Assert.NotEqual(a.Checksum(), b.Checksum());
        Assert.Equal(0, b.Frame);
    }
}
