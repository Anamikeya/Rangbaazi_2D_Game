using Rangbaazi.Sim.Diagnostics;
using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim.Tests;

/// <summary>
/// Arena hazards (launch pads, strikers, pushers on a shared clock), and the record of hits the
/// callouts and the winner's nickname read: each hit's weapon and sweet spot, and each fighter's
/// damage, KOs and sweet spots per weapon.
/// </summary>
public class HazardAndRecordTests
{
    private static readonly FixRect Zone = new(Fix.FromInt(-2), Fix.Zero, Fix.FromInt(2), Fix.FromInt(2));

    private static MatchRules RulesWith(HazardKind kind, int direction = 1) => new()
    {
        Hazards = true,
        Stage = new StageDefinition
        {
            Id = "test-hazard",
            Platforms = TestStages.Wide.Platforms,
            SpawnPoints = TestStages.Wide.SpawnPoints,
            BlastZone = TestStages.Wide.BlastZone,
            Hazards = new[] { new Hazard(kind, Zone, direction) },
        },
    };

    /// <summary>One fighter standing at x (metres x 100), the match clock just before the hazard turns on.</summary>
    private static GameState Standing(MatchRules rules, int xCm, int frame = Hazards.ActiveAt - 1)
    {
        var s = Simulation.CreateInitialState(rules, 1, 1);
        ref Fighter f = ref s.Fighters[0];
        f.Position = new FixVec2(Fix.FromRatio(xCm, 100), Fix.Zero);
        f.Velocity = FixVec2.Zero;
        f.Grounded = true;
        s.Frame = frame;
        return s;
    }

    private static void Step(GameState s, MatchRules rules, Buttons b = Buttons.None) => Simulation.Advance(s, rules, new[] { b });

    [Fact]
    public void TheClockCyclesIdleWarningActiveCooldownAndStaggersEachHazard()
    {
        Assert.False(Hazards.IsActive(Hazards.WarnAt, 0));
        Assert.True(Hazards.IsActive(Hazards.ActiveAt, 0));
        Assert.True(Hazards.IsActive(Hazards.CooldownAt - 1, 0));
        Assert.False(Hazards.IsActive(Hazards.CooldownAt, 0));
        Assert.True(Hazards.IsActive(Hazards.ActiveAt + Hazards.Period * 3, 0));
        Assert.True(Hazards.IsActive(Hazards.ActiveAt - Hazards.Stagger, 1)); // the next one runs ahead
        Assert.Equal(Hazards.ActiveAt - Hazards.WarnAt, 45); // 0.75 s of warning
    }

    [Fact]
    public void ALaunchPadFlingsUpAFighterStandingOnItOnlyWhileActive()
    {
        MatchRules rules = RulesWith(HazardKind.Launch);
        GameState idle = Standing(rules, 0, frame: 10);
        Step(idle, rules);
        Assert.True(idle.Fighters[0].Grounded);

        GameState s = Standing(rules, 0);
        Step(s, rules); // the frame before it turns on
        Assert.True(s.Fighters[0].Grounded);
        Step(s, rules);
        Assert.False(s.Fighters[0].Grounded);
        Assert.Equal(Hazards.LaunchSpeed, s.Fighters[0].Velocity.Y);
        Assert.Equal(0, s.Fighters[0].Josh); // no damage

        GameState outside = Standing(rules, 500);
        Step(outside, rules);
        Step(outside, rules);
        Assert.True(outside.Fighters[0].Grounded);
    }

    [Fact]
    public void APusherCarriesFightersItsWay()
    {
        MatchRules rules = RulesWith(HazardKind.Push, direction: -1);
        GameState s = Standing(rules, 100);
        Step(s, rules);
        Fix before = s.Fighters[0].Position.X;
        Step(s, rules);
        Assert.Equal(before - Hazards.PushSpeed, s.Fighters[0].Position.X);
        Assert.Equal(0, s.Fighters[0].Josh);
    }

