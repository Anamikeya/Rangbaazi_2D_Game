using Rangbaazi.Sim.Diagnostics;
using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim.Tests;

/// <summary>
/// The rules every weapon shares from combat spec v0.4 (sections 1.6, 3 and 4): the prop table and
/// its box slots, shots that fizzle, the placed-prop limit, weighted crates, teams, the statuses'
/// state, and a prop that comes to rest.
/// </summary>
public class WeaponRulesTests
{
    private static readonly MatchRules Rules = new() { Stage = TestStages.Wide, Weapons = true, Guts = false };

    private static Fix Cm(int cm) => Fix.FromRatio(cm, 100);

    /// <summary>Fighters standing at these x positions (metres x 100), P1 facing right, the rest left; no boxes due.</summary>
    private static GameState Standing(MatchRules rules, params int[] xCm)
    {
        var s = Simulation.CreateInitialState(rules, xCm.Length, 1);
        for (int i = 0; i < xCm.Length; i++)
        {
            ref Fighter f = ref s.Fighters[i];
            f.Position = new FixVec2(Cm(xCm[i]), Fix.Zero);
            f.Velocity = FixVec2.Zero;
            f.Grounded = true;
            f.Facing = i == 0 ? 1 : -1;
        }
        s.PropTimer = 1_000_000;
        return s;
    }

    private static GameState Standing(params int[] xCm) => Standing(Rules, xCm);

    private static void Arm(GameState s, int fighter, WeaponId w, int supply = -1)
    {
        s.Fighters[fighter].Weapon = w;
        s.Fighters[fighter].WeaponSupply = supply >= 0 ? supply : Weapons.Get(w).Supply;
    }

    private static void Step(GameState s, MatchRules rules, params Buttons[] inputs)
    {
        var all = new Buttons[s.Fighters.Length];
        inputs.CopyTo(all, 0);
        Simulation.Advance(s, rules, all);
    }

    private static void Step(GameState s, params Buttons[] inputs) => Step(s, Rules, inputs);

    /// <summary>Fighter 0 presses attack and the move plays to its end.</summary>
    private static void Use(GameState s, MatchRules rules, Buttons button = Buttons.Light)
    {
        Step(s, rules, button);
        for (int i = 0; i < 80 && s.Fighters[0].Action == FighterAction.Attack; i++) Step(s, rules);
    }

    private static int Count(GameState s, ShotKind shot, int owner = -1) =>
        s.Props.Count(p => p.Kind == PropKind.Shot && p.Shot == shot && (owner < 0 || p.Owner == owner));

    /// <summary>A mine far from everyone that isn't armed yet and won't be for the length of a test.</summary>
    private static Prop IdleMine(int owner, int xCm, int age = 0) => new()
    {
        Kind = PropKind.Shot, Shot = ShotKind.Mine, Weapon = WeaponId.Mines, Owner = owner, OwnerClear = true,
        Position = new FixVec2(Cm(xCm), Fix.Zero), Grounded = true, Timer = 100_000, Age = age,
    };

    /// <summary>Fills every shot slot (the ones after the box slots) with idle mines of this fighter.</summary>
    private static void FillShotSlots(GameState s, int owner)
    {
        for (int i = Weapons.BoxSlots(s.Fighters.Length); i < s.Props.Length; i++) s.Props[i] = IdleMine(owner, 30_000 + i * 300, age: i);
    }

    // ---- The prop table ----

    [Fact]
    public void ThePropTableGrowsWithTheFighterCountUpToSixtyFour()
    {
        Assert.Equal(20, Weapons.PropSlots(1));
        Assert.Equal(24, Weapons.PropSlots(2));
        Assert.Equal(32, Weapons.PropSlots(4));
        Assert.Equal(64, Weapons.PropSlots(12));
        Assert.Equal(64, Weapons.PropSlots(15));
        Assert.Equal(64, Weapons.PropSlots(Simulation.MaxFighters));
        foreach (int n in new[] { 1, 2, 4, 9, 15 })
        {
            GameState s = Simulation.CreateInitialState(Rules, n, 1);
            Assert.Equal(Weapons.PropSlots(n), s.Props.Length);
            Assert.Equal(Weapons.PropSlots(n), s.Clone().Props.Length);
            Assert.Equal(Weapons.Cap(n) + 2, Weapons.BoxSlots(n));
            Assert.True(Weapons.BoxSlots(n) + 2 * n <= s.Props.Length, "every fighter has room for a couple of shots");
        }
    }

