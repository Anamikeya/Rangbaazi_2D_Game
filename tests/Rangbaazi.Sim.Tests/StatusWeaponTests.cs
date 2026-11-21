using Rangbaazi.Sim.Diagnostics;
using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim.Tests;

/// <summary>
/// Batch 3 of combat spec v0.4: the statuses and holds (section 2.4), sprays and beams (2.5), lobs
/// and bouncers (2.6). One test per status; the hold, movement and tick rules; the spray and lob
/// archetypes; the push sprays at a ledge; and the trick weapons of the batch.
/// </summary>
public class StatusWeaponTests
{
    private static readonly MatchRules Rules = new() { Stage = TestStages.Wide, Weapons = true, Guts = false };

    /// <summary>A 32 m floor whose sides are the screen's, as an arena's are: the blast lines are 1.2 m beyond them.</summary>
    private static readonly StageDefinition Arena = new()
    {
        Id = "test-arena",
        Platforms = new[] { new FixRect(Fix.FromInt(-16), Fix.FromInt(-2), Fix.FromInt(16), Fix.Zero) },
        SpawnPoints = new[] { new FixVec2(Fix.FromInt(-2), Fix.Zero), new FixVec2(Fix.FromInt(2), Fix.Zero) },
        BlastZone = new FixRect(Fix.FromRatio(-86, 5), Fix.FromInt(-10), Fix.FromRatio(86, 5), Fix.FromInt(40)),
    };

    private static Fix Cm(int cm) => Fix.FromRatio(cm, 100);

    /// <summary>Fighters standing at these x positions (metres x 100), P1 facing right, the rest left; no boxes due.</summary>
    private static GameState Standing(params int[] xCm) => Standing(Rules, xCm);

    private static GameState Standing(MatchRules rules, params int[] xCm)
    {
        var s = Simulation.CreateInitialState(rules, xCm.Length, 1);
        FixRect floor = rules.Stage.Platforms[0];
        for (int i = 0; i < xCm.Length; i++)
        {
            ref Fighter f = ref s.Fighters[i];
            f.Position = new FixVec2(Cm(xCm[i]), floor.MaxY);
            f.Velocity = FixVec2.Zero;
            f.Grounded = true;
            f.Facing = i == 0 ? 1 : -1;
        }
        s.PropTimer = 1_000_000;
        return s;
    }

    private static void Arm(GameState s, int fighter, WeaponId w, int supply = -1)
    {
        s.Fighters[fighter].Weapon = w;
        s.Fighters[fighter].WeaponSupply = supply >= 0 ? supply : Weapons.Get(w).Supply;
    }

    private static void Step(GameState s, params Buttons[] inputs) => Step(Rules, s, inputs);

    private static void Step(MatchRules rules, GameState s, params Buttons[] inputs)
    {
        var all = new Buttons[s.Fighters.Length];
        inputs.CopyTo(all, 0);
        Simulation.Advance(s, rules, all);
    }

    private static void Wait(GameState s, int frames)
    {
        for (int i = 0; i < frames; i++) Step(s);
    }

    /// <summary>Fighter 0 presses attack, and the frames pass up to and including the move's startup frame.</summary>
    private static void Fire(GameState s, WeaponId w)
    {
        Step(s, Buttons.Light);
        Wait(s, Weapons.Get(w).Startup);
    }

    /// <summary>Puts fighter <paramref name="i"/> back where it stood, standing still and free to act.</summary>
    private static void Hold(GameState s, int i, Fix x)
    {
        ref Fighter f = ref s.Fighters[i];
        f.Position = new FixVec2(x, Fix.Zero);
        f.Velocity = FixVec2.Zero;
        f.Grounded = true;
        if (f.Action == FighterAction.Hitstun) f.Action = FighterAction.Idle;
    }

    private static ref Prop PutShot(GameState s, WeaponId w, int xCm, int vxCm, int owner = 0)
    {
        WeaponRow row = Weapons.Get(w);
        s.Props[^1] = new Prop
        {
            Kind = PropKind.Shot, Shot = row.Flight, Weapon = w, Owner = owner, OwnerClear = true,
            Position = new FixVec2(Cm(xCm), Weapons.HandHeight - row.ShotHalf),
            Velocity = new FixVec2(Cm(vxCm), Fix.Zero),
            Timer = row.Fuse,
        };
        return ref s.Props[^1];
    }

    private static int Sweet(int damage) => damage * Weapons.SweetPercent / 100;

    private static List<int> ShotSlots(GameState s, WeaponId w)
    {
        var slots = new List<int>();
        for (int i = 0; i < s.Props.Length; i++)
            if (s.Props[i].Kind == PropKind.Shot && s.Props[i].Weapon == w) slots.Add(i);
        return slots;
    }

    // ---- One test per status ----

    [Fact]
    public void SlowHalvesRunSpeedAndRunsOut()
    {
        GameState s = Standing(0, 5000);
        s.Fighters[0].SlowFrames = 60;
        for (int i = 0; i < 30; i++) Step(s, Buttons.Right);
        Assert.Equal(Rules.Tuning.RunSpeed / 2, s.Fighters[0].Velocity.X);
        Assert.Equal(30, s.Fighters[0].SlowFrames);
        for (int i = 0; i < 40; i++) Step(s, Buttons.Right);
        Assert.Equal(0, s.Fighters[0].SlowFrames);
        Assert.Equal(Rules.Tuning.RunSpeed, s.Fighters[0].Velocity.X);

        // Slow adds up, to at most 2.5 s.
        s.Fighters[0].SlowFrames = 140;
        Arm(s, 1, WeaponId.Balti);
        s.Fighters[1].Position = new FixVec2(s.Fighters[0].Position.X + Fix.One, Fix.Zero);
        s.Fighters[1].Facing = -1;
        Step(s, Buttons.None, Buttons.Light);
        Wait(s, Weapons.Get(WeaponId.Balti).Startup);
        Assert.Equal(Weapons.SlowCapFrames, s.Fighters[0].SlowFrames);
    }

    [Fact]
    public void TheBaltiSoaksItsTarget()
    {
        GameState s = Standing(0, 100);
        Arm(s, 0, WeaponId.Balti);
        Fire(s, WeaponId.Balti);
        Assert.Equal(Weapons.Get(WeaponId.Balti).Damage, s.Fighters[1].Josh);
        Assert.Equal(60, s.Fighters[1].SlowFrames);
        Assert.Equal(FighterAction.Hitstun, s.Fighters[1].Action); // an ordinary hit as well
    }

