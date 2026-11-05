namespace Rangbaazi.Sim.Diagnostics;

/// <summary>
/// The scripted smoke-test match: two <see cref="SparringBot"/>s on the graybox stage.
/// The Godot smoke test (<c>tools/godot.ps1 smoke</c>) plays it through real key events and
/// compares its final checksum with <see cref="Run"/> in the same process.
/// <c>SparringBotTests</c> checks that <see cref="Run"/> is reproducible and produces a fight.
/// No checksum is pinned, so neither side compares against another host or build.
/// </summary>
public static class SmokeScript
{
    public const int Players = 2;
    public const ulong Seed = 1;
    public const int Frames = 900; // 15 s at 60 Hz

    /// <summary>Frame on which the game sends one quick-chat line (presentation-side check only).</summary>
    public const int QuickChatFrame = 120;

    public static MatchRules CreateRules() => new() { Stage = Stages.Graybox };

    /// <summary>Fills <paramref name="inputs"/> with every player's bot input for the current frame.</summary>
    public static void Inputs(GameState state, MatchRules rules, Buttons[] inputs)
    {
        for (int p = 0; p < inputs.Length; p++) inputs[p] = SparringBot.Think(state, rules, p);
    }

    /// <summary>Runs the whole script headless and returns the final state.</summary>
    public static GameState Run(MatchRules rules, int frames = Frames)
    {
        GameState state = Simulation.CreateInitialState(rules, Players, Seed);
        var inputs = new Buttons[Players];
        for (int f = 0; f < frames; f++)
        {
            Inputs(state, rules, inputs);
            Simulation.Advance(state, rules, inputs);
        }
        return state;
    }
}
