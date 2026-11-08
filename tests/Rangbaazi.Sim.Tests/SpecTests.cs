using System.Globalization;
using Rangbaazi.Sim.Diagnostics;
using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim.Tests;

/// <summary>Keeps the numbers in docs/design/combat-spec-v0.1.md equal to what the sim does.</summary>
public class SpecTests
{
    private static string Spec() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "docs", "design", "combat-spec-v0.1.md")).Replace("\r\n", "\n");

    [Fact]
    public void SpecKoTableMatchesTheSim()
    {
        const string start = "<!-- ko-table:start -->", end = "<!-- ko-table:end -->";
        string spec = Spec();
        int from = spec.IndexOf(start, StringComparison.Ordinal);
        int to = spec.IndexOf(end, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from, "the spec has lost its KO table markers");
        string documented = spec.Substring(from + start.Length, to - from - start.Length).Trim();

        string actual = KoChart.MarkdownTable(new MatchRules { Stage = Stages.Graybox, Guts = false }).Trim();
        Assert.True(documented == actual, "The KO table in combat-spec-v0.1.md is out of date. Replace it with:\n" + actual);
    }

    private static readonly (string Name, MoveId Id)[] Moves =
    {
        ("Jab", MoveId.Jab), ("Neutral heavy", MoveId.NeutralHeavy), ("Side heavy", MoveId.SideHeavy),
        ("Up heavy", MoveId.UpHeavy), ("Down heavy", MoveId.DownHeavy), ("Ground slam", MoveId.GroundSlam),
    };

    /// <summary>The cells of the spec table row for this move that has exactly this many columns.</summary>
    private static string[] Row(string spec, string name, int columns)
    {
        string? row = spec.Split('\n').FirstOrDefault(l =>
            l.StartsWith($"| {name} | ", StringComparison.Ordinal) && l.Split('|').Length == columns + 2);
        Assert.True(row != null, $"no {columns}-column row for {name} in the spec");
        return row!.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>A number as the spec writes it: "0.156", "up to 40", "45°", "−0.3" (with a real minus sign).</summary>
    private static double Num(string cell) => double.Parse(
        cell.Replace("up to ", "").Replace("°", "").Replace('−', '-'), CultureInfo.InvariantCulture);

    private static double D(Fix f) => f.Raw / 65536.0;

    [Fact]
    public void SpecFrameDataMatchesTheMoveData()
    {
        // | Move | Startup | Active | Recovery | Total | Damage | Base KB | KB growth | Launch | Hitstun bonus | Hit-stop |
        var rules = new MatchRules { Stage = Stages.Graybox };
        string spec = Spec();
        foreach ((string name, MoveId id) in Moves)
        {
            MoveData m = rules.Tuning.Moves[id];
            string[] c = Row(spec, name, 11);
            Assert.Equal(m.Startup, Num(c[1]));
            Assert.Equal(m.Active, Num(c[2]));
            Assert.Equal(m.Recovery, Num(c[3]));
            Assert.Equal(m.TotalFrames, Num(c[4]));
            Assert.Equal(m.Damage, Num(c[5]));
            Assert.True(Math.Abs(D(m.BaseKnockback) - Num(c[6])) < 0.0005, $"{name} base knockback: spec {c[6]}, code {D(m.BaseKnockback)}");
            // Within one fixed-point step (1/65536): 3/1000 is stored as 196/65536.
            Assert.True(Math.Abs(D(m.KnockbackGrowth) - Num(c[7])) < 0.00002, $"{name} knockback growth: spec {c[7]}, code {D(m.KnockbackGrowth)}");
            double degrees = Math.Atan2(D(m.LaunchY), D(m.LaunchX)) * 180 / Math.PI;
            Assert.True(Math.Abs(degrees - Num(c[8])) < 0.5, $"{name} launch angle: spec {c[8]}, code {degrees:0.0}");
            Assert.Equal(m.HitstunBonus, Num(c[9]));
            Assert.Equal(Simulation.HitStopFrames(m.Damage, rules), Num(c[10]));
        }
    }

    [Fact]
    public void SpecHitboxesMatchTheMoveData()
    {
        // | Move | x | y |, each "from to to" in metres from the feet, x forward.
        var rules = new MatchRules { Stage = Stages.Graybox };
        string spec = Spec();
        foreach ((string name, MoveId id) in Moves)
        {
            MoveData m = rules.Tuning.Moves[id];
            string[] c = Row(spec, name, 3);
            double[] x = c[1].Split(" to ").Select(Num).ToArray();
            double[] y = c[2].Split(" to ").Select(Num).ToArray();
            double near = m.HitsBothSides ? -D(m.HitboxFar) : D(m.HitboxNear);
            Assert.True(Math.Abs(x[0] - near) < 0.0005 && Math.Abs(x[1] - D(m.HitboxFar)) < 0.0005, $"{name} hitbox x: spec {c[1]}");
            Assert.True(Math.Abs(y[0] - D(m.HitboxBottom)) < 0.0005 && Math.Abs(y[1] - D(m.HitboxTop)) < 0.0005, $"{name} hitbox y: spec {c[2]}");
        }
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Rangbaazi.sln"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Rangbaazi.sln not found above the test directory.");
    }
}