    [Fact]
    public void ChillFreezesAtTheThresholdAndNotAgainForTwoAndAHalfSeconds()
    {
        GameState s = Standing(0, 200);
        Arm(s, 0, WeaponId.FreezeRay);
        WeaponRow ray = Weapons.Get(WeaponId.FreezeRay);
        var freezes = new List<int>();
        int before = 0;
        for (int f = 0; f < 400; f++)
        {
            before = s.Fighters[1].SlowFrames;
            bool wasFrozen = s.Fighters[1].Action == FighterAction.Stunned;
            s.Fighters[0].WeaponSupply = ray.Supply; // a beam that never runs dry
            Step(s, Buttons.Light);
            ref Fighter t = ref s.Fighters[1];
            if (!wasFrozen && t.Action == FighterAction.Stunned)
            {
                freezes.Add(f);
                Assert.True(before - 1 + ray.EffectN >= Weapons.FreezeAt, $"froze with only {before} chill");
                Assert.Equal((int)WeaponId.FreezeRay, t.StunWeapon);
                Assert.Equal(0, t.SlowFrames);
                Assert.Equal(Weapons.FreezeImmuneFrames, t.StunImmune);
                Assert.True(t.Grounded);
            }
            if (t.Action == FighterAction.Stunned) Assert.Equal(Fix.Zero, t.Velocity.X);
        }
        Assert.True(freezes.Count >= 2, $"froze {freezes.Count} times");
        Assert.True(freezes[1] - freezes[0] >= Weapons.FreezeImmuneFrames, $"froze again {freezes[1] - freezes[0]} frames later");
        Assert.True(freezes[1] - freezes[0] <= Weapons.FreezeImmuneFrames + 8 * ray.Active, "once immunity was over, the next ticks froze it again");
    }

    [Fact]
    public void ANetRootsAStandingFighterAndPressesWorkItLoose()
    {
        GameState s = Standing(0, 300);
        Arm(s, 0, WeaponId.Net);
        for (int i = 0; i < 60 && s.Fighters[1].Josh == 0; i++) Step(s, i == 0 ? Buttons.Light : Buttons.None);
        ref Fighter t = ref s.Fighters[1];
        Assert.Equal(1, t.Josh);
        Assert.Equal(90, t.RootFrames);
        Assert.Equal((int)WeaponId.Net, t.StunWeapon);
        Assert.Equal(90 + Weapons.HoldImmuneFrames, t.StunImmune);
        Assert.Equal(FighterAction.Idle, t.Action); // soft: no knockback, no hitstun
        Assert.Equal(0, t.HitStop);

        Fix x = t.Position.X;
        for (int i = 0; i < 10; i++) Step(s, Buttons.None, Buttons.Left);
        Assert.Equal(x, s.Fighters[1].Position.X);           // it can't walk
        Assert.Equal(-1, s.Fighters[1].Facing);               // it can turn
        Step(s, Buttons.None, Buttons.Jump);
        Step(s, Buttons.None, Buttons.None);
        Assert.True(s.Fighters[1].Grounded);                  // or jump
        Step(s, Buttons.None, Buttons.Dodge);
        Assert.NotEqual(FighterAction.Dodge, s.Fighters[1].Action); // or dodge
        Assert.Equal(90 - 13, s.Fighters[1].RootFrames);

        Step(s, Buttons.None, Buttons.Light);                 // a jab: it can still attack, and the press cuts 12
        Assert.Equal(90 - 14 - 12, s.Fighters[1].RootFrames);
        Assert.Equal(FighterAction.Attack, s.Fighters[1].Action);
        Step(s, Buttons.None, Buttons.None);
        Step(s, Buttons.None, Buttons.Heavy);                 // a press in the middle of the attack counts too
        Assert.Equal(90 - 16 - 24, s.Fighters[1].RootFrames);

        for (int i = 0; i < 60; i++) Step(s);
        Assert.Equal(0, s.Fighters[1].RootFrames);
        Assert.Equal(0, s.Fighters[1].StunWeapon);
        for (int i = 0; i < 20; i++) Step(s, Buttons.None, Buttons.Left);
        Assert.True(s.Fighters[1].Position.X < x, "free again, it walks");
    }

    [Fact]
    public void TheTaserStunsInsteadOfKnockingBack()
    {
        GameState s = Standing(0, 100);
        Arm(s, 0, WeaponId.Taser);
        Fire(s, WeaponId.Taser);
        ref Fighter t = ref s.Fighters[1];
        Assert.Equal(5, t.Josh);
        Assert.Equal(FighterAction.Stunned, t.Action);
        Assert.Equal(24, t.HitstunFrames);
        Assert.Equal(FixVec2.Zero, t.Velocity);
        Assert.True(t.Grounded);
        Assert.Equal((int)WeaponId.Taser, t.StunWeapon);
        Assert.Equal(24 + Weapons.HoldImmuneFrames, t.StunImmune);
        Assert.True(t.HitStop > 0, "it isn't soft: both still freeze");

        for (int i = 0; i < 40 && s.Fighters[1].Action == FighterAction.Stunned; i++) Step(s);
        Assert.Equal(FighterAction.Idle, s.Fighters[1].Action);
        Assert.Equal(0, s.Fighters[1].StunWeapon);

        // Still immune: the next zap is an ordinary hit with the row's knockback.
        Hold(s, 1, Fix.One);
        s.Fighters[0].HitStop = 0;
        for (int i = 0; i < 30 && s.Fighters[0].Action == FighterAction.Attack; i++) Step(s);
        Fire(s, WeaponId.Taser);
        Assert.Equal(FighterAction.Hitstun, s.Fighters[1].Action);
        Assert.True(s.Fighters[1].Velocity.X > Fix.Zero);
    }