    [Fact]
    public void ShotsUseTheSlotsAfterTheBoxSlotsAndABoxTakesTheFirstEmptyOne()
    {
        GameState s = Standing(0, 2000);
        int boxSlots = Weapons.BoxSlots(2);
        Arm(s, 0, WeaponId.Uzi);
        Step(s, Buttons.Light);
        for (int i = 0; i < 4; i++) Step(s);
        int bullet = Array.FindIndex(s.Props, p => p.Kind == PropKind.Shot);
        Assert.Equal(boxSlots, bullet); // the first slot a shot may use

        s.PropTimer = 1;
        Step(s);
        Assert.Equal(PropKind.Box, s.Props[0].Kind); // a box takes the first empty slot anywhere: a box slot
    }

    [Fact]
    public void WithTheShotSlotsFullOfSomeoneElsesPlacedPropsAShotFizzlesAndSpendsNothing()
    {
        GameState s = Standing(0, 2000);
        FillShotSlots(s, owner: 1);
        int mines = Count(s, ShotKind.Mine);

        Arm(s, 0, WeaponId.Bow);
        Use(s, Rules);
        Assert.Equal(6, s.Fighters[0].WeaponSupply);            // nothing spent
        Assert.Equal(WeaponId.Bow, s.Fighters[0].Weapon);
        Assert.Equal(0, Count(s, ShotKind.Arrow));
        Assert.Equal(mines, Count(s, ShotKind.Mine, owner: 1)); // and nobody else's prop was touched
        Assert.Equal(FighterAction.Idle, s.Fighters[0].Action); // the move's frames played
    }

    [Fact]
    public void AWeaponThatWouldLeaveTheHandStaysInItWhenItsShotFizzles()
    {
        foreach (WeaponId id in new[] { WeaponId.BowlingBall, WeaponId.Boomerang })
        {
            GameState s = Standing(0, 2000);
            FillShotSlots(s, owner: 1);
            Arm(s, 0, id);
            int supply = s.Fighters[0].WeaponSupply;
            Use(s, Rules);
            Assert.Equal(id, s.Fighters[0].Weapon);
            Assert.Equal(supply, s.Fighters[0].WeaponSupply);
        }

        // A throw, too: the Drop weapon button with nowhere to put it.
        GameState t = Standing(0, 2000);
        FillShotSlots(t, owner: 1);
        Arm(t, 0, WeaponId.Belan, 5);
        Use(t, Rules, Buttons.DropWeapon);
        Assert.Equal(WeaponId.Belan, t.Fighters[0].Weapon);
        Assert.Equal(5, t.Fighters[0].WeaponSupply);
        Assert.Equal(0, Count(t, ShotKind.Thrown));
    }

    [Fact]
    public void AShotWithNoEmptySlotReplacesTheOldestBullet()
    {
        GameState s = Standing(0, 2000);
        FillShotSlots(s, owner: 1);
        int from = Weapons.BoxSlots(2);
        // Two of fighter 1's bullets, standing still far away: the older one is the one that goes.
        s.Props[from + 3] = new Prop { Kind = PropKind.Shot, Shot = ShotKind.Bullet, Weapon = WeaponId.Uzi, Owner = 1, OwnerClear = true, Position = new FixVec2(Cm(90_000), Fix.One), Age = 2 };
        s.Props[from + 7] = new Prop { Kind = PropKind.Shot, Shot = ShotKind.Bullet, Weapon = WeaponId.Uzi, Owner = 1, OwnerClear = true, Position = new FixVec2(Cm(90_000), Fix.One), Age = 9 };

        Arm(s, 0, WeaponId.Uzi);
        Step(s, Buttons.Light);
        for (int i = 0; i < Weapons.Get(WeaponId.Uzi).Startup; i++) Step(s);
        Assert.Equal(0, s.Props[from + 7].Owner);  // the oldest bullet's slot is fighter 0's bullet now
        Assert.Equal(1, s.Props[from + 3].Owner);
        Assert.Equal(29, s.Fighters[0].WeaponSupply);
    }

