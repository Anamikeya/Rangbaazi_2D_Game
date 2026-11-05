using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Rangbaazi.Sim.Tests;

/// <summary>
/// The BannedApiAnalyzer (src/Rangbaazi.Sim/BannedSymbols.txt) blocks System.Math, System.Random,
/// wall-clock APIs etc. at compile time, but it does not see the float/double keywords.
/// These tests close that gap.
/// </summary>
public partial class DeterminismGuardTests
{
    [Fact]
    public void SimSourceContainsNoFloatingPoint()
    {
        string simDir = Path.Combine(RepoRoot(), "src", "Rangbaazi.Sim");
        var offenders = new List<string>();

        foreach (string file in Directory.EnumerateFiles(simDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            string code = StripCommentsAndStrings(File.ReadAllText(file));
            foreach (Match m in FloatPattern().Matches(code))
                offenders.Add($"{Path.GetFileName(file)}: '{m.Value}'");
        }

        Assert.True(offenders.Count == 0, "Floating point found in sim:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void SimAssemblyExposesNoFloatingPointMembers()
    {
        Type[] banned = { typeof(float), typeof(double), typeof(decimal) };
        var offenders = new List<string>();
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (Type t in typeof(Simulation).Assembly.GetTypes())
        {
            if (t.IsDefined(typeof(CompilerGeneratedAttribute))) continue;
            foreach (FieldInfo f in t.GetFields(all))
                if (banned.Contains(f.FieldType)) offenders.Add($"{t.Name}.{f.Name}");
            foreach (MethodInfo m in t.GetMethods(all))
            {
                if (banned.Contains(m.ReturnType)) offenders.Add($"{t.Name}.{m.Name}() return");
                foreach (ParameterInfo p in m.GetParameters())
                    if (banned.Contains(p.ParameterType)) offenders.Add($"{t.Name}.{m.Name}({p.Name})");
            }
        }

        Assert.True(offenders.Count == 0, "Floating point members in sim:\n" + string.Join("\n", offenders));
    }

    private static string StripCommentsAndStrings(string code)
    {
        code = Regex.Replace(code, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        code = Regex.Replace(code, @"//[^\n]*", " ");
        code = Regex.Replace(code, "\\$?@?\"(?:[^\"\\\\]|\\\\.)*\"", "\"\"");
        return code;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Rangbaazi.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find Rangbaazi.sln above test output.");
    }

    // float/double/decimal keywords, System.Single/Double, and literals like 1.5, 2f, 3d, 1e5.
    [GeneratedRegex(@"\b(float|double|decimal|Single|Double|MathF)\b|\b\d+\.\d+([eE][+-]?\d+)?[fFdDmM]?\b|\b\d+[fFdDmM]\b|\b\d+[eE][+-]?\d+\b")]
    private static partial Regex FloatPattern();
}