    [Fact]
    public void PoisonTicksEveryHalfSecondNeverStacksAndCreditsThePoisoner()
    {
        GameState s = Standing(0, 300);
        Arm(s, 0, WeaponId.VenomDartLauncher);
        for (int i = 0; i < 30 && s.Fighters[1].Josh == 0; i++) Step(s, i == 0 ? Buttons.Light : Buttons.None);
        ref Fighter t = ref s.Fighters[1];
        Assert.Equal(3, t.Josh);
        Assert.Equal(240, t.DotFrames);
        Assert.Equal(1, t.DotBy);
        Assert.Equal((int)WeaponId.VenomDartLauncher, t.DotWeapon);

        // Nobody else's credit is running: each tick keeps the KO with the poisoner.
        t.LastHitBy = 0;
        t.LastHitTimer = 0;
        var ticks = new List<int>();
        int josh = t.Josh;
        for (int f = 0; f < 300; f++)
        {
            Step(s);
            if (s.Fighters[1].Josh > josh) ticks.Add(s.Frame);
            josh = s.Fighters[1].Josh;
            if (f == 100)
            {
                Assert.Equal(1, s.Fighters[1].LastHitBy);
                Assert.Equal(WeaponId.VenomDartLauncher, s.Fighters[1].LastHitWeapon);
            }
        }
        Assert.Equal(8, ticks.Count);                       // 240 frames at 30: 8 ticks, 1 each
        for (int i = 1; i < ticks.Count; i++) Assert.Equal(30, ticks[i] - ticks[i - 1]);
        Assert.Equal(3 + 8, s.Fighters[1].Josh);
        Assert.Equal(0, s.Fighters[1].DotBy);
        Assert.Equal(8, Simulation.WeaponStat(s, 0, WeaponId.VenomDartLauncher, 0) - 3);

        // A second dart while poisoned doesn't stack: the longer timer stands.
        GameState two = Standing(0, 300);
        Arm(two, 0, WeaponId.VenomDartLauncher);
        two.Fighters[1].DotFrames = 200;
        two.Fighters[1].DotBy = 1;
        two.Fighters[1].DotWeapon = (int)WeaponId.VenomDartLauncher;
        for (int i = 0; i < 30 && two.Fighters[1].Josh < 3; i++) Step(two, i == 0 ? Buttons.Light : Buttons.None);
        Assert.Equal(240, two.Fighters[1].DotFrames);

        // And the ring-out goes to the poisoner.
        s = Standing(0, 300);
        s.Fighters[1].DotFrames = 240;
        s.Fighters[1].DotBy = 1;
        s.Fighters[1].DotWeapon = (int)WeaponId.VenomDartLauncher;
        Wait(s, 31);
        Assert.Equal(1, s.Fighters[1].LastHitBy);
        s.Fighters[1].Position = new FixVec2(Fix.Zero, Fix.FromInt(-200));
        Step(s);
        Assert.Equal(1, s.Fighters[0].Kos);
        Assert.Equal(1, Simulation.WeaponStat(s, 0, WeaponId.VenomDartLauncher, 1));
    }

    [Fact]
    public void BurnTicksEveryQuarterSecond()
    {
        GameState s = Standing(0, 5000);
        s.Fighters[1].DotFrames = 90;
        s.Fighters[1].DotBy = 1;
        s.Fighters[1].DotWeapon = (int)WeaponId.Flamethrower;
        var ticks = new List<int>();
        for (int f = 0; f < 120; f++)
        {
            int before = s.Fighters[1].Josh;
            Step(s);
            if (s.Fighters[1].Josh > before) ticks.Add(f);
        }
        Assert.Equal(6, ticks.Count);
        for (int i = 1; i < ticks.Count; i++) Assert.Equal(15, ticks[i] - ticks[i - 1]);
        Assert.All(ticks, f => Assert.Equal(0, (f + 1) % 15)); // on the match clock, offset by the fighter's index
    }

    [Fact]
    public void AMarkedFighterTakesAQuarterMoreFromEveryHit()
    {
        GameState plain = Standing(0, 100);
        Step(plain, Buttons.Light);
        Wait(plain, Rules.Tuning.Moves.Jab.Startup);
        GameState marked = Standing(0, 100);
        marked.Fighters[1].MarkFrames = 300;
        Step(marked, Buttons.Light);
        Wait(marked, Rules.Tuning.Moves.Jab.Startup);
        Assert.Equal(6, plain.Fighters[1].Josh);
        Assert.Equal(6 * Weapons.MarkPercent / 100, marked.Fighters[1].Josh);
        Assert.True(marked.Fighters[1].MarkFrames < 300);
    }

    [Fact]
    public void TheColourPowderMarksEveryoneInThePuff()
    {
        GameState s = Standing(0, 120, 220, 400);
        Arm(s, 0, WeaponId.ColourPowderPouch);
        Fire(s, WeaponId.ColourPowderPouch);
        Assert.Equal((300, 300, 0), (s.Fighters[1].MarkFrames, s.Fighters[2].MarkFrames, s.Fighters[3].MarkFrames));
        Assert.Equal((2, 2), (s.Fighters[1].Josh, s.Fighters[2].Josh)); // the puff itself isn't boosted by its own mark
        Assert.Equal(WeaponTable.Rows[(int)WeaponId.ColourPowderPouch].Weight, 1);
    }

    [Fact]
    public void GutsArmorBlocksEveryStatusAndArmorBreakGetsPast()
    {
        foreach (WeaponId w in new[] { WeaponId.Balti, WeaponId.Taser, WeaponId.ColourPowderPouch })
        {
            GameState s = Standing(0, 100);
            Arm(s, 0, w);
            s.Fighters[1].ArmorFrames = 60;
            Fire(s, w);
            ref Fighter t = ref s.Fighters[1];
            Assert.True(t.Josh > 0, $"{w}: the armor still takes the Josh");
            Assert.Equal((0, 0, 0, 0, 0), (t.SlowFrames, t.RootFrames, t.MarkFrames, t.StunImmune, t.StunWeapon));
            Assert.Equal(FighterAction.Idle, t.Action);
        }

        // The Wrench (built in batch 4) has armor break: the armored fighter is knocked back.
        GameState wrench = Standing(0, 100);
        Arm(wrench, 0, WeaponId.Wrench);
        wrench.Fighters[1].ArmorFrames = 60;
        Fire(wrench, WeaponId.Wrench);
        Assert.Equal(FighterAction.Hitstun, wrench.Fighters[1].Action);
    }

    [Fact]
    public void TheHealthLeechTakesJoshOffItsWielder()
    {
        GameState s = Standing(0, 200, 330);
        Arm(s, 0, WeaponId.HealthLeech);
        s.Fighters[0].Josh = 50;
        WeaponRow leech = Weapons.Get(WeaponId.HealthLeech);
        for (int i = 0; i < leech.Startup + 3 * leech.Active; i++) Step(s, Buttons.Light);
        Assert.Equal(3 * leech.Damage, s.Fighters[1].Josh); // three ticks
        Assert.Equal(50 - 3 * leech.Damage, s.Fighters[0].Josh);
        Assert.Equal(0, s.Fighters[2].Josh);       // the nearest only
        Assert.Equal(FighterAction.Idle, s.Fighters[1].Action); // soft
        Assert.Equal(0, s.Fighters[0].HitStop);    // a tick never freezes the wielder

        s.Fighters[0].Josh = 1;
        for (int i = 0; i < leech.Active; i++) Step(s, Buttons.Light);
        Assert.Equal(0, s.Fighters[0].Josh);       // not below 0
    }

    // ---- Holds in the air, holds that end, movement ----