    [Fact]
    public void AShotWithNoSlotAndNoBulletReplacesItsFirersOwnOldestPlacedPropAndNeverAnothers()
    {
        GameState s = Standing(0, 2000);
        FillShotSlots(s, owner: 1);
        int from = Weapons.BoxSlots(2);
        s.Props[from + 2] = IdleMine(owner: 0, xCm: 50_000, age: 5);
        s.Props[from + 5] = IdleMine(owner: 0, xCm: 51_000, age: 40); // fighter 0's oldest
        int others = Count(s, ShotKind.Mine, owner: 1);

        Arm(s, 0, WeaponId.Bow);
        Step(s, Buttons.Light);
        for (int i = 0; i < Weapons.Get(WeaponId.Bow).Startup; i++) Step(s);
        Assert.Equal(ShotKind.Arrow, s.Props[from + 5].Shot);
        Assert.Equal(ShotKind.Mine, s.Props[from + 2].Shot);
        Assert.Equal(others, Count(s, ShotKind.Mine, owner: 1));
        Assert.Equal(5, s.Fighters[0].WeaponSupply);
    }

    [Fact]
    public void BoxesStillDropAndADeadFightersWeaponStillComesBackWithEveryShotSlotFull()
    {
        GameState s = Standing(0, 2000);
        FillShotSlots(s, owner: 1);
        s.PropTimer = 1;
        Step(s);
        Assert.Equal(1, s.Props.Count(p => p.Kind == PropKind.Box));

        Arm(s, 0, WeaponId.Shotgun, 3);
        s.Fighters[0].Position = new FixVec2(Fix.Zero, Fix.FromInt(-200)); // out through the bottom
        Step(s);
        Prop pending = Assert.Single(s.Props, p => p.Kind == PropKind.PendingBox);
        Assert.Equal((WeaponId.Shotgun, 3), (pending.Weapon, pending.Supply));
        for (int i = 0; i < Weapons.ReturnFrames + 1; i++) Step(s);
        Assert.Contains(s.Props, p => p.Kind == PropKind.Box && p.Weapon == WeaponId.Shotgun && p.Supply == 3);
    }

    // ---- Placed props ----

    /// <summary>Fighter 0 plants or places one at this x (metres x 100).</summary>
    private static void PlaceAt(GameState s, MatchRules rules, int xCm)
    {
        s.Fighters[0].Position = new FixVec2(Cm(xCm), Fix.Zero);
        s.Fighters[0].Velocity = FixVec2.Zero;
        s.Fighters[0].Grounded = true;
        s.Fighters[0].Facing = 1;
        Use(s, rules);
    }

    [Fact]
    public void AFighterKeepsFourPlacedPropsAndAFifthRemovesTheOldestWithoutBursting()
    {
        GameState s = Standing(0, 100_000);
        Arm(s, 0, WeaponId.Mines, 9);
        for (int k = 0; k < 4; k++) PlaceAt(s, Rules, k * 1000);
        Assert.Equal(4, Count(s, ShotKind.Mine, owner: 0));
        Fix first = Cm(0) + Weapons.HandReach; // where the first one lies

        // Fighter 1 stands inside the first mine's blast, not on it: a burst would hurt them.
        s.Fighters[1].Position = new FixVec2(first + Fix.One, Fix.Zero);
        PlaceAt(s, Rules, 4000);
        Assert.Equal(4, Count(s, ShotKind.Mine, owner: 0));
        Assert.DoesNotContain(s.Props, p => p.Kind == PropKind.Shot && p.Position.X == first);
        Assert.Equal(0, s.Fighters[1].Josh);      // it vanished: no burst
        Assert.Equal(0, s.Fighters[1].HitCount);
        Assert.Equal(4, s.Fighters[0].WeaponSupply); // the fifth still cost one
    }

