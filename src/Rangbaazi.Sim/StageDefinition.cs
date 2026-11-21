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

    /// <summary>Normal, or the big level a room of <see cref="StageScaling.BigFromPlayers"/> or more plays on.</summary>
    public StageSize Size { get; init; } = StageSize.Normal;
}

/// <summary>How big an arena is played: its own layout, or 1.5 times as wide (<see cref="StageScaling"/>).</summary>
public enum StageSize : byte { Normal = 0, Big = 1 }

/// <summary>
/// The big level (rooms v1, section 2.3): an arena's layout stretched in x only, by 3/2, as a pure
/// function of the stage, so it adds nothing to the rollback state. Platforms are 1.5 times as wide
/// at the same heights (the widest gap to a platform grows from 1.9 m to 2.85 m, inside a jump);
/// hazards keep their width on a centre moved out by 1.5; 15 spawn points 3 m apart; the side blast
/// zones 1.2 m past the 48 m floor's painted edge. The server picks the size once, at the start.
/// Same numbers as client/sim/stage_definition.gd (big).
/// </summary>
public static class StageScaling
{
    public const int BigFromPlayers = 9;
    public const int Num = 3, Den = 2;

    /// <summary>The order the 15 spawn slots are used in, so any first 9 to 15 spread over the floor.</summary>
    private static readonly int[] SpawnOrder = { 0, 14, 7, 3, 11, 1, 13, 5, 9, 2, 12, 6, 8, 4, 10 };

    private static readonly FixVec2[] BigSpawns = MakeSpawns();

    private static readonly FixRect BigBlast = new(Fix.FromRatio(-126, 5), Fix.FromInt(-10), Fix.FromRatio(126, 5), Fix.FromInt(22));

    /// <summary>Every arena stage's big level, in <see cref="ArenaStages.All"/>'s order.</summary>
    public static readonly IReadOnlyList<StageDefinition> BigAll = MakeBigAll();

    public static StageSize SizeFor(int players) => players >= BigFromPlayers ? StageSize.Big : StageSize.Normal;

    /// <summary>x times 3/2 on the raw value, truncated toward zero (as GDScript's int / is).</summary>
    public static Fix ScaleX(Fix x) => Fix.FromRaw(x.Raw * Num / Den);

    public static StageDefinition Big(StageDefinition s)
    {
        var platforms = new FixRect[s.Platforms.Count];
        for (int i = 0; i < platforms.Length; i++)
        {
            FixRect p = s.Platforms[i];
            platforms[i] = new FixRect(ScaleX(p.MinX), p.MinY, ScaleX(p.MaxX), p.MaxY);
        }
        var hazards = new Hazard[s.Hazards.Count];
        for (int i = 0; i < hazards.Length; i++)
        {
            Hazard h = s.Hazards[i];
            long c = (h.Zone.MinX.Raw + h.Zone.MaxX.Raw) / 2 * Num / Den;
            long half = (h.Zone.MaxX.Raw - h.Zone.MinX.Raw) / 2;
            hazards[i] = new Hazard(h.Kind, new FixRect(Fix.FromRaw(c - half), h.Zone.MinY, Fix.FromRaw(c + half), h.Zone.MaxY), h.Direction);
        }
        return new StageDefinition
        {
            Id = s.Id, Size = StageSize.Big, Platforms = platforms, SpawnPoints = BigSpawns, BlastZone = BigBlast, Hazards = hazards,
        };
    }

    private static FixVec2[] MakeSpawns()
    {
        var spawns = new FixVec2[SpawnOrder.Length];
        for (int i = 0; i < spawns.Length; i++) spawns[i] = new FixVec2(Fix.FromInt(-21 + 3 * SpawnOrder[i]), Fix.FromInt(2));
        return spawns;
    }

    private static StageDefinition[] MakeBigAll()
    {
        var all = new StageDefinition[ArenaStages.All.Count];
        for (int i = 0; i < all.Length; i++) all[i] = Big(ArenaStages.All[i]);
        return all;
    }
}

public static class Stages
{
    /// <summary>
    /// The stage with this id at this size ("graybox" or an arena id), or null. The big graybox
    /// isn't kept: rooms play arena stages only.
    /// </summary>
    public static StageDefinition? ById(string id, StageSize size = StageSize.Normal)
    {
        if (size == StageSize.Normal) return id == "graybox" ? Graybox : ArenaStages.ById(id);
        foreach (StageDefinition s in StageScaling.BigAll)
        {
            if (s.Id == id) return s;
        }
        return null;
    }

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