    [Fact]
    public void AStrikerHitsEveryTwentyFramesAndCreditsNobody()
    {
        MatchRules rules = RulesWith(HazardKind.Hit);
        GameState s = Standing(rules, 100);
        Step(s, rules);
        Assert.Equal(0, s.Fighters[0].Josh);
        Step(s, rules);
        Assert.Equal(Hazards.Strike.Damage, s.Fighters[0].Josh);
        Assert.Equal(FighterAction.Hitstun, s.Fighters[0].Action);
        Assert.True(s.Fighters[0].Velocity.X > Fix.Zero, "launched away from the zone's middle");
        Assert.Equal(1, s.Fighters[0].HitCount);
        Assert.Equal(WeaponId.None, s.Fighters[0].LastHitWeapon);
        Assert.All(s.WeaponStats, v => Assert.Equal(0, v)); // a hazard's damage is nobody's
    }

    [Fact]
    public void ADodgingFighterIsUntouched()
    {
        MatchRules rules = RulesWith(HazardKind.Hit);
        GameState s = Standing(rules, 100, frame: Hazards.ActiveAt - 3);
        Step(s, rules, Buttons.Dodge);
        for (int i = 0; i < 6; i++) Step(s, rules);
        Assert.Equal(0, s.Fighters[0].Josh);
    }

    [Fact]
    public void WithHazardsOffTheyAreScenery()
    {
        MatchRules on = RulesWith(HazardKind.Hit);
        var off = new MatchRules { Stage = on.Stage };
        GameState s = Standing(off, 100);
        for (int i = 0; i < 80; i++) Step(s, off);
        Assert.Equal(0, s.Fighters[0].Josh);
    }

    [Fact]
    public void EveryArenaHasThreeHazardsOnItsPlatforms()
    {
        foreach (StageDefinition stage in ArenaStages.All)
        {
            Assert.Equal(3, stage.Hazards.Count);
            foreach (Hazard h in stage.Hazards)
            {
                Assert.True(h.Zone.MaxX - h.Zone.MinX >= Fix.One, $"{stage.Id}: a hazard zone under 1 m wide");
                Assert.True(h.Direction is 1 or -1);
                // It sits on a platform top: some platform's top is the zone's bottom and spans its middle.
                Fix mid = (h.Zone.MinX + h.Zone.MaxX) / 2;
                Assert.Contains(stage.Platforms, p => p.MaxY == h.Zone.MinY && p.MinX <= mid && p.MaxX >= mid);
            }
        }
    }

    // ---- The record of hits ---------------------------------------------------------------

    private static readonly MatchRules Fight = new() { Stage = TestStages.Wide, Weapons = true };

    private static GameState Two(int gapCm)
    {
        var s = Simulation.CreateInitialState(Fight, 2, 1);
        for (int i = 0; i < 2; i++)
        {
            ref Fighter f = ref s.Fighters[i];
            f.Position = new FixVec2(Fix.FromRatio(i * gapCm, 100), Fix.Zero);
            f.Velocity = FixVec2.Zero;
            f.Grounded = true;
            f.Facing = i == 0 ? 1 : -1;
        }
        s.PropTimer = 1_000_000;
        return s;
    }

    private static int Stat(GameState s, int fighter, WeaponId weapon, int stat) =>
        s.WeaponStats[(fighter * Weapons.Count + (int)weapon) * 3 + stat];

    [Fact]
    public void EveryHitRecordsItsWeaponAndWhetherItWasASweetSpot()
    {
        GameState s = Two(190); // the bat's tip
        s.Fighters[0].Weapon = WeaponId.BaseballBat;
        s.Fighters[0].WeaponSupply = 8;
        Simulation.Advance(s, Fight, new[] { Buttons.Light, Buttons.None });
        for (int i = 0; i < Weapons.BatSwing.Startup; i++) Simulation.Advance(s, Fight, new Buttons[2]);
        Fighter hit = s.Fighters[1];
        Assert.Equal(1, hit.HitCount);
        Assert.Equal(WeaponId.BaseballBat, hit.LastHitWeapon);
        Assert.True(hit.LastHitSweet);
        Assert.Equal(hit.Josh, Stat(s, 0, WeaponId.BaseballBat, 0));
        Assert.Equal(1, Stat(s, 0, WeaponId.BaseballBat, 2));
        Assert.Equal(0, Stat(s, 0, WeaponId.None, 0));
    }