    [Fact]
    public void PadsAndMinesCountTogetherAndAnotherFightersAreLeftAlone()
    {
        GameState s = Standing(0, 100_000);
        int from = Weapons.BoxSlots(2);
        for (int k = 0; k < 4; k++) s.Props[from + 10 + k] = IdleMine(owner: 1, xCm: 90_000 + k * 500);

        Arm(s, 0, WeaponId.Trampoline, 2);
        PlaceAt(s, Rules, 0);
        PlaceAt(s, Rules, 1000);
        Arm(s, 0, WeaponId.Mines, 3);
        PlaceAt(s, Rules, 2000);
        PlaceAt(s, Rules, 3000);
        Assert.Equal((2, 2), (Count(s, ShotKind.Pad, owner: 0), Count(s, ShotKind.Mine, owner: 0)));

        PlaceAt(s, Rules, 4000); // a fifth: the first trampoline, the oldest of the four, goes
        Assert.Equal((1, 3), (Count(s, ShotKind.Pad, owner: 0), Count(s, ShotKind.Mine, owner: 0)));
        Assert.Equal(4, Count(s, ShotKind.Mine, owner: 1));
    }

    [Fact]
    public void InAMatchOfMoreThanEightTheLimitIsTwo()
    {
        Assert.Equal(4, Weapons.PlacedLimit(2));
        Assert.Equal(4, Weapons.PlacedLimit(8));
        Assert.Equal(2, Weapons.PlacedLimit(9));
        Assert.Equal(2, Weapons.PlacedLimit(15));

        GameState s = Standing(0, 10_000, 20_000, 30_000, 40_000, 50_000, 60_000, 70_000, 80_000);
        Arm(s, 0, WeaponId.Mines, 5);
        for (int k = 0; k < 3; k++) PlaceAt(s, Rules, k * 1000);
        Assert.Equal(2, Count(s, ShotKind.Mine, owner: 0));
        Assert.DoesNotContain(s.Props, p => p.Kind == PropKind.Shot && p.Position.X == Weapons.HandReach); // the first is the one that went
    }

    // ---- Crates ----

    [Fact]
    public void ACrateNeverHoldsAHiddenOrUnbuiltRowAndNeverTheLastBoxsWeapon()
    {
        var rules = new MatchRules { Stage = Stages.Graybox, Weapons = true };
        var s = Simulation.CreateInitialState(rules, 2, 11);
        var seen = new List<WeaponId>();
        // Long enough for every weapon to turn up, however many the crates hold: it stops once they all have.
        var inputs = new Buttons[2];
        for (int f = 0; f < 60 * 6000 && (seen.Count <= 40 || seen.Distinct().Count() < Weapons.CrateList.Length); f++)
        {
            WeaponId last = s.LastBoxWeapon;
            Simulation.Advance(s, rules, inputs);
            if (s.LastBoxWeapon != last) seen.Add(s.LastBoxWeapon);
            for (int i = 0; i < s.Props.Length; i++) s.Props[i] = default;
        }
        Assert.True(seen.Count > 40);
        for (int i = 0; i < seen.Count; i++)
        {
            WeaponRow row = Weapons.Get(seen[i]);
            Assert.True(row.Built && row.Crate && row.Id != WeaponId.None, $"a crate held {seen[i]}");
            if (i > 0) Assert.NotEqual(seen[i - 1], seen[i]);
        }
        Assert.Equal(Weapons.CrateList.Length, seen.Distinct().Count()); // every one of them turns up
    }