    [Theory]
    [InlineData(WeaponId.Net)]
    [InlineData(WeaponId.Taser)]
    [InlineData(WeaponId.FreezeRay)]
    public void NobodyIsHeldInTheAirTheyAreSlowedInstead(WeaponId w)
    {
        WeaponRow row = Weapons.Get(w);
        GameState s = Standing(0, w == WeaponId.Net ? 300 : 100);
        Arm(s, 0, w);
        if (w == WeaponId.FreezeRay) s.Fighters[1].SlowFrames = 140; // enough chill to freeze anyone standing
        bool hit = false;
        for (int i = 0; i < 80 && !hit; i++)
        {
            ref Fighter t = ref s.Fighters[1];
            t.Position = new FixVec2(t.Position.X, Fix.One);   // hanging in the air in reach
            t.Velocity = FixVec2.Zero;
            t.Grounded = false;
            int josh = t.Josh;
            Step(s, w == WeaponId.FreezeRay || i == 0 ? Buttons.Light : Buttons.None);
            hit = s.Fighters[1].Josh > josh;
        }
        ref Fighter d = ref s.Fighters[1];
        Assert.True(hit, $"{w} never reached a fighter in the air");
        Assert.Equal(0, d.RootFrames);
        Assert.Equal(0, d.StunWeapon);
        Assert.NotEqual(FighterAction.Stunned, d.Action);
        Assert.True(d.SlowFrames > 0, "slowed instead");
        if (row.Soft) Assert.Equal(FighterAction.Idle, d.Action);
        else Assert.Equal(FighterAction.Hitstun, d.Action); // the row's knockback
    }

    [Fact]
    public void AFrozenFighterLiftedByAGravityGunIsFreeTheFrameItLeavesTheGround()
    {
        GameState s = Standing(0, 200);
        Arm(s, 0, WeaponId.GravityGun);
        ref Fighter t = ref s.Fighters[1];
        t.Action = FighterAction.Stunned;
        t.HitstunFrames = 60;
        t.StunWeapon = (int)WeaponId.FreezeRay;
        t.StunImmune = 210;
        for (int i = 0; i < 40; i++)
        {
            Step(s, Buttons.Light);
            ref Fighter d = ref s.Fighters[1];
            if (d.Grounded)
            {
                Assert.Equal(FighterAction.Stunned, d.Action);
                continue;
            }
            Assert.Equal(FighterAction.Idle, d.Action);
            Assert.Equal(0, d.StunWeapon);
            Assert.True(d.Velocity.Y > Fix.Zero);
            return;
        }
        Assert.Fail("the beam never lifted the frozen fighter");
    }

    [Fact]
    public void ARootedFighterPushedOffALedgeByAPankhaIsFree()
    {
        var rules = new MatchRules { Stage = Stages.Graybox, Weapons = true, Guts = false };
        FixRect floor = rules.Stage.Platforms[0];
        GameState s = Simulation.CreateInitialState(rules, 2, 1);
        s.PropTimer = 1_000_000;
        ref Fighter a = ref s.Fighters[0];
        a.Position = new FixVec2(floor.MaxX - Fix.FromInt(2), floor.MaxY);
        a.Velocity = FixVec2.Zero;
        a.Grounded = true;
        a.Facing = 1;
        Arm(s, 0, WeaponId.Pankha);
        ref Fighter t = ref s.Fighters[1];
        t.Position = new FixVec2(floor.MaxX - Fix.FromRatio(3, 10), floor.MaxY);
        t.Velocity = FixVec2.Zero;
        t.Grounded = true;
        t.RootFrames = 200;
        t.StunWeapon = (int)WeaponId.Net;
        t.StunImmune = 290;
        for (int i = 0; i < 40; i++)
        {
            Step(rules, s, Buttons.Light);
            ref Fighter d = ref s.Fighters[1];
            if (d.Grounded)
            {
                Assert.True(d.RootFrames > 0);
                continue;
            }
            Assert.Equal(0, d.RootFrames);
            Assert.Equal(0, d.StunWeapon);
            return;
        }
        Assert.Fail("the gust never pushed the rooted fighter off");
    }

    [Fact]
    public void ARootedFighterDoesntDashOrRecover()
    {
        GameState free = Standing(0, 5000);
        Arm(free, 0, WeaponId.Katana);
        Step(free, Buttons.Light);
        Wait(free, 30);
        GameState rooted = Standing(0, 5000);
        Arm(rooted, 0, WeaponId.Katana);
        rooted.Fighters[0].RootFrames = 120;
        rooted.Fighters[0].StunWeapon = (int)WeaponId.Net;
        Step(rooted, Buttons.Light);
        Wait(rooted, 30);
        Assert.True(free.Fighters[0].Position.X > Fix.FromRatio(3, 2), "the Katana dashes");
        Assert.True(rooted.Fighters[0].Position.X < Cm(5), $"rooted, it moved {rooted.Fighters[0].Position.X}");

        // Up + Heavy, the recovery, does nothing while rooted: armed or not.
        foreach (WeaponId w in new[] { WeaponId.None, WeaponId.Balti })
        {
            GameState s = Standing(0, 5000);
            Arm(s, 0, w, w == WeaponId.None ? 0 : -1);
            s.Fighters[0].RootFrames = 120;
            s.Fighters[0].StunWeapon = (int)WeaponId.Net;
            Step(s, Buttons.Up | Buttons.Heavy);
            Assert.Equal(FighterAction.Idle, s.Fighters[0].Action);
            Assert.True(s.Fighters[0].Grounded);
        }
    }

    [Fact]
    public void ASlowedFighterFiringAnUziWalksAtAQuarterOfRunSpeed()
    {
        GameState s = Standing(0, 5000);
        Arm(s, 0, WeaponId.Uzi);
        s.Fighters[0].SlowFrames = 150;
        for (int i = 0; i < 30; i++) Step(s, Buttons.Light | Buttons.Right);
        Assert.Equal(FighterAction.Attack, s.Fighters[0].Action);
        Assert.Equal(Rules.Tuning.RunSpeed / 2 / 2, s.Fighters[0].Velocity.X);

        GameState rooted = Standing(0, 5000);
        Arm(rooted, 0, WeaponId.Uzi);
        rooted.Fighters[0].RootFrames = 120;
        rooted.Fighters[0].StunWeapon = (int)WeaponId.Net;
        for (int i = 0; i < 30; i++) Step(rooted, Buttons.Light | Buttons.Right);
        Assert.Equal(Fix.Zero, rooted.Fighters[0].Velocity.X);
    }

    // ---- Ticks ----