    [Fact]
    public void FistsCountAsWeaponZeroAndAPlainHitIsNotSweet()
    {
        GameState s = Two(100);
        Simulation.Advance(s, Fight, new[] { Buttons.Light, Buttons.None });
        for (int i = 0; i < 6; i++) Simulation.Advance(s, Fight, new Buttons[2]);
        Assert.Equal(1, s.Fighters[1].HitCount);
        Assert.False(s.Fighters[1].LastHitSweet);
        Assert.Equal(6, Stat(s, 0, WeaponId.None, 0));
    }

    [Fact]
    public void AKoIsCreditedToTheWeaponOfTheLastHit()
    {
        var rules = new MatchRules { Stage = Stages.Graybox, Weapons = true };
        var s = Simulation.CreateInitialState(rules, 2, 1);
        s.PropTimer = 1_000_000;
        s.Fighters[1].LastHitBy = 1;
        s.Fighters[1].LastHitTimer = 100;
        s.Fighters[1].LastHitWeapon = WeaponId.Shotgun;
        s.Fighters[1].Position = new FixVec2(Fix.Zero, Fix.FromInt(-30));
        Simulation.Advance(s, rules, new Buttons[2]);
        Assert.Equal(1, s.Fighters[0].Kos);
        Assert.Equal(1, Stat(s, 0, WeaponId.Shotgun, 1));
    }

    [Fact]
    public void TheMainWeaponDidMostOfTheDamageAndAMasterAlsoHasSweetSpotsAndAKo()
    {
        var s = Simulation.CreateInitialState(Fight, 2, 1);
        Assert.Equal(-1, Simulation.MainWeapon(s, 0, out bool master)); // no damage yet: an all-rounder
        Assert.False(master);

        void Set(WeaponId w, int damage, int kos, int sweets)
        {
            int at = (int)w * 3;
            s.WeaponStats[at] = damage;
            s.WeaponStats[at + 1] = kos;
            s.WeaponStats[at + 2] = sweets;
        }
        Set(WeaponId.None, 30, 0, 0);
        Set(WeaponId.Bow, 50, 1, 3);
        Set(WeaponId.Uzi, 20, 0, 0);
        Assert.Equal((int)WeaponId.Bow, Simulation.MainWeapon(s, 0, out master));
        Assert.True(master);

        Set(WeaponId.Bow, 50, 0, 3); // no KO with it: not a master
        Assert.Equal((int)WeaponId.Bow, Simulation.MainWeapon(s, 0, out master));
        Assert.False(master);

        Set(WeaponId.None, 45, 0, 0);
        Set(WeaponId.Bow, 30, 1, 3);
        Set(WeaponId.Uzi, 40, 0, 0); // nothing reaches 40%: an all-rounder
        Assert.Equal(-1, Simulation.MainWeapon(s, 0, out master));
        Assert.Equal(-1, Simulation.MainWeapon(s, 1, out _)); // the other fighter's record is separate
    }

    [Fact]
    public void RollbackThroughARoomWithHazardsMatchesTheOriginalTimeline()
    {
        MatchRules rules = MatchRules.Room(Stages.Rooftops, 1);
        var reference = Simulation.CreateInitialState(rules, 4, 5);
        var live = Simulation.CreateInitialState(rules, 4, 5);
        var levels = new[] { CpuLevel.Hard, CpuLevel.Normal, CpuLevel.Hard, CpuLevel.Easy };
        int hazardHits = 0;
        for (int f = 0; f < 60 * 45; f++)
        {
            Buttons[] inputs = Enumerable.Range(0, 4).Select(p => SparringBot.Think(reference, rules, p, levels[p])).ToArray();
            int before = reference.Fighters.Sum(x => x.HitCount);
            int recorded = reference.WeaponStats.Sum();
            Simulation.Advance(reference, rules, inputs);
            // A hit that added to nobody's record came from a hazard (or was self-inflicted).
            if (reference.Fighters.Sum(x => x.HitCount) > before && reference.WeaponStats.Sum() == recorded) hazardHits++;
            if (f % 7 == 0)
            {
                GameState saved = live.Clone();
                for (int k = 0; k < 8; k++) Simulation.Advance(live, rules, new[] { Buttons.Right, Buttons.Left, Buttons.Jump, Buttons.Light });
                live.CopyFrom(saved);
            }
            Simulation.Advance(live, rules, inputs);
            Assert.Equal(reference.Checksum(), live.Checksum());
        }
        Assert.True(reference.WeaponStats.Sum() > 0, "nobody's hits were recorded");
    }
}