    [Fact]
    public void UtilityRowsComeAboutHalfAsOftenAndTheLastBoxsWeaponNever()
    {
        // The pick over every weapon the table will put in crates: 88 of weight 2 and the 12 utility rows of weight 1.
        WeaponId[] list = WeaponTable.Rows.Where(r => r.Crate).Select(r => r.Id).ToArray();
        Assert.Equal(100, list.Length);
        Assert.Equal((2, 2, 1, 1), (Weapons.Get(WeaponId.Belan).Weight, Weapons.Get(WeaponId.Bow).Weight, Weapons.Get(WeaponId.Lota).Weight, Weapons.Get(WeaponId.Pankha).Weight));
        var random = new SimRandom(99);
        var counts = new Dictionary<WeaponId, int>();
        foreach (WeaponId w in list) counts[w] = 0;
        WeaponId last = WeaponId.None;
        const int draws = 200_000;
        for (int i = 0; i < draws; i++)
        {
            ulong before = random.State;
            WeaponId picked = Simulation.PickCrate(ref random, list, last);
            Assert.NotEqual(last, picked);
            Assert.NotEqual(before, random.State);
            var once = new SimRandom(before);
            once.NextULong();
            Assert.Equal(once.State, random.State); // one draw a box
            counts[picked]++;
            last = picked;
        }
        double normal = list.Where(w => Weapons.Get(w).Weight == 2).Average(w => counts[w]);
        double utility = list.Where(w => Weapons.Get(w).Weight == 1).Average(w => counts[w]);
        Assert.InRange(utility / normal, 0.47, 0.53);
        Assert.All(list, w => Assert.True(counts[w] > 0, $"{w} never came"));
        Assert.InRange(list.Where(w => Weapons.Get(w).Weight == 1).Sum(w => counts[w]) * 16.0 / draws, 0.9, 1.1); // about one crate in sixteen

        // The first box (no last weapon), and a list of one.
        var first = new SimRandom(3);
        Assert.Contains(Simulation.PickCrate(ref first, list, WeaponId.None), list);
        var lone = new SimRandom(3);
        Assert.Equal(WeaponId.Bow, Simulation.PickCrate(ref lone, new[] { WeaponId.Bow }, WeaponId.Bow));
    }

    // ---- Teams ----

    private static MatchRules Teams(params int[] teams) => new() { Stage = TestStages.Wide, Weapons = true, Guts = false, Teams = teams };

    [Fact]
    public void AlliesAreTheFighterItselfAndItsTeamAndNobodyWhenNoTeamsAreSet()
    {
        Assert.True(Simulation.Allies(Rules, 1, 1));
        Assert.False(Simulation.Allies(Rules, 0, 1));
        Assert.Equal(0, Rules.TeamOf(0));
        Assert.Equal(0, Rules.TeamOf(-1));

        MatchRules rules = Teams(1, 1, 2, 0, 0);
        Assert.True(Simulation.Allies(rules, 0, 1));
        Assert.True(Simulation.Allies(rules, 1, 0));
        Assert.False(Simulation.Allies(rules, 0, 2));
        Assert.False(Simulation.Allies(rules, 3, 4)); // team 0 is no team
        Assert.True(Simulation.Allies(rules, 3, 3));
        Assert.Equal(0, rules.TeamOf(9));             // past the list: no team
        Assert.Equal(new[] { 1, 1, 2 }, MatchRules.Room(Stages.Rooftops, 10, null, new[] { 1, 1, 2 }).Teams);
    }

    [Fact]
    public void AJabGoesThroughATeammateAndHitsTheEnemyBehindThem()
    {
        MatchRules rules = Teams(1, 1, 2);
        GameState s = Standing(rules, 0, 90, 120);
        Step(s, rules, Buttons.Light);
        for (int i = 0; i < 8; i++) Step(s, rules);
        Assert.Equal(0, s.Fighters[1].Josh);
        Assert.Equal(0, s.Fighters[1].HitCount);
        Assert.Equal(6, s.Fighters[2].Josh);

        // With no teams the same jab hits both.
        GameState free = Standing(0, 90, 120);
        Step(free, Buttons.Light);
        for (int i = 0; i < 8; i++) Step(free);
        Assert.Equal((6, 6), (free.Fighters[1].Josh, free.Fighters[2].Josh));
    }

    [Fact]
    public void ABulletFliesThroughATeammateAndHitsTheEnemyBehindThem()
    {
        MatchRules rules = Teams(1, 1, 2);
        GameState s = Standing(rules, 0, 200, 400);
        Arm(s, 0, WeaponId.Uzi, 1);
        Step(s, rules, Buttons.Light);
        for (int i = 0; i < 14; i++) Step(s, rules);
        Assert.Equal(0, s.Fighters[1].HitCount);
        Assert.Equal(2, s.Fighters[2].Josh);
    }

    [Fact]
    public void ABurstHurtsItsThrowerAndTheEnemyButNotATeammate()
    {
        MatchRules rules = Teams(1, 1, 2);
        GameState s = Standing(rules, 0, 60, -60);
        s.Props[Weapons.BoxSlots(3)] = new Prop
        {
            Kind = PropKind.Shot, Shot = ShotKind.Bomb, Weapon = WeaponId.CherryBombs, Owner = 0, OwnerClear = true,
            Position = new FixVec2(Fix.Zero, Fix.Zero), Grounded = true, Timer = 1,
        };
        Step(s, rules);
        Assert.Equal(12, s.Fighters[0].Josh); // a fighter's own bomb still hurts that fighter
        Assert.Equal(0, s.Fighters[1].Josh);
        Assert.Equal(12, s.Fighters[2].Josh);
    }

