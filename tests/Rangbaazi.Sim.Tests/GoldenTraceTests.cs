using System.Text;
using Rangbaazi.Sim.Diagnostics;

namespace Rangbaazi.Sim.Tests;

/// <summary>
/// Golden traces for the GDScript port of the sim (client/sim): scripted matches with every
/// frame's inputs and the checksum after it, written to client/tests/golden. The GDScript tests
/// replay them and must match bit for bit, which proves the port behaves exactly like this
/// reference implementation. Run with RANGBAAZI_WRITE_GOLDENS=1 to (re)write the files; otherwise
/// this checks the files still match the C# sim.
/// </summary>
public class GoldenTraceTests
{
    private sealed record Scenario(string Name, int Players, ulong Seed, int Frames, bool Guts, CpuLevel[]? Bots, ulong InputSeed);

    private static readonly Scenario[] Scenarios =
    {
        new("random_2p", 2, 42, 1800, true, null, 3),
        new("random_4p", 4, 7, 1200, true, null, 11),
        new("bots_normal_vs_hard", 2, 1, 3000, true, new[] { CpuLevel.Normal, CpuLevel.Hard }, 0),
        new("bots_hard_vs_hard_no_guts", 2, 5, 3000, false, new[] { CpuLevel.Hard, CpuLevel.Hard }, 0),
        new("bots_easy_vs_normal", 2, 9, 2400, true, new[] { CpuLevel.Easy, CpuLevel.Normal }, 0),
    };

    private static string Trace(Scenario s)
    {
        var rules = new MatchRules { Stage = Stages.Graybox, Guts = s.Guts };
        GameState state = Simulation.CreateInitialState(rules, s.Players, s.Seed);
        var inputs = new Buttons[s.Players];
        var json = new StringBuilder();
        json.Append("{\n");
        json.Append($"  \"name\": \"{s.Name}\",\n  \"players\": {s.Players},\n  \"seed\": {s.Seed},\n  \"frames\": {s.Frames},\n");
        json.Append($"  \"guts\": {(s.Guts ? "true" : "false")},\n");
        json.Append(s.Bots == null ? "  \"bots\": null,\n" : $"  \"bots\": [{string.Join(", ", s.Bots.Select(b => $"\"{b.ToString().ToLowerInvariant()}\""))}],\n");
        json.Append($"  \"initial\": \"{state.Checksum():X16}\",\n");
        json.Append("  \"steps\": [\n");
        for (int f = 0; f < s.Frames; f++)
        {
            for (int p = 0; p < s.Players; p++)
            {
                inputs[p] = s.Bots != null
                    ? SparringBot.Think(state, rules, p, s.Bots[p])
                    : RandomInput(s.InputSeed, f, p);
            }
            Simulation.Advance(state, rules, inputs);
            json.Append($"    [[{string.Join(",", inputs.Select(b => ((int)b).ToString()))}], \"{state.Checksum():X16}\"]");
            json.Append(f + 1 < s.Frames ? ",\n" : "\n");
        }
        json.Append("  ]\n}\n");
        return json.ToString();
    }

    /// <summary>Button mashing that changes often, like DeterminismTests.</summary>
    private static Buttons RandomInput(ulong seed, int frame, int player)
    {
        var rng = new SimRandom(seed ^ ((ulong)frame * 0x9E3779B97F4A7C15UL) ^ ((ulong)player << 56));
        return (Buttons)(rng.NextUInt() & 0x7FF);
    }

    [Fact]
    public void GoldenTracesMatchTheSim()
    {
        string dir = Path.Combine(RepoRoot(), "client", "tests", "golden");
        bool write = Environment.GetEnvironmentVariable("RANGBAAZI_WRITE_GOLDENS") == "1";
        if (write) Directory.CreateDirectory(dir);
        foreach (Scenario s in Scenarios)
        {
            string path = Path.Combine(dir, s.Name + ".json");
            string trace = Trace(s);
            if (write)
            {
                File.WriteAllText(path, trace);
                continue;
            }
            Assert.True(File.Exists(path), $"{path} is missing; run the tests with RANGBAAZI_WRITE_GOLDENS=1");
            Assert.True(File.ReadAllText(path).Replace("\r\n", "\n") == trace,
                $"{s.Name}.json no longer matches the C# sim; if the change is intended, rewrite the goldens (RANGBAAZI_WRITE_GOLDENS=1) and port it to client/sim");
        }
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Rangbaazi.sln"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Rangbaazi.sln not found above the test directory.");
    }
}
