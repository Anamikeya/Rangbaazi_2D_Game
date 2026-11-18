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
    /// <summary>A scripted match. <c>TimeLimit</c> &gt; 0 plays it under room rules on Rooftops (timed, rage, second wind).</summary>
    private sealed record Scenario(string Name, int Players, ulong Seed, int Frames, bool Guts, CpuLevel[]? Bots, ulong InputSeed, int TimeLimit = 0,
        bool Weapons = false, WeaponId[]? Signatures = null);

    private static readonly WeaponId[] SomeSignatures =
    {
        WeaponId.Uzi, WeaponId.Bow, WeaponId.None, WeaponId.BowlingBall, WeaponId.BaseballBat, WeaponId.CherryBombs,
    };

    private static readonly Scenario[] Scenarios =
    {
        new("random_2p", 2, 42, 1800, true, null, 3),
        new("random_4p", 4, 7, 1200, true, null, 11),
        new("bots_normal_vs_hard", 2, 1, 3000, true, new[] { CpuLevel.Normal, CpuLevel.Hard }, 0),
        new("bots_hard_vs_hard_no_guts", 2, 5, 3000, false, new[] { CpuLevel.Hard, CpuLevel.Hard }, 0),
        new("bots_easy_vs_normal", 2, 9, 2400, true, new[] { CpuLevel.Easy, CpuLevel.Normal }, 0),
        new("room_random_15p", 15, 3, 2460, true, null, 17, TimeLimit: 2400, Weapons: true, Signatures: SomeSignatures),
        new("room_bots_6p", 6, 21, 2760, true, new[] { CpuLevel.Hard, CpuLevel.Hard, CpuLevel.Normal, CpuLevel.Normal, CpuLevel.Easy, CpuLevel.Hard }, 0, TimeLimit: 2700,
            Weapons: true, Signatures: SomeSignatures),
        new("weapons_random_4p", 4, 31, 2400, true, null, 23, Weapons: true, Signatures: SomeSignatures),
        new("weapons_bots_4p", 4, 8, 3600, true, new[] { CpuLevel.Hard, CpuLevel.Normal, CpuLevel.Hard, CpuLevel.Easy }, 0,
            Weapons: true, Signatures: SomeSignatures),
    };

    /// <summary>The rules a scenario plays under; room scenarios copy <see cref="MatchRules.Room"/> with a short clock.</summary>
    private static MatchRules RulesFor(Scenario s)
    {
        if (s.TimeLimit <= 0) return new MatchRules { Stage = Stages.Graybox, Guts = s.Guts, Weapons = s.Weapons, Signatures = s.Signatures };
        MatchRules room = MatchRules.Room(Stages.Rooftops, 1);
        return new MatchRules
        {
            Stage = room.Stage,
            Guts = s.Guts,
            TimeLimitFrames = s.TimeLimit,
            RagePercent = room.RagePercent,
            RespawnShield = room.RespawnShield,
            Weapons = s.Weapons,
            Signatures = s.Signatures,
        };
    }

    private static string Trace(Scenario s)
    {
        MatchRules rules = RulesFor(s);
        GameState state = Simulation.CreateInitialState(rules, s.Players, s.Seed);
        var inputs = new Buttons[s.Players];
        var json = new StringBuilder();
        json.Append("{\n");
        json.Append($"  \"name\": \"{s.Name}\",\n  \"players\": {s.Players},\n  \"seed\": {s.Seed},\n  \"frames\": {s.Frames},\n");
        json.Append($"  \"guts\": {(s.Guts ? "true" : "false")},\n");
        json.Append($"  \"stage\": \"{rules.Stage.Id}\",\n  \"time_limit_frames\": {rules.TimeLimitFrames},\n");
        json.Append($"  \"weapons\": {(rules.Weapons ? "true" : "false")},\n");
        json.Append($"  \"signatures\": [{string.Join(", ", (rules.Signatures ?? Array.Empty<WeaponId>()).Select(w => ((int)w).ToString()))}],\n");
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

    /// <summary>
    /// A hash of every arena stage, in the order StageDefinition.add_to hashes one in GDScript:
    /// each platform's min x, min y, max x, max y, then the spawn points, then the blast zone.
    /// </summary>
    private static string ArenaStageHashes()
    {
        var json = new StringBuilder("{\n");
        for (int i = 0; i < ArenaStages.All.Count; i++)
        {
            StageDefinition s = ArenaStages.All[i];
            var h = new StateHasher();
            foreach (var p in s.Platforms)
            {
                h.Add(p.MinX.Raw);
                h.Add(p.MinY.Raw);
                h.Add(p.MaxX.Raw);
                h.Add(p.MaxY.Raw);
            }
            foreach (var sp in s.SpawnPoints)
            {
                h.Add(sp.X.Raw);
                h.Add(sp.Y.Raw);
            }
            h.Add(s.BlastZone.MinX.Raw);
            h.Add(s.BlastZone.MinY.Raw);
            h.Add(s.BlastZone.MaxX.Raw);
            h.Add(s.BlastZone.MaxY.Raw);
            json.Append($"  \"{s.Id}\": \"{h.Value:X16}\"{(i + 1 < ArenaStages.All.Count ? "," : "")}\n");
        }
        return json.Append("}\n").ToString();
    }

    [Fact]
    public void ArenaStageGoldenMatchesTheSim()
    {
        string path = Path.Combine(RepoRoot(), "client", "tests", "golden", "arena_stages.json");
        string hashes = ArenaStageHashes();
        if (Environment.GetEnvironmentVariable("RANGBAAZI_WRITE_GOLDENS") == "1")
        {
            File.WriteAllText(path, hashes);
            return;
        }
        Assert.True(File.Exists(path) && File.ReadAllText(path).Replace("\r\n", "\n") == hashes,
            "arena_stages.json doesn't match ArenaStages; rerun tools/gen_arena_stages.py and rewrite the goldens");
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Rangbaazi.sln"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Rangbaazi.sln not found above the test directory.");
    }
}
