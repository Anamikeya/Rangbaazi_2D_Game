using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim.Tests;

internal static class TestStages
{
    /// <summary>A 1 km floor with a far-away blast zone, so knockback never rings anyone out.</summary>
    public static readonly StageDefinition Wide = new()
    {
        Id = "test-wide",
        Platforms = new[] { new FixRect(Fix.FromInt(-500), Fix.FromInt(-2), Fix.FromInt(500), Fix.Zero) },
        SpawnPoints = new[]
        {
            new FixVec2(Fix.FromInt(-2), Fix.Zero), new FixVec2(Fix.FromInt(2), Fix.Zero),
            new FixVec2(Fix.FromInt(-4), Fix.Zero), new FixVec2(Fix.FromInt(4), Fix.Zero),
        },
        BlastZone = new FixRect(Fix.FromInt(-1000), Fix.FromInt(-100), Fix.FromInt(1000), Fix.FromInt(1000)),
    };
}
