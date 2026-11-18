using System.Collections.Generic;
using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim;

/// <summary>
/// Immutable arena data (not part of rollback state). Platforms are land-on-top surfaces;
/// leaving <see cref="BlastZone"/> is a ring-out (GDD B2 "Damage &amp; KO model").
/// </summary>
public enum HazardKind : byte
{
    /// <summary>A spring, bounce pad or steam vent: flings a fighter standing on it into the air.</summary>
    Launch,
    /// <summary>A shutter, crusher, laser or burner: hits whoever is in it.</summary>
    Hit,
    /// <summary>A gust, conveyor or current: carries whoever is in it sideways.</summary>
    Push,
}

/// <summary>
/// One arena hazard: a zone on a platform that cycles idle, warning, active, cooldown (Hazards).
/// Direction is the way a Push carries fighters (+1 right, -1 left).
/// </summary>
public readonly record struct Hazard(HazardKind Kind, FixRect Zone, int Direction);

/// <summary>
/// The hazards' shared clock and numbers. A hazard's phase is a pure function of the match frame
/// (each one offset from the last), so hazards add nothing to the rollback state.
/// </summary>
public static class Hazards
{
    public const int Period = 600;       // a 10 s cycle
    public const int WarnAt = 420;       // 0.75 s of warning ...
    public const int ActiveAt = 465;     // ... then 1 s active ...
    public const int CooldownAt = 525;   // ... then cooling down until the cycle restarts
    public const int Stagger = 200;      // each hazard runs this many frames ahead of the one before
    public const int HitEvery = 20;      // a Hit hazard strikes on these frames while active

    public static readonly Fix LaunchSpeed = Fix.FromRatio(36, 100);
    public static readonly Fix PushSpeed = Fix.FromRatio(7, 100);

    /// <summary>What a Hit hazard does to a fighter: 8 damage, launched up and away at 60 degrees.</summary>
    public static readonly MoveData Strike = new()
    {
        Damage = 8, BaseKnockback = Fix.FromRatio(15, 100), KnockbackGrowth = Fix.FromRatio(22, 10000),
        LaunchX = Fix.FromRatio(5000, 10000), LaunchY = Fix.FromRatio(8660, 10000),
        HitstunBonus = 2,
    };

    /// <summary>Where hazard <paramref name="index"/> is in its cycle on this frame: 0 to Period - 1.</summary>
    public static int CycleAt(int frame, int index) => (frame + index * Stagger) % Period;

    public static bool IsActive(int frame, int index)
    {
        int at = CycleAt(frame, index);
        return at >= ActiveAt && at < CooldownAt;
    }
}

public sealed class StageDefinition
{
    public required string Id { get; init; }
    public required IReadOnlyList<FixRect> Platforms { get; init; }
    public required IReadOnlyList<FixVec2> SpawnPoints { get; init; }
    public required FixRect BlastZone { get; init; }

    /// <summary>The arena's hazards (none on the test stages); on only when MatchRules.Hazards is.</summary>
    public IReadOnlyList<Hazard> Hazards { get; init; } = System.Array.Empty<Hazard>();
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

    /// <summary>
    /// Rookie Rooftops (Shan's arena), the default room arena: see <see cref="ArenaStages"/>, which
    /// holds all 31 arenas converted from the art pack's layouts.
    /// </summary>
    public static StageDefinition Rooftops => ArenaStages.ById("rooftops")!;
}
