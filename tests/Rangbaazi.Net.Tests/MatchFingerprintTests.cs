using System.Reflection;
using Rangbaazi.Sim;
using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Net.Tests;

public class MatchFingerprintTests
{
    private static MatchRules Default() => new() { Stage = Stages.Graybox };

    [Fact]
    public void EqualRulesGiveEqualFingerprints()
    {
        Assert.Equal(MatchFingerprint.Compute(Default()), MatchFingerprint.Compute(Default()));
        Assert.Equal(MatchFingerprint.Compute(Default()), MatchFingerprint.Compute(new MatchRules { Stage = Stages.Graybox, Tuning = new FighterTuning() }));
    }

    [Fact]
    public void EveryNumberOrSwitchInTheRulesChangesTheFingerprint()
    {
        ulong baseline = MatchFingerprint.RulesHash(Default());
        foreach (PropertyInfo property in typeof(MatchRules).GetProperties())
        {
            if (property.PropertyType != typeof(int) && property.PropertyType != typeof(bool)) continue;
            MatchRules rules = Default();
            object value = property.GetValue(rules)!;
            property.SetValue(rules, value is bool b ? !b : (int)value + 1);
            Assert.True(MatchFingerprint.RulesHash(rules) != baseline, $"MatchRules.{property.Name} doesn't change the fingerprint");
        }
    }

    [Fact]
    public void EveryTuningValueChangesTheFingerprint()
    {
        ulong baseline = MatchFingerprint.RulesHash(Default());
        foreach (PropertyInfo property in typeof(FighterTuning).GetProperties())
        {
            if (property.PropertyType != typeof(int) && property.PropertyType != typeof(Fix)) continue;
            var tuning = new FighterTuning();
            object value = property.GetValue(tuning)!;
            property.SetValue(tuning, value is Fix f ? f + Fix.FromRatio(1, 1000) : (int)value + 1);
            ulong changed = MatchFingerprint.RulesHash(new MatchRules { Stage = Stages.Graybox, Tuning = tuning });
            Assert.True(changed != baseline, $"FighterTuning.{property.Name} doesn't change the fingerprint");
        }
    }

    [Fact]
    public void AStageThatDiffersChangesTheFingerprint()
    {
        StageDefinition g = Stages.Graybox;
        var moved = new StageDefinition
        {
            Id = g.Id,
            Platforms = g.Platforms.Select((p, i) => i == 0 ? new FixRect(p.MinX, p.MinY, p.MaxX + Fix.One, p.MaxY) : p).ToArray(),
            SpawnPoints = g.SpawnPoints,
            BlastZone = g.BlastZone,
        };
        Assert.NotEqual(MatchFingerprint.Compute(Default()), MatchFingerprint.Compute(new MatchRules { Stage = moved }));
    }

    [Fact]
    public void GutsOnAndOffCannotPlayTogether() =>
        Assert.NotEqual(MatchFingerprint.Compute(Default()), MatchFingerprint.Compute(new MatchRules { Stage = Stages.Graybox, Guts = false }));
}
