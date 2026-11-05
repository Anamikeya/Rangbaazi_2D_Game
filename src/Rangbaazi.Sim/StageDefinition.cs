using System.Collections.Generic;
using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim;

/// <summary>
/// Immutable arena data (not part of rollback state). Platforms are land-on-top surfaces;
/// leaving <see cref="BlastZone"/> is a ring-out (GDD B2 "Damage &amp; KO model").
/// </summary>
public sealed class StageDefinition
{
    public required string Id { get; init; }
    public required IReadOnlyList<FixRect> Platforms { get; init; }
    public required IReadOnlyList<FixVec2> SpawnPoints { get; init; }
    public required FixRect BlastZone { get; init; }
}

public static class Stages
{
    /// <summary>
    /// Graybox test arena: one main stage plus two floating platforms. Roughly Chawl Courtyard sized.
    /// Units are metres, origin at the centre of the main stage top surface.
    /// </summary>
    public static readonly StageDefinition Graybox = new()
    {
        Id = "graybox",
        Platforms = new[]
        {
            new FixRect(Fix.FromInt(-9), Fix.FromInt(-2), Fix.FromInt(9), Fix.Zero),
            new FixRect(Fix.FromInt(-6), Fix.FromRatio(5, 2), Fix.FromInt(-2), Fix.FromRatio(11, 4)),
            new FixRect(Fix.FromInt(2), Fix.FromRatio(5, 2), Fix.FromInt(6), Fix.FromRatio(11, 4)),
        },
        SpawnPoints = new[]
        {
            // Over open main-stage ground (clear of the floating platforms) so everyone lands at y = 0.
            new FixVec2(Fix.FromRatio(-15, 2), Fix.FromInt(3)),
            new FixVec2(Fix.FromRatio(15, 2), Fix.FromInt(3)),
            new FixVec2(Fix.FromInt(-1), Fix.FromInt(3)),
            new FixVec2(Fix.FromInt(1), Fix.FromInt(3)),
        },
        BlastZone = new FixRect(Fix.FromInt(-18), Fix.FromInt(-10), Fix.FromInt(18), Fix.FromInt(14)),
    };
}
