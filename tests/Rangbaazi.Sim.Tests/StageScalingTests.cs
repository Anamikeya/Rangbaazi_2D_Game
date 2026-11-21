using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim.Tests;

/// <summary>
/// The big level for rooms of 9 or more (docs/design/rooms-teams-view-ui-v1.md, sections 2.2, 2.3
/// and 6.1): every arena stretched in x by 3/2, hazards as wide as before, 15 spawn points and the
/// side blast zones at 25.2 m. The GDScript twin is checked through the "@big" stage hashes.
/// </summary>
public class StageScalingTests
{
    public static TheoryData<string> Ids()
    {
        var ids = new TheoryData<string>();
        foreach (StageDefinition s in ArenaStages.All) ids.Add(s.Id);
        return ids;
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Platforms_are_half_as_wide_again_at_the_same_heights_and_hazards_keep_their_width(string id)
    {
        StageDefinition normal = Stages.ById(id)!;
        StageDefinition big = Stages.ById(id, StageSize.Big)!;
        Assert.Equal(id, big.Id);
        Assert.Equal(StageSize.Big, big.Size);
        Assert.Equal(StageSize.Normal, normal.Size);
        Assert.Equal(normal.Platforms.Count, big.Platforms.Count);
        for (int i = 0; i < normal.Platforms.Count; i++)
        {
            FixRect a = normal.Platforms[i], b = big.Platforms[i];
            Assert.Equal(a.MinX.Raw * 3 / 2, b.MinX.Raw);
            Assert.Equal(a.MaxX.Raw * 3 / 2, b.MaxX.Raw);
            Assert.Equal(a.MinY, b.MinY);
            Assert.Equal(a.MaxY, b.MaxY);
        }
        Assert.Equal(Fix.FromInt(-24), big.Platforms[0].MinX); // the 48 m floor
        Assert.Equal(Fix.FromInt(24), big.Platforms[0].MaxX);

        Assert.Equal(normal.Hazards.Count, big.Hazards.Count);
        for (int i = 0; i < normal.Hazards.Count; i++)
        {
            Hazard a = normal.Hazards[i], b = big.Hazards[i];
            Assert.Equal(a.Kind, b.Kind);
            Assert.Equal(a.Direction, b.Direction);
            Assert.Equal(a.Zone.MinY, b.Zone.MinY);
            Assert.Equal(a.Zone.MaxY, b.Zone.MaxY);
            long width = a.Zone.MaxX.Raw - a.Zone.MinX.Raw;
            Assert.InRange(b.Zone.MaxX.Raw - b.Zone.MinX.Raw, width - 1, width); // the half truncates
            long centre = (a.Zone.MinX.Raw + a.Zone.MaxX.Raw) / 2;
            Assert.Equal(centre * 3 / 2, (b.Zone.MinX.Raw + b.Zone.MaxX.Raw) / 2);
        }
    }

    [Fact]
    public void Fifteen_spawns_spread_over_the_floor_three_metres_apart()
    {
        StageDefinition big = Stages.ById("rooftops", StageSize.Big)!;
        FixRect floor = big.Platforms[0];
        Assert.Equal(15, big.SpawnPoints.Count);
        var xs = big.SpawnPoints.Select(p => p.X).ToList();
        Assert.Equal(15, xs.Distinct().Count());
        foreach (FixVec2 p in big.SpawnPoints)
        {
            Assert.InRange(p.X.Raw, floor.MinX.Raw, floor.MaxX.Raw);
            Assert.Equal(Fix.FromInt(2), p.Y);
        }
        var sorted = xs.OrderBy(x => x.Raw).ToList();
        for (int i = 1; i < sorted.Count; i++) Assert.True(sorted[i] - sorted[i - 1] >= Fix.FromInt(3));
        var first9 = xs.Take(9).ToList();
        Assert.True(first9.Max() - first9.Min() >= Fix.FromInt(40), "the first 9 should span the floor");
        // Every first n from 9 to 15 keeps its fighters at least 3 m apart (they are all distinct slots).
        Assert.Equal(new[] { -21, 21, 0, -12, 12, -18, 18, -6, 6 }, first9.Select(x => (int)(x.Raw >> 16)).ToArray());
        // Every arena shares them.
        Assert.All(StageScaling.BigAll, s => Assert.Same(big.SpawnPoints, s.SpawnPoints));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void The_side_blast_zones_are_at_25_point_2_metres(int side)
    {
        var rules = new MatchRules { Stage = Stages.ById("rooftops", StageSize.Big)! };
        Assert.Equal(Fix.FromRatio(-126, 5), rules.Stage.BlastZone.MinX);
        Assert.Equal(Fix.FromRatio(126, 5), rules.Stage.BlastZone.MaxX);
        foreach ((int tenths, int falls) in new[] { (253, 1), (251, 0) })
        {
            var s = Simulation.CreateInitialState(rules, 2, 1);
            ref Fighter f = ref s.Fighters[0];
            f.Position = new FixVec2(Fix.FromRatio(tenths * side, 10), Fix.FromInt(3));
            f.Velocity = FixVec2.Zero;
            f.Grounded = false;
            Simulation.Advance(s, rules, new Buttons[2]);
            Assert.Equal(falls, s.Fighters[0].Falls);
        }
    }

    [Fact]
    public void The_normal_size_is_todays_stage_and_nine_players_make_it_big()
    {
        foreach (StageDefinition s in ArenaStages.All) Assert.Same(s, Stages.ById(s.Id, StageSize.Normal));
        Assert.Same(Stages.Rooftops, Stages.ById("rooftops"));
        Assert.Same(Stages.Graybox, Stages.ById("graybox"));
        Assert.Null(Stages.ById("nowhere"));
        Assert.Null(Stages.ById("nowhere", StageSize.Big));
        Assert.Equal(StageSize.Normal, StageScaling.SizeFor(1));
        Assert.Equal(StageSize.Normal, StageScaling.SizeFor(8));
        Assert.Equal(StageSize.Big, StageScaling.SizeFor(9));
        Assert.Equal(StageSize.Big, StageScaling.SizeFor(15));
        Assert.Equal(ArenaStages.All.Count, StageScaling.BigAll.Count);
        Assert.Same(StageScaling.BigAll[0], Stages.ById(ArenaStages.All[0].Id, StageSize.Big));
        // ScaleX truncates toward zero, as GDScript's int / does.
        Assert.Equal(-1L, StageScaling.ScaleX(Fix.FromRaw(-1)).Raw);
        Assert.Equal(1L, StageScaling.ScaleX(Fix.FromRaw(1)).Raw);
        Assert.Equal(-4L, StageScaling.ScaleX(Fix.FromRaw(-3)).Raw);
    }

    /// <summary>
    /// Every platform can be reached from the floor by a chain of steps, each at most 5.5 m up (the
    /// double jump's reach) across a horizontal gap of at most 4 m.
    /// </summary>
    [Theory]
    [MemberData(nameof(Ids))]
    public void Every_platform_of_the_big_level_is_reachable(string id)
    {
        StageDefinition big = Stages.ById(id, StageSize.Big)!;
        IReadOnlyList<FixRect> p = big.Platforms;
        Fix rise = Fix.FromRatio(11, 2), reach = Fix.FromInt(4);
        var reached = new bool[p.Count];
        reached[0] = true;
        for (bool grew = true; grew;)
        {
            grew = false;
            for (int i = 0; i < p.Count; i++)
            {
                if (reached[i]) continue;
                for (int j = 0; j < p.Count && !reached[i]; j++)
                {
                    if (!reached[j]) continue;
                    Fix gap = Fix.Max(Fix.Zero, Fix.Max(p[i].MinX, p[j].MinX) - Fix.Min(p[i].MaxX, p[j].MaxX));
                    if (p[i].MaxY - p[j].MaxY <= rise && gap <= reach) reached[i] = grew = true;
                }
            }
        }
        var missing = Enumerable.Range(0, p.Count).Where(i => !reached[i]).ToList();
        Assert.True(missing.Count == 0, $"{id}: platforms {string.Join(", ", missing)} can't be reached");
    }
}