    [Fact]
    public void AFlamethrowerHeldForASecondDoesExactlyItsTicksAndFourBurnTicks()
    {
        WeaponRow flame = Weapons.Get(WeaponId.Flamethrower);
        GameState s = Standing(0, 150);
        Arm(s, 0, WeaponId.Flamethrower);
        int ticks = 0;
        for (int f = 0; f < 60; f++)
        {
            int supply = s.Fighters[0].WeaponSupply;
            Step(s, Buttons.Light);
            if (s.Fighters[0].WeaponSupply < supply) ticks++;
            Hold(s, 1, Cm(150)); // the target stays in the cone
        }
        // Ticks on frames 8, 14, ..., 56: every 6 frames while held, 1 supply each.
        Assert.Equal(9, ticks);
        Assert.Equal(flame.Supply - 9, s.Fighters[0].WeaponSupply);
        // 2 a tick, and the burn from frame 8 ticks on frames 14, 29, 44 and 59 (every 15 on the match clock).
        Assert.Equal(9 * 2 + 4, s.Fighters[1].Josh);
        Assert.Equal((int)WeaponId.Flamethrower, s.Fighters[1].DotWeapon);
        Assert.Equal(flame.EffectN - 3, s.Fighters[1].DotFrames); // refreshed by the last tick, three frames ago

        // Let go: no more ticks, and the move ends.
        for (int f = 0; f < 40; f++) Step(s);
        Assert.Equal(FighterAction.Idle, s.Fighters[0].Action);
        Assert.Equal(flame.Supply - 9, s.Fighters[0].WeaponSupply);
    }

    [Fact]
    public void AFighterInHitStopDoesntTick()
    {
        GameState s = Standing(0, 5000);
        s.Fighters[1].DotFrames = 100;
        s.Fighters[1].DotBy = 1;
        s.Fighters[1].DotWeapon = (int)WeaponId.VenomDartLauncher;
        s.Fighters[1].HitStop = 40; // frozen through frame 29, its first tick frame
        Wait(s, 40);
        Assert.Equal(0, s.Fighters[1].Josh);
        Assert.Equal(100, s.Fighters[1].DotFrames);
        Wait(s, 19);
        Assert.Equal(0, s.Fighters[1].Josh);
        Wait(s, 1); // frame 59
        Assert.Equal(1, s.Fighters[1].Josh);
    }

    [Fact]
    public void ALauncherKeepsTheKoOverALaterPoison()
    {
        GameState s = Standing(0, 5000, 5100);
        ref Fighter t = ref s.Fighters[1];
        t.LastHitBy = 1;
        t.LastHitTimer = Rules.KoCreditFrames;
        t.LastHitWeapon = WeaponId.Sledgehammer;
        t.DotFrames = 240;
        t.DotBy = 3;
        t.DotWeapon = (int)WeaponId.VenomDartLauncher;
        Wait(s, 70);
        Assert.Equal(2, s.Fighters[1].Josh); // two ticks
        Assert.Equal(1, s.Fighters[1].LastHitBy);
        Assert.Equal(WeaponId.Sledgehammer, s.Fighters[1].LastHitWeapon);
        Assert.Equal(2, Simulation.WeaponStat(s, 2, WeaponId.VenomDartLauncher, 0)); // the poisoner's damage still counts
        s.Fighters[1].Position = new FixVec2(Fix.Zero, Fix.FromInt(-200));
        Step(s);
        Assert.Equal((1, 0), (s.Fighters[0].Kos, s.Fighters[2].Kos));

        // A fighter's own poison never takes the credit: whoever hit it last keeps it.
        GameState own = Standing(0, 5000);
        ref Fighter o = ref own.Fighters[1];
        o.LastHitBy = 1;
        o.LastHitTimer = 40;
        o.DotFrames = 240;
        o.DotBy = 2;
        o.DotWeapon = (int)WeaponId.VenomDartLauncher;
        Wait(own, 35);
        Assert.Equal(1, own.Fighters[1].LastHitBy);
        own.Fighters[1].Position = new FixVec2(Fix.Zero, Fix.FromInt(-200));
        Step(own);
        Assert.Equal(1, own.Fighters[0].Kos);
    }

    // ---- Sprays ----

    [Fact]
    public void ASprayTicksWhileHeldAndSpendsOneATick()
    {
        WeaponRow flame = Weapons.Get(WeaponId.Flamethrower);
        GameState s = Standing(0, 5000);
        Arm(s, 0, WeaponId.Flamethrower);
        var tickFrames = new List<int>();
        for (int f = 0; f < 50; f++)
        {
            int supply = s.Fighters[0].WeaponSupply;
            Step(s, Buttons.Light);
            if (s.Fighters[0].WeaponSupply < supply) tickFrames.Add(f);
            Assert.Equal(f >= flame.Startup, Simulation.TryGetActiveHitbox(in s.Fighters[0], Rules.Tuning, out _));
        }
        Assert.Equal(new[] { 8, 14, 20, 26, 32, 38, 44 }, tickFrames);
        Assert.Equal(7, s.Fighters[0].BurstShots);

        // Released, the stream finishes its tick and the recovery follows; supply out, it stops too.
        GameState dry = Standing(0, 5000);
        Arm(dry, 0, WeaponId.Flamethrower, 2);
        for (int f = 0; f < 60; f++) Step(dry, Buttons.Light);
        Assert.Equal(0, dry.Fighters[0].WeaponSupply);
        Assert.Equal(FighterAction.Idle, dry.Fighters[0].Action);
    }

    [Fact]
    public void ThePichkariPushesAGroundedFighterAtFullDriftAndAnAirborneOneAtSixtyPercentOfAirSpeed()
    {
        WeaponRow jet = Weapons.Get(WeaponId.Pichkari);
        GameState s = Standing(0, 200);
        Arm(s, 0, WeaponId.Pichkari);
        for (int i = 0; i < jet.Startup; i++) Step(s, Buttons.Light);
        Fix x = s.Fighters[1].Position.X;
        Step(s, Buttons.Light);
        Assert.Equal(jet.Drift, s.Fighters[1].Position.X - x);
        x = s.Fighters[1].Position.X;
        Step(s, Buttons.Light);
        Assert.Equal(jet.Drift, s.Fighters[1].Position.X - x);
        Assert.Equal(FighterAction.Idle, s.Fighters[1].Action); // soft: 1 a tick and no knockback
        Assert.True(s.Fighters[1].Josh >= 1);

        GameState air = Standing(0, 200);
        Arm(air, 0, WeaponId.Pichkari);
        air.Fighters[1].Position = new FixVec2(Cm(200), Cm(60));
        air.Fighters[1].Grounded = false;
        for (int i = 0; i < jet.Startup; i++) Step(air, Buttons.Light);
        x = air.Fighters[1].Position.X;
        Step(air, Buttons.Light);
        Assert.False(air.Fighters[1].Grounded);
        Fix cap = Rules.Tuning.AirSpeed * Weapons.AirDriftPercent / 100;
        Assert.Equal(cap, air.Fighters[1].Position.X - x);
        Assert.True(cap < jet.Drift);
    }