    [Fact]
    public void AMineIsNotSetOffByATeammateAndABombDoesNotBurstOnOne()
    {
        MatchRules rules = Teams(1, 1, 2);
        GameState s = Standing(rules, 0, 1000, 5000);
        s.Props[Weapons.BoxSlots(3)] = new Prop
        {
            Kind = PropKind.Shot, Shot = ShotKind.Mine, Weapon = WeaponId.Mines, Owner = 0, OwnerClear = true,
            Position = new FixVec2(Cm(1000), Fix.Zero), Grounded = true,
        };
        for (int i = 0; i < 5; i++) Step(s, rules);
        Assert.Equal(1, Count(s, ShotKind.Mine)); // the teammate is standing on it
        Assert.Equal(0, s.Fighters[1].Josh);

        s.Fighters[2].Position = new FixVec2(Cm(1000), Fix.Zero); // the enemy steps on it: it bursts, on the enemy only
        Step(s, rules);
        Assert.Equal(0, Count(s, ShotKind.Mine));
        Assert.Equal(16 * Weapons.SweetPercent / 100, s.Fighters[2].Josh);
        Assert.Equal(0, s.Fighters[1].Josh);

        // A cherry bomb in the air touching a teammate flies on.
        GameState t = Standing(rules, 0, 1000, 5000);
        t.Props[Weapons.BoxSlots(3)] = new Prop
        {
            Kind = PropKind.Shot, Shot = ShotKind.Bomb, Weapon = WeaponId.CherryBombs, Owner = 0, OwnerClear = true,
            Position = new FixVec2(Cm(1000), Fix.One), Timer = 40,
        };
        Step(t, rules);
        Assert.Equal(1, Count(t, ShotKind.Bomb));
        Assert.Equal(0, t.Fighters[1].Josh);
    }

    [Fact]
    public void ATrampolineStillBouncesATeammateAndAMissileIgnoresOne()
    {
        MatchRules rules = Teams(1, 1, 2);
        GameState s = Standing(rules, 0, 1000, 5000);
        s.Props[Weapons.BoxSlots(3)] = new Prop
        {
            Kind = PropKind.Shot, Shot = ShotKind.Pad, Weapon = WeaponId.Trampoline, Owner = 0, OwnerClear = true,
            Position = new FixVec2(Cm(1000), Fix.Zero), Grounded = true, Timer = 600,
        };
        Step(s, rules);
        Assert.Equal(Weapons.Get(WeaponId.Trampoline).PadLaunch, s.Fighters[1].Velocity.Y); // pads work for everybody

        // A missile between a near teammate (left) and a far enemy (right) turns to the enemy.
        GameState m = Standing(rules, 0, 300, 3000);
        m.Props[Weapons.BoxSlots(3)] = new Prop
        {
            Kind = PropKind.Shot, Shot = ShotKind.Missile, Weapon = WeaponId.HomingMissile, Owner = 0, OwnerClear = true,
            Position = new FixVec2(Cm(600), Fix.FromRatio(3, 4)),
        };
        for (int i = 0; i < 10; i++) Step(m, rules);
        Assert.True(m.Props[Weapons.BoxSlots(3)].Velocity.X > Fix.Zero, "it steered at the teammate");
    }

    [Fact]
    public void ACpuWalksPastATeammateToAnEnemy()
    {
        MatchRules rules = Teams(1, 1, 2);
        GameState s = Standing(rules, 0, -100, 800);
        Assert.Equal(Buttons.Right, SparringBot.Think(s, rules, 0, CpuLevel.Hard) & (Buttons.Left | Buttons.Right));
        Assert.Equal(Buttons.None, SparringBot.Think(s, rules, 0, CpuLevel.Hard) & Buttons.Light); // it doesn't jab the friend beside it

        MatchRules alone = Teams(1, 1);
        GameState two = Standing(alone, 0, 100);
        Assert.Equal(Buttons.None, SparringBot.Think(two, alone, 0, CpuLevel.Hard)); // nobody to fight
    }

    // ---- Statuses, and a prop at rest ----

    [Fact]
    public void ARespawnAndAKoClearEveryStatus()
    {
        var rules = new MatchRules { Stage = Stages.Graybox, Weapons = true };
        var s = Simulation.CreateInitialState(rules, 2, 1);
        s.PropTimer = 1_000_000;
        void Afflict(ref Fighter f)
        {
            f.SlowFrames = 30;
            f.RootFrames = 20;
            f.DotFrames = 90;
            f.DotBy = 1;
            f.DotWeapon = 73;
            f.MarkFrames = 300;
            f.StunImmune = 150;
            f.StunWeapon = 78;
        }
        static int[] Statuses(in Fighter f) => new[] { f.SlowFrames, f.RootFrames, f.DotFrames, f.DotBy, f.DotWeapon, f.MarkFrames, f.StunImmune, f.StunWeapon };

        Afflict(ref s.Fighters[1]);
        Simulation.Advance(s, rules, new Buttons[2]);
        Fighter f1 = s.Fighters[1]; // they count down (the hold ends: a fighter just spawned is in the air)
        Assert.Equal((29, 89, 299, 149), (f1.SlowFrames, f1.DotFrames, f1.MarkFrames, f1.StunImmune));
        ulong with = s.Checksum();

        s.Fighters[1].Position = new FixVec2(Fix.Zero, Fix.FromInt(-30)); // knocked out
        Simulation.Advance(s, rules, new Buttons[2]);
        Assert.Equal(FighterAction.Respawning, s.Fighters[1].Action);
        Assert.All(Statuses(in s.Fighters[1]), v => Assert.Equal(0, v));
        Assert.NotEqual(with, s.Checksum());

        Afflict(ref s.Fighters[1]); // whatever gets set while it waits is gone when it spawns
        for (int i = 0; i < rules.RespawnFrames; i++) Simulation.Advance(s, rules, new Buttons[2]);
        Assert.True(s.Fighters[1].IsActive);
        Assert.All(Statuses(in s.Fighters[1]), v => Assert.Equal(0, v));
    }

    [Fact]
    public void AShotThatComesToRestForgetsEveryShotField()
    {
        GameState s = Standing(0, 5000);
        int slot = Weapons.BoxSlots(2);
        WeaponRow ball = Weapons.Get(WeaponId.BowlingBall);
        s.Props[slot] = new Prop
        {
            Kind = PropKind.Shot, Shot = ShotKind.Roll, Weapon = WeaponId.BowlingBall, Owner = 0, OwnerClear = true,
            Position = new FixVec2(Cm(2000), Fix.Zero), Velocity = new FixVec2(ball.Vx, Fix.Zero), Grounded = true,
            Age = ball.Life - 1, HitMask = 3, Hits = 2, Timer = 7, IgnoreFighter = 2, IgnoreTimer = 30, Supply = 0,
        };
        Step(s);
        Prop rested = s.Props[slot];
        Assert.Equal(new Prop
        {
            Kind = PropKind.Item, Weapon = WeaponId.BowlingBall, Shot = ShotKind.None, Supply = 1, Owner = -1, OwnerClear = false,
            Position = new FixVec2(Cm(2000) + ball.Vx, Fix.Zero), Velocity = FixVec2.Zero, Grounded = true,
            Timer = 0, Age = 0, HitMask = 0, Hits = 0, IgnoreFighter = 0, IgnoreTimer = 0,
        }, rested);
    }

    // ---- A full room, and rollback ----

    private static void ArmAll(GameState s, int frame, int every)
    {
        if (frame % every != 0) return;
        for (int p = 0; p < s.Fighters.Length; p++)
        {
            if (!s.Fighters[p].IsActive) continue;
            WeaponId w = Weapons.CrateList[(frame / every + p) % Weapons.CrateList.Length];
            s.Fighters[p].Weapon = w;
            s.Fighters[p].WeaponSupply = Weapons.Get(w).Supply;
        }
    }