    [Fact]
    public void ThePankhaTurnsSomeoneElsesArrowRound()
    {
        GameState s = Standing(0, 5000);
        Arm(s, 0, WeaponId.Pankha);
        ref Prop arrow = ref PutShot(s, WeaponId.Bow, 300, -10, owner: 1);
        arrow.Velocity = new FixVec2(Cm(-10), Fix.Zero);
        for (int i = 0; i < Weapons.Get(WeaponId.Pankha).Startup + 1; i++) Step(s, Buttons.Light);
        int slot = s.Props.Length - 1;
        Assert.Equal(PropKind.Shot, s.Props[slot].Kind);
        Assert.True(s.Props[slot].Velocity.X > Fix.Zero, "blown back the way it came");
        Assert.Equal(1, s.Props[slot].Owner);   // still its owner's

        // Its own wielder's shots fly on as they were.
        GameState mine = Standing(0, 5000);
        Arm(mine, 0, WeaponId.Pankha);
        ref Prop own = ref PutShot(mine, WeaponId.Bow, 300, -10, owner: 0);
        for (int i = 0; i < Weapons.Get(WeaponId.Pankha).Startup + 1; i++) Step(mine, Buttons.Light);
        Assert.True(mine.Props[^1].Kind != PropKind.Shot || mine.Props[^1].Velocity.X < Fix.Zero);
    }

    [Fact]
    public void AFirstSprayTouchesOnlyTheNearestFighter()
    {
        GameState s = Standing(0, 150, 300);
        Arm(s, 0, WeaponId.FreezeRay);
        for (int i = 0; i < 20; i++) Step(s, Buttons.Light);
        Assert.True(s.Fighters[1].SlowFrames > 0 && s.Fighters[1].Josh > 0);
        Assert.Equal((0, 0), (s.Fighters[2].SlowFrames, s.Fighters[2].Josh));
    }

    [Fact]
    public void TheMagnetGunDragsTheArmedInAndClonksThemOnlyCloseUp()
    {
        WeaponRow magnet = Weapons.Get(WeaponId.MagnetGun);
        GameState s = Standing(0, 500, 400);
        Arm(s, 0, WeaponId.MagnetGun);
        Arm(s, 1, WeaponId.Balti);                     // armed: dragged
        s.Fighters[2].Position = new FixVec2(Cm(400), Fix.Zero); // unarmed: left alone

        for (int i = 0; i < magnet.Startup; i++) Step(s, Buttons.Light);
        Fix x = s.Fighters[1].Position.X;
        Step(s, Buttons.Light);
        Assert.Equal(x + magnet.Drift, s.Fighters[1].Position.X); // pulled in, slower than a run
        Assert.Equal(0, s.Fighters[1].Josh);                      // no clonk from 5 m
        Assert.Equal(Cm(400), s.Fighters[2].Position.X);
        Assert.Equal(0, s.Fighters[2].Josh);

        bool clonked = false;
        for (int i = 0; i < 60 && !clonked; i++)
        {
            Step(s, Buttons.Light);
            ref Fighter t = ref s.Fighters[1];
            if (t.Josh > 0)
            {
                clonked = true;
                Assert.True(Fix.Abs(t.Position.X) <= magnet.TickNear, $"clonked from {t.Position.X}");
                Assert.True(t.Velocity.Y > Fix.Zero, "upwards");
            }
            else Assert.True(t.Position.X >= Weapons.PullStop, "a pull stops 1 m in front");
        }
        Assert.True(clonked, "dragged all the way in, it is clonked");

        // A crate flies in fast, to 1 m in front of the wielder, to be taken.
        GameState crate = Standing(0, 5000);
        Arm(crate, 0, WeaponId.MagnetGun);
        int slot = Weapons.BoxSlots(2) - 1;
        crate.Props[slot] = new Prop { Kind = PropKind.Box, Weapon = WeaponId.Uzi, Supply = 30, Owner = -1, Position = new FixVec2(Cm(600), Fix.Zero), Grounded = true };
        for (int i = 0; i < magnet.Startup + 1; i++) Step(crate, Buttons.Light);
        Assert.Equal(Cm(600) + magnet.PropDrift, crate.Props[slot].Position.X);
        for (int i = 0; i < 30; i++) Step(crate, Buttons.Light);
        Assert.Equal(PropKind.Box, crate.Props[slot].Kind);
        Assert.Equal(Weapons.PullStop, crate.Props[slot].Position.X);
    }

    [Fact]
    public void TheGravityGunFloatsAFighterUpAndAwayAndPushesCratesOff()
    {
        WeaponRow gun = Weapons.Get(WeaponId.GravityGun);
        GameState s = Standing(0, 200);
        Arm(s, 0, WeaponId.GravityGun);
        int slot = Weapons.BoxSlots(2) - 1;
        s.Props[slot] = new Prop { Kind = PropKind.Box, Weapon = WeaponId.Uzi, Supply = 30, Owner = -1, Position = new FixVec2(Cm(300), Fix.Zero), Grounded = true };
        for (int i = 0; i < gun.Startup + 30; i++) Step(s, Buttons.Light);
        Assert.False(s.Fighters[1].Grounded);
        Assert.True(s.Fighters[1].Position.Y > Fix.FromRatio(6, 5), $"it rose to {s.Fighters[1].Position.Y}");
        Assert.True(s.Fighters[1].Position.X > Cm(250), "and drifted away");
        Assert.True(s.Props[slot].Position.X > Cm(600), "the crate was thrown off");
    }

    /// <summary>A 16 m floor inside an arena's blast lines: 4 m past its ledges is still in the match.</summary>
    private static readonly StageDefinition Ledges = new()
    {
        Id = "test-ledges",
        Platforms = new[] { new FixRect(Fix.FromInt(-8), Fix.FromInt(-2), Fix.FromInt(8), Fix.Zero) },
        SpawnPoints = new[] { new FixVec2(Fix.FromInt(-2), Fix.Zero), new FixVec2(Fix.FromInt(2), Fix.Zero) },
        BlastZone = Arena.BlastZone,
    };