    /// <summary>
    /// Fifteen Hard CPUs under room rules for a minute, handed every weapon a crate can hold in
    /// turn: boxes keep dropping throughout, every fighter's placed props stay within the limit of
    /// a big room, the box slots never hold a shot, and the same run twice gives the same checksums.
    /// </summary>
    [Fact]
    public void AFullRoomKeepsItsCratesAndItsPlacedLimit()
    {
        MatchRules rules = MatchRules.Room(Stages.Rooftops, 1);
        ulong[] Run(bool check)
        {
            GameState s = Simulation.CreateInitialState(rules, 15, 2026);
            Assert.Equal(64, s.Props.Length);
            var sums = new ulong[rules.TimeLimitFrames];
            var inputs = new Buttons[15];
            var drops = new int[3];
            int boxSlots = Weapons.BoxSlots(15);
            for (int f = 0; f < rules.TimeLimitFrames; f++)
            {
                ArmAll(s, f, 45);
                for (int p = 0; p < 15; p++) inputs[p] = SparringBot.Think(s, rules, p, CpuLevel.Hard);
                Simulation.Advance(s, rules, inputs);
                sums[f] = s.Checksum();
                if (!check) continue;
                // A box that appeared this frame: a fresh crate, or a fallen fighter's weapon coming back.
                drops[f * 3 / rules.TimeLimitFrames] += s.Props.Count(q => q.Kind == PropKind.Box && q.Timer == 0);
                for (int p = 0; p < 15; p++)
                {
                    Assert.True(s.Props.Count(q => q.Kind == PropKind.Shot && q.Owner == p && WeaponTable.Placed(q.Shot)) <= 2, $"fighter {p} has more than 2 placed props at frame {f}");
                    Assert.True(s.Props.Count(q => q.Kind == PropKind.Shot && q.Owner == p && q.Shot == ShotKind.Speaker) <= 1, $"fighter {p} has two speakers at frame {f}");
                    Fighter fp = s.Fighters[p];
                    Assert.False(fp.IsActive && fp.StunWeapon != 0 && !fp.Grounded, $"fighter {p} is held in the air at frame {f}"); // nobody is held while falling
                }
                // Only a box or loose weapon flung by a swing (a thrown shot carrying its supply)
                // is a shot in a box slot: it keeps the slot it lay in, and lies there again.
                for (int i = 0; i < boxSlots; i++)
                    Assert.True(s.Props[i].Kind != PropKind.Shot || (s.Props[i].Shot == ShotKind.Thrown && s.Props[i].Supply > 0),
                        $"a shot is in box slot {i} at frame {f}: {s.Props[i].Shot} of {s.Props[i].Weapon}, supply {s.Props[i].Supply}");
            }
            if (check) Assert.All(drops, d => Assert.True(d >= 3, $"boxes stopped dropping: {string.Join(", ", drops)} in the three thirds"));
            return sums;
        }
        Assert.Equal(Run(check: true), Run(check: false));
    }

    [Fact]
    public void RollbackThroughAMatchWithEveryWeaponMatchesTheOriginalTimeline()
    {
        var rules = new MatchRules { Stage = Stages.Graybox, Weapons = true, Flurry = true };
        CpuLevel[] levels = { CpuLevel.Hard, CpuLevel.Normal, CpuLevel.Hard, CpuLevel.Easy };
        GameState s = Simulation.CreateInitialState(rules, 4, 77);
        var inputs = new Buttons[4];
        void Advance(GameState state)
        {
            ArmAll(state, state.Frame, 90);
            for (int p = 0; p < 4; p++) inputs[p] = SparringBot.Think(state, rules, p, levels[p]);
            Simulation.Advance(state, rules, inputs);
        }

        int frames = 90 * Weapons.CrateList.Length;
        for (int from = 60; from + 120 <= frames; from += 150)
        {
            while (s.Frame < from) Advance(s);
            GameState saved = s.Clone();
            var sums = new ulong[120];
            for (int i = 0; i < 120; i++)
            {
                Advance(s);
                sums[i] = s.Checksum();
            }
            GameState replay = s.Clone();
            replay.CopyFrom(saved); // load the save into a state that has moved on
            for (int i = 0; i < 120; i++)
            {
                Advance(replay);
                Assert.True(sums[i] == replay.Checksum(), $"the replay from frame {from} differs {i + 1} frames later");
            }
        }
    }
}