    /// <summary>
    /// A fighter recovering from 4 m outside a ledge, with its air jump and recovery, against a gust
    /// or a jet held from the ledge the whole time, gets back: in the air the drift is capped at 60%
    /// of air speed, so it still gains 40% against it.
    /// </summary>
    [Theory]
    [InlineData("graybox", WeaponId.Pankha, 100)]
    [InlineData("graybox", WeaponId.Pankha, -100)]
    [InlineData("graybox", WeaponId.Pichkari, 100)]
    [InlineData("graybox", WeaponId.Pichkari, -100)]
    [InlineData("test-ledges", WeaponId.Pankha, 100)]
    [InlineData("test-ledges", WeaponId.Pichkari, 100)]
    public void ThePankhaAndThePichkariCantHoldARecoveringFighterOut(string stageId, WeaponId w, int heightCm)
    {
        StageDefinition stage = stageId == "graybox" ? Stages.Graybox : Ledges;
        var rules = new MatchRules { Stage = stage, Weapons = true };
        FixRect floor = stage.Platforms[0];
        GameState s = Simulation.CreateInitialState(rules, 2, 1);
        s.PropTimer = 1_000_000;
        ref Fighter a = ref s.Fighters[0];
        a.Position = new FixVec2(floor.MaxX - Fix.Half, floor.MaxY);
        a.Velocity = FixVec2.Zero;
        a.Grounded = true;
        a.Facing = 1;
        Arm(s, 0, w);
        ref Fighter d = ref s.Fighters[1];
        d.Position = new FixVec2(floor.MaxX + Fix.FromInt(4), floor.MaxY + Cm(heightCm));
        d.Velocity = FixVec2.Zero;
        d.Grounded = false;
        d.Facing = -1;
        var inputs = new Buttons[2];
        bool pushed = false, back = false;
        for (int f = 0; f < 600 && !back; f++)
        {
            s.Fighters[0].WeaponSupply = Weapons.Get(w).Supply; // held all the way, never running dry
            inputs[0] = Buttons.Light;
            inputs[1] = SparringBot.Think(s, rules, 1, CpuLevel.Hard);
            Fix x = s.Fighters[1].Position.X;
            Simulation.Advance(s, rules, inputs);
            Assert.Equal(rules.Stocks, s.Fighters[1].Stocks);
            pushed |= s.Fighters[1].Josh > 0;
            back = s.Fighters[1].Grounded && s.Fighters[1].Position.Y == floor.MaxY;
        }
        Assert.True(pushed, "the stream reached the recovering fighter");
        Assert.True(back, $"{w} on {stage.Id}: never got back");
    }

    // ---- Lobs and bouncers ----

    [Fact]
    public void SnowballsArcLandAndSlow()
    {
        GameState s = Standing(0, 5000);
        Arm(s, 0, WeaponId.Snowballs);
        Fire(s, WeaponId.Snowballs);
        List<int> slots = ShotSlots(s, WeaponId.Snowballs);
        Assert.Single(slots);
        int slot = slots[0];
        Fix vy = s.Props[slot].Velocity.Y;
        Assert.True(vy > Fix.Zero, "it is thrown upwards");
        int frames = 0;
        while (s.Props[slot].Kind == PropKind.Shot && frames < 200)
        {
            Step(s);
            frames++;
            if (s.Props[slot].Kind == PropKind.Shot) Assert.True(s.Props[slot].Velocity.Y < vy);
            if (s.Props[slot].Kind == PropKind.Shot) vy = s.Props[slot].Velocity.Y;
        }
        Assert.InRange(frames, 20, 60); // it comes down and is gone where it lands

        GameState hit = Standing(0, 400);
        Arm(hit, 0, WeaponId.Snowballs);
        for (int i = 0; i < 60 && hit.Fighters[1].Josh == 0; i++) Step(hit, i == 0 ? Buttons.Light : Buttons.None);
        Assert.Equal(3, hit.Fighters[1].Josh);
        Assert.True(hit.Fighters[1].SlowFrames >= 49);
        Assert.Empty(ShotSlots(hit, WeaponId.Snowballs));
    }

    [Fact]
    public void ATiffinTossIsThreeContainersEachBouncingTwiceThenGone()
    {
        GameState s = Standing(0, 5000);
        Arm(s, 0, WeaponId.TiffinStack);
        Fire(s, WeaponId.TiffinStack);
        List<int> slots = ShotSlots(s, WeaponId.TiffinStack);
        Assert.Equal(3, slots.Count);
        Assert.Equal(3, slots.Select(i => s.Props[i].Velocity.Y).Distinct().Count()); // they fan out
        Assert.Equal(2, s.Fighters[0].WeaponSupply);                                  // one toss
        var bounces = slots.ToDictionary(i => i, _ => 0);
        for (int f = 0; f < 300; f++)
        {
            Step(s);
            foreach (int i in slots)
                if (s.Props[i].Kind == PropKind.Shot) bounces[i] = s.Props[i].Timer;
        }
        Assert.All(slots, i => Assert.Equal(PropKind.None, s.Props[i].Kind));
        Assert.All(slots, i => Assert.Equal(2, bounces[i]));
    }

    [Fact]
    public void ACricketBallHitAfterItPitchesIsTheSweetSpot()
    {
        GameState close = Standing(0, 150);
        Arm(close, 0, WeaponId.CricketBall);
        for (int i = 0; i < 40 && close.Fighters[1].Josh == 0; i++) Step(close, i == 0 ? Buttons.Light : Buttons.None);
        Assert.Equal(8, close.Fighters[1].Josh);

        GameState far = Standing(0, 800);
        Arm(far, 0, WeaponId.CricketBall);
        for (int i = 0; i < 60 && far.Fighters[1].Josh == 0; i++) Step(far, i == 0 ? Buttons.Light : Buttons.None);
        Assert.Equal(Sweet(8), far.Fighters[1].Josh);
        Assert.True(far.Fighters[1].LastHitSweet);
    }

    [Fact]
    public void TheChakramComesBackOffTheScreenEdgeOnceAndItsWayBackIsSweet()
    {
        var rules = new MatchRules { Stage = Arena, Weapons = true, Guts = false };
        GameState s = Standing(rules, 1400, 1200);
        s.Fighters[1].Facing = 1;
        Arm(s, 0, WeaponId.Chakram);
        Step(rules, s, Buttons.Light);
        for (int i = 0; i < Weapons.Get(WeaponId.Chakram).Startup; i++) Step(rules, s);
        List<int> slots = ShotSlots(s, WeaponId.Chakram);
        Assert.Single(slots);
        int slot = slots[0];
        Fix right = Arena.BlastZone.MaxX - Weapons.ScreenInset;
        bool turned = false;
        for (int f = 0; f < 100 && s.Props[slot].Kind == PropKind.Shot; f++)
        {
            Step(rules, s);
            ref Prop p = ref s.Props[slot];
            if (p.Kind != PropKind.Shot) break;
            Assert.True(p.Position.X <= right);
            if (!turned && p.Velocity.X < Fix.Zero)
            {
                turned = true;
                Assert.Equal(1, p.IgnoreTimer);
                Assert.Equal(right, p.Position.X);
            }
        }
        Assert.True(turned);
        Assert.Equal(PropKind.None, s.Props[slot].Kind);
        Assert.Equal(Sweet(7), s.Fighters[1].Josh); // behind the thrower: only hit on the way back
        Assert.True(s.Fighters[1].LastHitSweet);
    }

    [Fact]
    public void TheSteelTumblerSkipsThroughEveryone()
    {
        GameState s = Standing(0, 300, 500);
        Arm(s, 0, WeaponId.SteelTumbler);
        Fire(s, WeaponId.SteelTumbler);
        int slot = ShotSlots(s, WeaponId.SteelTumbler).Single();
        int bounces = 0;
        for (int f = 0; f < 60 && s.Props[slot].Kind == PropKind.Shot; f++)
        {
            Step(s);
            if (s.Props[slot].Kind == PropKind.Shot) bounces = s.Props[slot].Timer;
        }
        int damage = Weapons.Get(WeaponId.SteelTumbler).Damage;
        Assert.Equal(damage, s.Fighters[1].Josh);
        Assert.False(s.Fighters[1].LastHitSweet);
        Assert.Equal(Sweet(damage), s.Fighters[2].Josh); // the second fighter on is the sweet spot
        Assert.True(s.Fighters[2].LastHitSweet);
        Assert.True(bounces >= 1, "it skipped off the floor");
    }

    [Fact]
    public void GrenadesBounceTwiceThenLieThere()
    {
        GameState s = Standing(0, 5000);
        Arm(s, 0, WeaponId.GrenadeLauncher);
        Fire(s, WeaponId.GrenadeLauncher);
        int slot = ShotSlots(s, WeaponId.GrenadeLauncher).Single();
        int ups = 0;
        bool wasDown = false;
        for (int f = 0; f < 70 && s.Props[slot].Kind == PropKind.Shot; f++)
        {
            Fix vy = s.Props[slot].Velocity.Y;
            Step(s);
            ref Prop p = ref s.Props[slot];
            if (p.Kind != PropKind.Shot) break;
            if (vy < Fix.Zero && p.Velocity.Y > Fix.Zero) ups++;
            wasDown |= p.Grounded;
        }
        Assert.Equal(2, ups);
        Assert.Equal(2, s.Props[slot].Hits);
        Assert.True(s.Props[slot].Grounded, "after the last bounce it lies there");
        Wait(s, 10);
        Assert.Equal(PropKind.None, s.Props[slot].Kind); // and bursts at 80 frames
    }

    [Fact]
    public void TheChaklaBurstsWhereItLands()
    {
        GameState s = Standing(0, 5000);
        Arm(s, 0, WeaponId.Chakla);
        Fire(s, WeaponId.Chakla);
        int slot = ShotSlots(s, WeaponId.Chakla).Single();
        int frames = 0;
        while (s.Props[slot].Kind == PropKind.Shot && frames < 200)
        {
            Assert.False(s.Props[slot].Grounded);
            Step(s);
            frames++;
        }
        Assert.True(frames < 40, $"it burst after {frames} frames: where it landed, not at the end of its fuse");

        // Thrown at someone, it bursts on them, a direct hit.
        GameState on = Standing(0, 250);
        Arm(on, 0, WeaponId.Chakla);
        for (int i = 0; i < 40 && on.Fighters[1].Josh == 0; i++) Step(on, i == 0 ? Buttons.Light : Buttons.None);
        Assert.Equal(Sweet(10), on.Fighters[1].Josh);
    }

    [Fact]
    public void TheCannonballGoesThroughEveryoneInItsPath()
    {
        GameState s = Standing(0, 200, 260);
        Arm(s, 0, WeaponId.Cannon);
        for (int i = 0; i < 60; i++) Step(s, i == 0 ? Buttons.Light : Buttons.None);
        Assert.Equal(22, s.Fighters[1].Josh);
        Assert.Equal(22, s.Fighters[2].Josh);
    }

    [Fact]
    public void RubberChickensLeaveAtADifferentHeightEachShot()
    {
        GameState s = Standing(0, 5000);
        Arm(s, 0, WeaponId.RubberChickenLauncher);
        var vys = new List<Fix>();
        for (int k = 0; k < 3; k++)
        {
            Fire(s, WeaponId.RubberChickenLauncher);
            int newest = ShotSlots(s, WeaponId.RubberChickenLauncher).OrderBy(i => s.Props[i].Age).First();
            vys.Add(s.Props[newest].Velocity.Y);
            Wait(s, 20);
        }
        Assert.Equal(3, vys.Distinct().Count());
    }

    // ---- The batch's rows ----

    [Fact]
    public void TheTwentyFiveRowsOfTheBatchAreInTheCrates()
    {
        WeaponId[] batch =
        {
            WeaponId.Chakla, WeaponId.Balti, WeaponId.BathroomMug, WeaponId.TiffinStack, WeaponId.ChaiKettle, WeaponId.SteelTumbler,
            WeaponId.Pankha, WeaponId.CricketBall, WeaponId.Net, WeaponId.Chakram, WeaponId.BlowDarts, WeaponId.Chainsaw,
            WeaponId.MagnetGun, WeaponId.GrenadeLauncher, WeaponId.Cannon, WeaponId.HealthLeech, WeaponId.VenomDartLauncher,
            WeaponId.FreezeRay, WeaponId.Taser, WeaponId.Flamethrower, WeaponId.Pichkari, WeaponId.ColourPowderPouch,
            WeaponId.Snowballs, WeaponId.GravityGun, WeaponId.RubberChickenLauncher,
        };
        Assert.Equal(25, batch.Distinct().Count());
        Assert.All(batch, id => Assert.Contains(id, Weapons.CrateList));
        Assert.True(Weapons.CrateList.Length >= 68);
    }

    [Fact]
    public void ACpuHoldsASprayForItsBurst()
    {
        WeaponRow flame = Weapons.Get(WeaponId.Flamethrower);
        GameState s = Standing(0, 200);
        Arm(s, 0, WeaponId.Flamethrower);
        for (int f = 0; f < 120; f++)
        {
            Buttons b = SparringBot.Think(s, Rules, 0, CpuLevel.Hard);
            Step(s, b);
            Hold(s, 1, Cm(200));
        }
        Assert.Equal(0, (flame.Supply - s.Fighters[0].WeaponSupply) % flame.CpuBurst);
        Assert.True(flame.Supply - s.Fighters[0].WeaponSupply >= flame.CpuBurst);
    }
}
