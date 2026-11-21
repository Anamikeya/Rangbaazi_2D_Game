using Rangbaazi.Sim.Diagnostics;
using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim.Tests;

/// <summary>
/// Batch 4 of combat spec v0.4: guards, melee tricks and hidden rows (section 2.7); traps, zones and
/// walls (2.8); the one-frame-of-hits rules (1.7) and Become (3.2). The archetype tests, the
/// table-driven flight-flag test, the index-swapped one-frame tests, and the trick weapons.
/// </summary>
public class GuardTrapWeaponTests
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

    /// <summary>
    /// A prop of this flight in the last free slot from the end: a shot at hand height, or a placed
    /// prop (or one left behind) lying on the floor, with its row's timer.
    /// </summary>
    private static int Put(GameState s, WeaponId w, ShotKind shot, int xCm, int owner, int vxCm = 0, int timer = -1, int yCm = -1)
    {
        WeaponRow row = Weapons.Get(w);
        int slot = s.Props.Length - 1;
        while (s.Props[slot].Kind != PropKind.None) slot--;
        bool lying = WeaponTable.Placed(shot) || shot == ShotKind.Drop;
        s.Props[slot] = new Prop
        {
            Kind = PropKind.Shot, Shot = shot, Weapon = w, Owner = owner, OwnerClear = true,
            Position = new FixVec2(Cm(xCm), yCm >= 0 ? Cm(yCm) : lying ? Fix.Zero : Fix.FromRatio(4, 5)),
            Velocity = new FixVec2(Cm(vxCm), Fix.Zero),
            Grounded = lying && yCm <= 0,
            Timer = timer >= 0 ? timer : shot is ShotKind.Zone or ShotKind.Wall ? row.ZoneLife : shot == ShotKind.Pad ? 600
                : shot is ShotKind.Speaker or ShotKind.Gate or ShotKind.Decoy ? row.Life : row.Fuse,
        };
        return slot;
    }

    private static int Count(GameState s, ShotKind shot) => s.Props.Count(p => p.Kind == PropKind.Shot && p.Shot == shot);

    private static int Sweet(int damage) => damage * Weapons.SweetPercent / 100;

    // ---- Guards (2.7) ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheParatBlocksAJabFromTheFrontNotFromBehind(bool front)
    {
        GameState s = Standing(0, front ? 100 : -100);
        s.Fighters[1].Facing = front ? -1 : 1;
        Arm(s, 0, WeaponId.Parat);
        Step(s, Buttons.Light);
        Step(s, Buttons.Light);
        Step(s, Buttons.Light);
        Assert.True(Simulation.Guarding(in s.Fighters[0]));
        Step(s, Buttons.Light, Buttons.Light); // the jab
        for (int i = 0; i < 12; i++) Step(s, Buttons.Light);
        if (front)
        {
            Assert.Equal(0, s.Fighters[0].Josh);
            Assert.True(s.Fighters[1].AttackConnected); // a blocked attack has connected
            Assert.True(s.Fighters[1].Position.X > Fix.One); // and was pushed back
            Assert.Equal(7, s.Fighters[0].WeaponSupply); // a block costs one
        }
        else
        {
            Assert.Equal(6, s.Fighters[0].Josh);
            Assert.Equal(8, s.Fighters[0].WeaponSupply);
        }
    }

    [Fact]
    public void TheParatsHeldGuardLastsAtMostNinetyFramesThenBashes()
    {
        GameState s = Standing(0, 5000);
        Arm(s, 0, WeaponId.Parat);
        int startup = Weapons.Get(WeaponId.Parat).Startup;
        Step(s, Buttons.Light);
        for (int i = 0; i < startup - 1 + Weapons.HoldMaxFrames; i++) Step(s, Buttons.Light);
        Assert.Equal(startup - 1, s.Fighters[0].ActionFrame);
        Assert.True(Simulation.Guarding(in s.Fighters[0]));
        Step(s, Buttons.Light);
        Assert.Equal(startup, s.Fighters[0].ActionFrame); // still held, but the bash comes out
        Assert.False(Simulation.Guarding(in s.Fighters[0]));

        // Let go, and the bash comes out at once.
        GameState t = Standing(0, 5000);
        Arm(t, 0, WeaponId.Parat);
        Step(t, Buttons.Light);
        for (int i = 0; i < 20; i++) Step(t, Buttons.Light);
        Assert.Equal(startup - 1, t.Fighters[0].ActionFrame);
        Step(t);
        Assert.Equal(startup, t.Fighters[0].ActionFrame);
    }

    [Fact]
    public void AGuardWithNothingLeftIsThrownAtTheNextPress()
    {
        GameState s = Standing(0, 100);
        Arm(s, 0, WeaponId.Parat, supply: 1);
        Step(s, Buttons.Light);
        Step(s, Buttons.Light);
        Step(s, Buttons.Light);
        Step(s, Buttons.Light, Buttons.Light);
        for (int i = 0; i < 8; i++) Step(s, Buttons.Light);
        Assert.Equal(0, s.Fighters[0].Josh);
        Assert.Equal(0, s.Fighters[0].WeaponSupply);
        for (int i = 0; i < 60; i++) Step(s);
        Step(s, Buttons.Light);
        Assert.Equal(MoveId.WeaponThrow, s.Fighters[0].Move);
    }

    [Theory]
    [InlineData(WeaponId.Wrench)]
    [InlineData(WeaponId.Sledgehammer)]
    public void AGuardBreakerGoesThroughTheParat(WeaponId breaker)
    {
        int damage = Weapons.Get(breaker).Damage;
        GameState s = Standing(0, 100);
        Arm(s, 0, WeaponId.Parat);
        Arm(s, 1, breaker);
        Step(s, Buttons.Light);
        Step(s, Buttons.Light);
        Step(s, Buttons.Light);
        Step(s, Buttons.Light, Buttons.Light);
        for (int i = 0; i < Weapons.Get(breaker).Startup; i++) Step(s, Buttons.Light);
        Assert.Equal(damage, s.Fighters[0].Josh);
        Assert.Equal(FighterAction.Hitstun, s.Fighters[0].Action);
        Assert.Equal(8, s.Fighters[0].WeaponSupply); // not a block
    }

    [Fact]
    public void TheThaliBlocksWhileItWindsUpThenFlies()
    {
        GameState s = Standing(0, 100);
        Arm(s, 0, WeaponId.Thali);
        Step(s, Buttons.Light, Buttons.Light); // both at once: the jab lands on the Thali's frame 4
        for (int i = 0; i < 20; i++) Step(s);
        Assert.Equal(0, s.Fighters[0].Josh);
        Assert.Equal(7, s.Fighters[1].Josh); // the plate flew and stopped in them
        Assert.Equal(6 - 2, s.Fighters[0].WeaponSupply); // one for the block, one for the throw
    }

    [Fact]
    public void AGuardDestroysAShotFromTheFrontAndIgnoresABurstInFront()
    {
        GameState s = Standing(0, 5000, -5000);
        Arm(s, 0, WeaponId.Tawa);
        Step(s, Buttons.Light);
        Wait(s, 2);
        Put(s, WeaponId.Uzi, ShotKind.Bullet, 80, owner: 1, vxCm: -30);
        Step(s);
        Assert.Equal(0, Count(s, ShotKind.Bullet));
        Assert.Equal(0, s.Fighters[0].Josh);
        Assert.Equal(6, s.Fighters[0].WeaponSupply);

        int bomb = Put(s, WeaponId.StickGrenades, ShotKind.Bomb, 150, owner: 1, timer: 1, yCm: 0);
        s.Props[bomb].Grounded = true;
        Step(s);
        Assert.Equal(PropKind.None, s.Props[bomb].Kind); // it burst
        Assert.Equal(0, s.Fighters[0].Josh);
        Assert.Equal(5, s.Fighters[0].WeaponSupply);
    }

    /// <summary>
    /// A fast shot is often first seen overlapping a guard after it has passed the guard's middle.
    /// The side is judged by the way the shot moves, so a bullet or a bolt fired straight at a
    /// guard's front is blocked at every spacing, and one from behind always hits.
    /// </summary>
    [Theory]
    [InlineData(WeaponId.Uzi, ShotKind.Bullet, true)]
    [InlineData(WeaponId.Crossbow, ShotKind.Arrow, true)]
    [InlineData(WeaponId.Uzi, ShotKind.Bullet, false)]
    [InlineData(WeaponId.Crossbow, ShotKind.Arrow, false)]
    public void AGuardBlocksAFastShotFromTheFrontAtEverySpacingAndNeverOneFromBehind(WeaponId gun, ShotKind flight, bool front)
    {
        int speed = (int)(Weapons.Get(gun).Vx.Raw * 100 / Fix.One.Raw); // cm a frame
        int blocked = 0, hits = 0;
        for (int offset = 0; offset < speed; offset += 5) // every phase of one frame's travel
        {
            GameState s = Standing(0, 5000, -5000);
            Arm(s, 0, WeaponId.Parat);
            Step(s, Buttons.Light);
            Step(s, Buttons.Light);
            Step(s, Buttons.Light);
            Assert.True(Simulation.Guarding(in s.Fighters[0]));
            int from = front ? 200 + offset : -(200 + offset);
            int shot = Put(s, gun, flight, from, owner: 1, vxCm: front ? -speed : speed);
            for (int i = 0; i < 12 && s.Props[shot].Kind != PropKind.None; i++) Step(s, Buttons.Light);
            if (s.Fighters[0].Josh == 0 && s.Fighters[0].WeaponSupply == 7) blocked++;
            if (s.Fighters[0].Josh > 0) hits++;
        }
        int tries = (speed + 4) / 5;
        if (front) Assert.Equal(tries, blocked);
        else Assert.Equal(tries, hits);
    }

    /// <summary>Each block costs one: a guard that has spent its last blocks nothing more, and the Parat's tray isn't held up.</summary>
    [Fact]
    public void AGuardThatHasSpentItsLastSupplyBlocksNothingMore()
    {
        GameState s = Standing(0, 5000, -5000);
        Arm(s, 0, WeaponId.Tawa, supply: 1);
        Step(s, Buttons.Light);
        Wait(s, 2);
        Put(s, WeaponId.Uzi, ShotKind.Bullet, 80, owner: 1, vxCm: -30);
        Step(s);
        Assert.Equal(0, s.Fighters[0].Josh); // blocked, for the last one
        Assert.Equal(0, s.Fighters[0].WeaponSupply);
        Assert.False(Simulation.Guarding(in s.Fighters[0]));
        Put(s, WeaponId.Uzi, ShotKind.Bullet, 80, owner: 1, vxCm: -30);
        Step(s);
        Assert.Equal(Weapons.Get(WeaponId.Uzi).Damage, s.Fighters[0].Josh);

        // The Parat at 1: the block spends it, and the bash comes out at once though Light is still held.
        GameState t = Standing(0, 5000, -5000);
        Arm(t, 0, WeaponId.Parat, supply: 1);
        for (int i = 0; i < 4; i++) Step(t, Buttons.Light);
        Put(t, WeaponId.Uzi, ShotKind.Bullet, 80, owner: 1, vxCm: -30);
        Step(t, Buttons.Light);
        Assert.Equal(0, t.Fighters[0].Josh);
        Assert.Equal(0, t.Fighters[0].WeaponSupply);
        int startup = Weapons.Get(WeaponId.Parat).Startup;
        for (int i = 0; i < startup && t.Fighters[0].ActionFrame < startup; i++) Step(t, Buttons.Light);
        Assert.Equal(startup, t.Fighters[0].ActionFrame);
        Put(t, WeaponId.Uzi, ShotKind.Bullet, 80, owner: 1, vxCm: -30);
        Step(t, Buttons.Light);
        Assert.Equal(Weapons.Get(WeaponId.Uzi).Damage, t.Fighters[0].Josh);
    }

    // ---- Thrown and flung weapons are plain thrown props (2.7) ----

    /// <summary>
    /// A thrown or flung weapon takes none of its weapon's shot columns: whatever it carries, no
    /// swing or burst breaks it (so a flung trap, chicken or speaker can't leave the match), it
    /// doesn't bounce off the screen edge, and it can hit its thrower.
    /// </summary>
    [Theory]
    [InlineData(WeaponId.MouseTrap)]
    [InlineData(WeaponId.VenomTrap)]
    [InlineData(WeaponId.Chicken)]
    [InlineData(WeaponId.DarkEnergy)]
    [InlineData(WeaponId.SonicSpeaker)]
    public void AFlungWeaponThatBreaksAsAShotIsNotBrokenBySwingsOrBursts(WeaponId w)
    {
        GameState s = Standing(0, 5000);
        int thrown = Put(s, w, ShotKind.Thrown, 100, owner: 1, vxCm: -1);
        s.Props[thrown].Supply = Weapons.Get(w).Supply; // flung by a Hockey Stick: it carries its supply
        Assert.False(Weapons.Breakable(in s.Props[thrown]));
        Arm(s, 0, WeaponId.Belan);
        Step(s, Buttons.Light);
        for (int i = 0; i < Weapons.Get(WeaponId.Belan).Startup + 2; i++)
        {
            s.Props[thrown].Position = new FixVec2(Cm(100), Fix.FromRatio(4, 5));
            s.Props[thrown].Velocity = new FixVec2(Cm(-1), Fix.Zero);
            Step(s);
        }
        Assert.Equal(ShotKind.Thrown, s.Props[thrown].Shot);
        Assert.False(s.Fighters[0].AttackConnected); // there was nothing to break

        int bomb = Put(s, WeaponId.CherryBombs, ShotKind.Bomb, 150, owner: 0, timer: 1, yCm: 0); // the other side's: a burst spares its own
        s.Props[bomb].Grounded = true;
        s.Props[thrown].Position = new FixVec2(Cm(100), Fix.FromRatio(4, 5));
        Step(s);
        Assert.Equal(PropKind.None, s.Props[bomb].Kind); // it burst over the thrown weapon
        Assert.NotEqual(PropKind.None, s.Props[thrown].Kind);
    }

    [Theory]
    [InlineData(WeaponId.Chakram)]
    [InlineData(WeaponId.SteelTumbler)]
    public void AThrownWeaponThatBouncesAsAShotDoesntBounceOffTheScreenEdge(WeaponId w)
    {
        var rules = new MatchRules { Stage = Arena, Weapons = true, Guts = false };
        GameState s = Standing(rules, -1000, -1200);
        Fix right = Arena.BlastZone.MaxX - Weapons.ScreenInset;
        int thrown = Put(s, w, ShotKind.Thrown, (int)((right - Fix.FromRatio(1, 10)).Raw * 100 / Fix.One.Raw), owner: 0, vxCm: 32, yCm: 300);
        for (int i = 0; i < 4; i++) Step(rules, s);
        Assert.True(s.Props[thrown].Kind == PropKind.None || s.Props[thrown].Velocity.X > Fix.Zero, "it flew on past the screen edge");
        Assert.True(s.Props[thrown].Kind == PropKind.None || s.Props[thrown].IgnoreTimer == 0);
    }

    [Fact]
    public void AThrownDarkEnergyCanHitItsThrower()
    {
        GameState s = Standing(0, 5000);
        int thrown = Put(s, WeaponId.DarkEnergy, ShotKind.Thrown, 0, owner: 0, vxCm: 0);
        s.Props[thrown].Velocity = FixVec2.Zero;
        Step(s);
        Assert.Equal(Weapons.Get(WeaponId.DarkEnergy).Throw, s.Fighters[0].Josh);
    }

    /// <summary>
    /// The Sledgehammer breaks any wall, speaker, trap or decoy in one swing (section 2.1): the
    /// Banana Peel (1 swing), a Decoy Crate and a rigged crate (2 swings, so any two swings pop one)
    /// are breakable too.
    /// </summary>
    [Theory]
    [InlineData(WeaponId.BananaPeel, ShotKind.Mine)]
    [InlineData(WeaponId.MouseTrap, ShotKind.Mine)]
    [InlineData(WeaponId.VenomTrap, ShotKind.Mine)]
    [InlineData(WeaponId.DecoyCrate, ShotKind.Decoy)]
    [InlineData(WeaponId.CrateDetonator, ShotKind.Decoy)]
    [InlineData(WeaponId.SonicSpeaker, ShotKind.Speaker)]
    [InlineData(WeaponId.Matka, ShotKind.Wall)]
    public void TheSledgehammerBreaksEveryTrapDecoySpeakerAndWallInOneSwing(WeaponId w, ShotKind shot)
    {
        GameState s = Standing(0, 5000);
        Arm(s, 0, WeaponId.Sledgehammer);
        int prop = Put(s, w, shot, 120, owner: 1, timer: shot == ShotKind.Mine ? 0 : -1); // a speaker faces away (supply 0: right)
        Assert.True(Weapons.Breakable(in s.Props[prop]));
        Fire(s, WeaponId.Sledgehammer);
        Wait(s, 2);
        Assert.Equal(PropKind.None, s.Props[prop].Kind);
    }

    [Theory]
    [InlineData(WeaponId.DecoyCrate)]
    [InlineData(WeaponId.CrateDetonator)]
    public void TwoSwingsPopADecoy(WeaponId w)
    {
        GameState s = Standing(0, 5000);
        Arm(s, 0, WeaponId.Belan);
        int prop = Put(s, w, ShotKind.Decoy, 100, owner: 1);
        Fire(s, WeaponId.Belan);
        Wait(s, 3);
        Assert.Equal(1, s.Props[prop].IgnoreFighter); // one swing taken
        while (s.Fighters[0].Action == FighterAction.Attack) Step(s);
        Fire(s, WeaponId.Belan);
        Wait(s, 3);
        Assert.Equal(PropKind.None, s.Props[prop].Kind);
    }

    // ---- Other defences (2.7) ----

    [Fact]
    public void TheLathisSpinDestroysABullet()
    {
        GameState s = Standing(0, 5000);
        Arm(s, 0, WeaponId.Lathi);
        Step(s, Buttons.Light);
        Wait(s, Weapons.Get(WeaponId.Lathi).Startup - 1);
        Put(s, WeaponId.Uzi, ShotKind.Bullet, 150, owner: 1, vxCm: -5);
        Put(s, WeaponId.Uzi, ShotKind.Bullet, -150, owner: 1, vxCm: 5); // behind too: it spins
        Step(s);
        Assert.Equal(0, Count(s, ShotKind.Bullet));
        Assert.Equal(0, s.Fighters[0].Josh);
    }

    [Theory]
    [InlineData(WeaponId.Lathi)]
    [InlineData(WeaponId.BaseballBat)]
    public void NeitherTheLathiNorABatTouchesAPadAMineOrAWall(WeaponId swing)
    {
        GameState s = Standing(0, 5000);
        Arm(s, 0, swing);
        int pad = Put(s, WeaponId.Trampoline, ShotKind.Pad, 100, owner: 1);
        int mine = Put(s, WeaponId.Mines, ShotKind.Mine, swing == WeaponId.Lathi ? -100 : 70, owner: 1);
        int wall = Put(s, WeaponId.Matka, ShotKind.Wall, 190, owner: 1);
        Fire(s, swing);
        Wait(s, 3);
        Assert.Equal(ShotKind.Pad, s.Props[pad].Shot);
        Assert.Equal(1, s.Props[pad].Owner);
        Assert.Equal(ShotKind.Mine, s.Props[mine].Shot);
        Assert.Equal(1, s.Props[mine].Owner);
        Assert.Equal(ShotKind.Wall, s.Props[wall].Shot);
        Assert.Equal(1, s.Props[wall].IgnoreFighter); // a swing took one off the wall, as any swing does
    }

    // ---- The flight flags, table-driven (1.3) ----

    public static IEnumerable<object[]> Flights() => Enumerable.Range(1, 17).Select(f => new object[] { f });

    /// <summary>A row whose shot flies this way, or that leaves it behind; a thrown prop carries a Belan.</summary>
    private static WeaponId RowFlying(ShotKind kind)
    {
        if (kind == ShotKind.Thrown) return WeaponId.Belan;
        for (int i = 1; i < WeaponTable.Count; i++)
            if (WeaponTable.Rows[i].Flight == kind) return (WeaponId)i;
        for (int i = 1; i < WeaponTable.Count; i++)
            if (Weapons.LeftFlight(WeaponTable.Rows[i]) == kind) return (WeaponId)i;
        throw new InvalidOperationException($"no row flies {kind}");
    }

    /// <summary>
    /// For each of the 17 flights, one prop of it meets a bat swing, a Lathi spin, a Pankha gust, a
    /// parry, a guard and a wall. A flight with the <c>flies</c> flag is returned, destroyed, turned,
    /// destroyed, destroyed and stopped; any other is left alone by all six (a swing may still take
    /// its one swing off a breakable prop). The black hole keeps today's rule: it only pulls and
    /// bursts, so no fighter touches it and a parry or a guard doesn't reach it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Flights))]
    public void EveryFlightMeetsEachDefenceAsItsFlagsSay(int flight)
    {
        var kind = (ShotKind)flight;
        WeaponId w = RowFlying(kind);
        bool flies = WeaponTable.Flies(kind);

        // Fighter 0 defends; fighter 1, far away, owns the prop.
        foreach (string defence in new[] { "bat", "lathi", "pankha", "parry", "guard", "wall" })
        {
            GameState s = Standing(0, 5000, -5000);
            Buttons held = Buttons.None;
            switch (defence)
            {
                case "bat":
                case "lathi":
                    Arm(s, 0, defence == "bat" ? WeaponId.BaseballBat : WeaponId.Lathi);
                    Step(s, Buttons.Light);
                    Wait(s, Weapons.Get(s.Fighters[0].Weapon).Startup - 1);
                    break;
                case "pankha":
                    Arm(s, 0, WeaponId.Pankha);
                    held = Buttons.Light;
                    Step(s, Buttons.Light);
                    for (int i = 0; i < Weapons.Get(WeaponId.Pankha).Startup - 1; i++) Step(s, Buttons.Light);
                    break;
                case "parry":
                    Step(s, Buttons.Dodge);
                    break;
                case "guard":
                    Arm(s, 0, WeaponId.Parat);
                    held = Buttons.Light;
                    for (int i = 0; i < 3; i++) Step(s, Buttons.Light);
                    break;
                default:
                    Hold(s, 0, Fix.FromInt(-50));
                    Put(s, WeaponId.Matka, ShotKind.Wall, 200, owner: 2); // someone else's wall: a shot passes its own side's
                    break;
            }
            bool touching = defence is "parry" or "guard";
            int x = defence == "wall" ? 140 : touching ? 30 : 120;
            int vx = WeaponTable.Flies(kind) ? (defence == "wall" ? 2 : -2) : 0;
            int slot = Put(s, w, kind, x, owner: 1, vxCm: vx);
            Prop before = s.Props[slot];
            Step(s, held);
            Prop after = s.Props[slot];

            bool gone = after.Kind != PropKind.Shot || after.Shot != kind;
            string what = $"{kind} against {defence}";
            if (!flies || (kind == ShotKind.Hole && touching))
            {
                bool brittle = defence is "bat" or "lathi" && Weapons.Breakable(in before) && Weapons.ShotRow(in before).Break == 1;
                if (brittle) Assert.True(gone, what);
                else
                {
                    Assert.False(gone, what);
                    Assert.Equal(1, after.Owner);
                    Assert.Equal(before.Velocity.X, after.Velocity.X);
                }
                continue;
            }
            switch (defence)
            {
                case "bat":
                    Assert.False(gone, what);
                    Assert.Equal(0, after.Owner); // returned: it is the batter's
                    Assert.True(after.Velocity.X > Fix.Zero, what);
                    break;
                case "pankha":
                    Assert.False(gone, what);
                    Assert.Equal(1, after.Owner); // turned round, still its owner's
                    Assert.True(after.Velocity.X > Fix.Zero, what);
                    break;
                case "wall":
                    if (kind == ShotKind.Roll) Assert.True(after.Velocity.X < Fix.Zero, what); // turned round
                    else if (kind == ShotKind.Boomerang) Assert.Equal(1, after.Timer); // on its way back
                    else Assert.True(gone, what);
                    break;
                default:
                    Assert.True(gone, what);
                    break;
            }
        }
    }

    // ---- One frame of hits (1.7), each case both ways round ----

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void AGuardHitFromBothSidesOnOneFrameBlocksTheFrontAndTakesTheBack(int front, int back)
    {
        var xs = new int[3];
        xs[front] = 100;
        xs[back] = -100;
        GameState s = Standing(xs);
        s.Fighters[front].Facing = -1;
        s.Fighters[back].Facing = 1;
        Arm(s, 0, WeaponId.Parat);
        for (int i = 0; i < 3; i++) Step(s, Buttons.Light);
        Step(s, Buttons.Light, Buttons.Light, Buttons.Light); // both jab at once
        for (int i = 0; i < 12; i++) Step(s, Buttons.Light);
        Assert.Equal(6, s.Fighters[0].Josh); // the back jab landed
        Assert.Equal(7, s.Fighters[0].WeaponSupply); // the front one was blocked
        Assert.True(s.Fighters[front].AttackConnected);
        Assert.True(s.Fighters[back].AttackConnected);
        Assert.Equal(0, s.Fighters[front].Josh);
        Assert.Equal(0, s.Fighters[back].Josh);
        Assert.True(s.Fighters[front].Position.X > Fix.One); // pushed back by the block
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void AFirstBlastPastADodgerHitsTheFighterBehind(int dodger, int stander)
    {
        var xs = new int[3];
        xs[dodger] = 300;
        xs[stander] = 600;
        GameState s = Standing(xs);
        Arm(s, 0, WeaponId.SniperRifle);
        int startup = Weapons.Get(WeaponId.SniperRifle).Startup;
        var press = new Buttons[3];
        press[0] = Buttons.Light;
        Simulation.Advance(s, Rules, press);
        for (int f = 1; f <= startup; f++)
        {
            var b = new Buttons[3];
            if (f == startup - 8) b[dodger] = Buttons.Dodge;
            Simulation.Advance(s, Rules, b);
        }
        Assert.Equal(0, s.Fighters[dodger].Josh);
        Assert.Equal(Weapons.Get(WeaponId.SniperRifle).Damage, s.Fighters[stander].Josh);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void ASwingBreaksATrapAndHitsTheFighterBesideItOnOneFrame(int attacker, int victim)
    {
        var xs = new int[3];
        xs[attacker] = 0;
        xs[victim] = 190;
        xs[2] = -5000;
        GameState s = Standing(xs);
        s.Fighters[attacker].Facing = 1;
        s.Fighters[victim].Facing = -1;
        Arm(s, attacker, WeaponId.HockeyStick);
        int trap = Put(s, WeaponId.MouseTrap, ShotKind.Mine, 120, owner: 2, timer: 0);
        var b = new Buttons[3];
        b[attacker] = Buttons.Light;
        Simulation.Advance(s, Rules, b);
        b[attacker] = Buttons.None;
        for (int i = 0; i < Weapons.Get(WeaponId.HockeyStick).Startup; i++) Simulation.Advance(s, Rules, b);
        Assert.Equal(PropKind.None, s.Props[trap].Kind);
        Assert.Equal(9, s.Fighters[victim].Josh);
        Assert.Equal(0, s.Fighters[victim].RootFrames);
    }

    // ---- Become (3.2) ----

    private static void AssertProp(Prop expected, Prop actual)
    {
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.Weapon, actual.Weapon);
        Assert.Equal(expected.Shot, actual.Shot);
        Assert.Equal(expected.Supply, actual.Supply);
        Assert.Equal(expected.Owner, actual.Owner);
        Assert.Equal(expected.OwnerClear, actual.OwnerClear);
        Assert.Equal(expected.Position, actual.Position);
        Assert.Equal(expected.Velocity, actual.Velocity);
        Assert.Equal(expected.Grounded, actual.Grounded);
        Assert.Equal(expected.Timer, actual.Timer);
        Assert.Equal(expected.Age, actual.Age);
        Assert.Equal(expected.HitMask, actual.HitMask);
        Assert.Equal(expected.Hits, actual.Hits);
        Assert.Equal(expected.IgnoreFighter, actual.IgnoreFighter);
        Assert.Equal(expected.IgnoreTimer, actual.IgnoreTimer);
    }

    [Fact]
    public void AMatkaWallStartsWithEveryFieldFresh()
    {
        GameState s = Standing(0, 5000);
        int slot = Put(s, WeaponId.Matka, ShotKind.Bomb, 500, owner: 1, timer: 1, yCm: 0);
        s.Props[slot].Grounded = true;
        s.Props[slot].Hits = 2;         // stale values the wall must not keep
        s.Props[slot].HitMask = 5;
        s.Props[slot].IgnoreFighter = 2;
        s.Props[slot].IgnoreTimer = 3;
        s.Props[slot].Supply = 4;
        Step(s);
        AssertProp(new Prop
        {
            Kind = PropKind.Shot, Weapon = WeaponId.Matka, Shot = ShotKind.Wall, Owner = 1,
            Position = new FixVec2(Fix.FromInt(5), Fix.Zero), Grounded = true, Timer = Weapons.Get(WeaponId.Matka).ZoneLife,
        }, s.Props[slot]);
    }

    [Fact]
    public void AFlungCrateStartsWithEveryFieldFresh()
    {
        GameState s = Standing(0, 5000);
        Arm(s, 0, WeaponId.HockeyStick);
        s.Props[0] = new Prop
        {
            Kind = PropKind.Box, Weapon = WeaponId.Belan, Supply = 12, Owner = -1, Position = new FixVec2(Fix.One, Fix.Zero),
            Grounded = true, Timer = 100, IgnoreFighter = 3, IgnoreTimer = 40,
        };
        Step(s, Buttons.Light);
        Wait(s, Weapons.Get(WeaponId.HockeyStick).Startup);
        Fix vy = Weapons.ThrowVy - Weapons.ShotGravity;
        AssertProp(new Prop
        {
            Kind = PropKind.Shot, Weapon = WeaponId.Belan, Shot = ShotKind.Thrown, Supply = 12, Owner = 0, OwnerClear = true,
            Position = new FixVec2(Fix.One + Weapons.ThrowVx, vy), Velocity = new FixVec2(Weapons.ThrowVx, vy), Age = 1,
        }, s.Props[0]);
    }

    [Fact]
    public void ARestedBowlingBallAndABouncedGrenadeHaveTheirExactFields()
    {
        GameState s = Standing(0, 5000);
        WeaponRow ball = Weapons.Get(WeaponId.BowlingBall);
        int b = Put(s, WeaponId.BowlingBall, ShotKind.Roll, 300, owner: 1, vxCm: 22, timer: 7, yCm: 0);
        s.Props[b].Grounded = true;
        s.Props[b].Velocity = new FixVec2(ball.Vx, Fix.Zero);
        s.Props[b].Age = ball.Life - 1;
        s.Props[b].HitMask = 3;
        s.Props[b].Hits = 2;
        s.Props[b].IgnoreFighter = 5;
        s.Props[b].IgnoreTimer = 2;

        WeaponRow gl = Weapons.Get(WeaponId.GrenadeLauncher);
        int g = Put(s, WeaponId.GrenadeLauncher, ShotKind.Bomb, 200, owner: 1, timer: 80, yCm: 5);
        s.Props[g].Velocity = new FixVec2(gl.Vx, -Fix.FromRatio(1, 10));
        s.Props[g].Grounded = false;
        Step(s);
        AssertProp(new Prop
        {
            Kind = PropKind.Item, Weapon = WeaponId.BowlingBall, Supply = ball.Supply, Owner = -1,
            Position = new FixVec2(Fix.FromInt(3) + ball.Vx, Fix.Zero), Grounded = true,
        }, s.Props[b]);
        Fix down = -Fix.FromRatio(1, 10) - gl.Gravity;
        AssertProp(new Prop
        {
            Kind = PropKind.Shot, Weapon = WeaponId.GrenadeLauncher, Shot = ShotKind.Bomb, Owner = 1, OwnerClear = true,
            Position = new FixVec2(Fix.FromInt(2) + gl.Vx, Fix.Zero), Velocity = new FixVec2(gl.Vx, Fix.Max(-down * gl.Keep / 100, Weapons.BounceMin)),
            Timer = 79, Age = 1, Hits = 1,
        }, s.Props[g]);
    }

    // ---- Traps (2.8) ----

    [Fact]
    public void AMouseTrapIsSafeWhileArmingThenRootsTheFirstToTouchItAndIsGone()
    {
        GameState s = Standing(0, 200);
        int trap = Put(s, WeaponId.MouseTrap, ShotKind.Mine, 200, owner: 0);
        Wait(s, Weapons.Get(WeaponId.MouseTrap).Fuse - 1);
        Assert.Equal(0, s.Fighters[1].Josh);
        Assert.Equal(ShotKind.Mine, s.Props[trap].Shot);
        Step(s);
        Assert.Equal(PropKind.None, s.Props[trap].Kind);
        int damage = Weapons.Get(WeaponId.MouseTrap).Damage;
        Assert.Equal(damage, s.Fighters[1].Josh);
        Assert.Equal(45, s.Fighters[1].RootFrames);
        Assert.Equal((int)WeaponId.MouseTrap, s.Fighters[1].StunWeapon);
        Assert.Equal(damage, Simulation.WeaponStat(s, 0, WeaponId.MouseTrap, 0)); // credited to the setter
    }

    [Fact]
    public void ASwingBreaksAMouseTrapAndATrapNobodyFindsIsGoneWithoutBursting()
    {
        GameState s = Standing(0, 5000);
        Arm(s, 0, WeaponId.HockeyStick);
        int trap = Put(s, WeaponId.MouseTrap, ShotKind.Mine, 100, owner: 1, timer: 0);
        Fire(s, WeaponId.HockeyStick);
        Assert.Equal(PropKind.None, s.Props[trap].Kind);
        Assert.True(s.Fighters[0].AttackConnected);

        GameState t = Standing(0, 5000);
        int old = Put(t, WeaponId.VenomTrap, ShotKind.Mine, 300, owner: 1, timer: 0);
        t.Props[old].Age = Weapons.Get(WeaponId.VenomTrap).Life - 1;
        Hold(t, 0, Fix.FromInt(3)); // standing next to it: no burst
        t.Fighters[0].Position = new FixVec2(Fix.FromRatio(39, 10), Fix.Zero);
        Step(t);
        Assert.Equal(PropKind.None, t.Props[old].Kind);
        Assert.Equal(0, t.Fighters[0].Josh);
        Assert.Equal(0, t.Fighters[0].DotFrames);
    }

    [Fact]
    public void AJhaaduSweepRemovesTrapsMinesAndPatchesButNotACloud()
    {
        GameState s = Standing(0, 5000);
        Arm(s, 0, WeaponId.Jhaadu);
        int peel = Put(s, WeaponId.BananaPeel, ShotKind.Mine, 100, owner: 1, timer: 0);
        int mine = Put(s, WeaponId.Mines, ShotKind.Mine, -100, owner: 1, timer: 0);
        int goo = Put(s, WeaponId.ToxicGooLauncher, ShotKind.Zone, 200, owner: 1);
        int cloud = Put(s, WeaponId.StinkCloudCapsule, ShotKind.Zone, -250, owner: 1);
        Fire(s, WeaponId.Jhaadu);
        Assert.Equal(PropKind.None, s.Props[peel].Kind);
        Assert.Equal(PropKind.None, s.Props[mine].Kind); // gone, without bursting
        Assert.Equal(PropKind.None, s.Props[goo].Kind);
        Assert.Equal(ShotKind.Zone, s.Props[cloud].Shot);
        Assert.Equal(0, s.Fighters[0].Josh);
    }

    // ---- Zones (2.8) ----

    [Fact]
    public void StandingInTheGooForTwoSecondsIsExactlyEightDamageAndItStopsAfterLeaving()
    {
        GameState s = Standing(-500, 300);
        Put(s, WeaponId.ToxicGooLauncher, ShotKind.Zone, 300, owner: 0);
        for (int f = 0; f < 120; f++) Step(s);
        Assert.Equal(8, s.Fighters[1].Josh);
        Assert.Equal(1, s.Fighters[1].DotBy); // credited to the goo's owner
        Hold(s, 1, Fix.FromInt(20));
        for (int f = 0; f < 120; f++) Step(s);
        Assert.Equal(0, s.Fighters[1].DotFrames);
        Assert.Equal(8 + 6, s.Fighters[1].Josh); // 90 frames of poison after leaving, a tick every 15
        for (int f = 0; f < 60; f++) Step(s);
        Assert.Equal(14, s.Fighters[1].Josh);
    }

    [Fact]
    public void ThePuddleSlipsARunnerAndNotSomeoneStandingInIt()
    {
        GameState s = Standing(-5000, 0, 300);
        Put(s, WeaponId.Lota, ShotKind.Zone, 300, owner: 0);
        bool slipped = false;
        for (int f = 0; f < 40; f++)
        {
            Step(s, Buttons.None, s.Fighters[1].Action == FighterAction.Idle ? Buttons.Right : Buttons.None);
            slipped |= s.Fighters[1].StunWeapon == (int)WeaponId.Lota && s.Fighters[1].Action == FighterAction.Stunned;
            Assert.Equal(0, s.Fighters[2].StunWeapon);
            Assert.Equal(FighterAction.Idle, s.Fighters[2].Action);
        }
        Assert.True(slipped);
    }

    [Fact]
    public void AGluePatchKeepsAFighterSlowWhileTheyStandInIt()
    {
        GameState s = Standing(-5000, 300);
        Put(s, WeaponId.GlueGun, ShotKind.Zone, 300, owner: 0);
        for (int f = 0; f < 100; f++)
        {
            Step(s);
            Assert.Equal(Weapons.Get(WeaponId.GlueGun).EffectN, s.Fighters[1].SlowFrames);
        }
        Hold(s, 1, Fix.FromInt(20));
        Wait(s, 46);
        Assert.Equal(0, s.Fighters[1].SlowFrames);
    }

    // ---- Walls (2.8) ----

    [Fact]
    public void AFighterWalkingAtAWallDoesNotPassButAJumpDoes()
    {
        GameState s = Standing(0, 5000);
        Put(s, WeaponId.Matka, ShotKind.Wall, 200, owner: 1);
        for (int f = 0; f < 60; f++)
        {
            Step(s, Buttons.Right);
            Assert.True(s.Fighters[0].Position.X <= Cm(101), "walked into the wall");
        }

        GameState t = Standing(0, 5000);
        Put(t, WeaponId.Matka, ShotKind.Wall, 200, owner: 1);
        Step(t, Buttons.Right | Buttons.Jump);
        for (int f = 0; f < 70; f++) Step(t, Buttons.Right);
        Assert.True(t.Fighters[0].Position.X > Fix.FromInt(3));
    }

    [Fact]
    public void AWallStopsABulletAndASwingOrABlastDoesNotReachPastIt()
    {
        GameState s = Standing(-5000, 310, 5000);
        Put(s, WeaponId.Matka, ShotKind.Wall, 200, owner: 2);
        Put(s, WeaponId.Uzi, ShotKind.Bullet, 100, owner: 0, vxCm: 70);
        Wait(s, 5);
        Assert.Equal(0, Count(s, ShotKind.Bullet));
        Assert.Equal(0, s.Fighters[1].Josh);

        // A shot passes its own side's wall: cover to shoot from.
        GameState own = Standing(-5000, 310);
        Put(own, WeaponId.Matka, ShotKind.Wall, 200, owner: 0);
        int b = Put(own, WeaponId.Uzi, ShotKind.Bullet, 100, owner: 0, vxCm: 70);
        own.Props[b].Owner = 0;
        Wait(own, 5);
        Assert.Equal(2, own.Fighters[1].Josh);

        foreach (WeaponId w in new[] { WeaponId.Spear, WeaponId.Shotgun })
        {
            foreach (bool wall in new[] { true, false })
            {
                GameState t = Standing(90, 310, -5000);
                Arm(t, 0, w);
                int slot = wall ? Put(t, WeaponId.Matka, ShotKind.Wall, 200, owner: 2) : -1;
                Fire(t, w);
                Wait(t, 2);
                if (wall)
                {
                    Assert.Equal(0, t.Fighters[1].Josh);
                    Assert.Equal(1, t.Props[slot].IgnoreFighter); // the wall took the swing
                }
                else Assert.True(t.Fighters[1].Josh > 0, $"{w} reaches without a wall");
            }
        }
    }

    [Theory]
    [InlineData(WeaponId.Belan, 3)]
    [InlineData(WeaponId.Sledgehammer, 1)]
    public void ThreeSwingsBreakAWallAndASledgehammerBreaksItInOne(WeaponId w, int swings)
    {
        GameState s = Standing(90, 5000);
        Arm(s, 0, w, supply: 10);
        int wall = Put(s, WeaponId.Matka, ShotKind.Wall, 200, owner: 1);
        WeaponRow row = Weapons.Get(w);
        for (int k = 1; k <= swings; k++)
        {
            Assert.Equal(ShotKind.Wall, s.Props[wall].Shot);
            Step(s, Buttons.Light);
            Wait(s, row.Startup + row.Active + row.Recovery);
        }
        Assert.Equal(PropKind.None, s.Props[wall].Kind);
    }

    // ---- Pads (2.8) ----

    [Fact]
    public void AThrownCharpaiLiesAsAPadAndBounces()
    {
        GameState s = Standing(0, 5000);
        Arm(s, 0, WeaponId.Charpai);
        Step(s, Buttons.DropWeapon);
        int pad = -1;
        for (int f = 0; f < 80 && pad < 0; f++)
        {
            Step(s);
            for (int i = 0; i < s.Props.Length; i++)
                if (s.Props[i].Kind == PropKind.Shot && s.Props[i].Shot == ShotKind.Pad) pad = i;
        }
        Assert.True(pad >= 0, "the charpai never opened");
        Assert.Equal(WeaponId.ThrownCharpai, s.Props[pad].Weapon);
        Assert.Equal(WeaponId.None, s.Fighters[0].Weapon);
        Assert.True(s.Props[pad].Timer > 300);
        Hold(s, 1, s.Props[pad].Position.X);
        Step(s);
        Assert.Equal(Weapons.Get(WeaponId.ThrownCharpai).PadLaunch, s.Fighters[1].Velocity.Y);
    }

    // ---- The trick weapons ----

    [Fact]
    public void TheChimtasTipDisarmsAndAClosePinchDoesNot()
    {
        GameState s = Standing(0, 195);
        Arm(s, 1, WeaponId.Belan);
        Arm(s, 0, WeaponId.Chimta);
        Fire(s, WeaponId.Chimta);
        Assert.Equal(Sweet(Weapons.Get(WeaponId.Chimta).Damage), s.Fighters[1].Josh);
        Assert.Equal(WeaponId.None, s.Fighters[1].Weapon);
        int item = Array.FindIndex(s.Props, p => p.Kind == PropKind.Item && p.Weapon == WeaponId.Belan);
        Assert.True(item >= 0);
        Assert.Equal(12, s.Props[item].Supply);
        Fix at = s.Props[item].Position.X;
        for (int f = 0; f < Weapons.NoSwapBackFrames - 2; f++)
        {
            Step(s);
            Hold(s, 1, at);
            Assert.Equal(WeaponId.None, s.Fighters[1].Weapon); // not for a second
        }
        Wait(s, 4);
        Hold(s, 1, at);
        Wait(s, 2);
        Assert.Equal(WeaponId.Belan, s.Fighters[1].Weapon);

        GameState t = Standing(0, 100);
        Arm(t, 1, WeaponId.Belan);
        Arm(t, 0, WeaponId.Chimta);
        Fire(t, WeaponId.Chimta);
        Assert.Equal(Weapons.Get(WeaponId.Chimta).Damage, t.Fighters[1].Josh);
        Assert.Equal(WeaponId.Belan, t.Fighters[1].Weapon);
    }

    [Fact]
    public void TheRakeLaunchesTowardsTheRakerAndDragsACrateIn()
    {
        GameState s = Standing(0, 120);
        Arm(s, 0, WeaponId.Rake);
        s.Props[0] = new Prop
        {
            Kind = PropKind.Box, Weapon = WeaponId.Uzi, Supply = 30, Owner = -1, Position = new FixVec2(Fix.FromRatio(21, 10), Fix.Zero),
            Grounded = true, IgnoreFighter = 2, IgnoreTimer = 600, // the raked fighter doesn't take it
        };
        Fire(s, WeaponId.Rake);
        Assert.Equal(8, s.Fighters[1].Josh);
        Assert.True(s.Fighters[1].Velocity.X < Fix.Zero, "launched towards the raker");
        Assert.Equal(Weapons.ScoopPullTo, s.Props[0].Position.X);
    }

    [Fact]
    public void TheHockeyStickFlingsACrateThatHitsAndLiesThereAndOneFlungOffComesBack()
    {
        // It flies, hits, and lies there with its supply (taken by whoever it lay by).
        GameState s = Standing(0, 300);
        Arm(s, 0, WeaponId.HockeyStick);
        s.Props[0] = new Prop { Kind = PropKind.Box, Weapon = WeaponId.Belan, Supply = 12, Owner = -1, Position = new FixVec2(Fix.One, Fix.Zero), Grounded = true };
        Fire(s, WeaponId.HockeyStick);
        Assert.Equal(ShotKind.Thrown, s.Props[0].Shot);
        Wait(s, 20);
        Assert.Equal(Weapons.Get(WeaponId.Belan).Throw, s.Fighters[1].Josh);
        Assert.Equal(WeaponId.Belan, s.Fighters[1].Weapon);
        Assert.Equal(12, s.Fighters[1].WeaponSupply);

        // With nobody in the way it lands and lies there as a loose weapon.
        GameState t = Standing(0, 5000);
        Arm(t, 0, WeaponId.HockeyStick);
        t.Props[0] = new Prop { Kind = PropKind.Box, Weapon = WeaponId.Belan, Supply = 12, Owner = -1, Position = new FixVec2(Fix.One, Fix.Zero), Grounded = true };
        Fire(t, WeaponId.HockeyStick);
        Wait(t, 40);
        Assert.Equal(PropKind.Item, t.Props[0].Kind);
        Assert.Equal(12, t.Props[0].Supply);

        // Flung off the stage, it comes back in a box.
        var rules = new MatchRules { Stage = Arena, Weapons = true, Guts = false };
        GameState u = Standing(rules, 1450, -1000);
        Arm(u, 0, WeaponId.HockeyStick);
        u.Props[0] = new Prop { Kind = PropKind.Box, Weapon = WeaponId.Belan, Supply = 12, Owner = -1, Position = new FixVec2(Fix.FromRatio(159, 10), Fix.Zero), Grounded = true };
        Step(rules, u, Buttons.Light);
        for (int f = 0; f < 100; f++) Step(rules, u);
        Assert.Contains(u.Props, p => p.Kind == PropKind.Box && p.Weapon == WeaponId.Belan && p.Supply == 12);
    }

    [Fact]
    public void AThrownGasCylinderBurstsOnWhoeverItTouches()
    {
        GameState s = Standing(0, 300);
        Arm(s, 0, WeaponId.GasCylinder);
        Step(s, Buttons.DropWeapon);
        Wait(s, 30);
        Assert.Equal(Sweet(16), s.Fighters[1].Josh); // a direct hit
        Assert.Equal(WeaponId.GasCylinder, s.Fighters[1].LastHitWeapon);
        Assert.Equal(WeaponId.None, s.Fighters[0].Weapon);
    }

    [Fact]
    public void AThrownSpearGoesThroughTwo()
    {
        GameState s = Standing(0, 200, 350);
        Arm(s, 0, WeaponId.Spear);
        Step(s, Buttons.DropWeapon);
        Wait(s, 25);
        Assert.Equal(13, s.Fighters[1].Josh);
        Assert.Equal(13, s.Fighters[2].Josh);
        Assert.Equal(WeaponId.Spear, s.Fighters[2].LastHitWeapon);
        Assert.Equal(26, Simulation.WeaponStat(s, 0, WeaponId.Spear, 0));
    }

    [Fact]
    public void TheThunderHammersWaveHitsBeyondTheHammerWithItsOwnDamage()
    {
        GameState s = Standing(0, 400);
        Arm(s, 0, WeaponId.ThunderHammer);
        Fire(s, WeaponId.ThunderHammer);
        Assert.Equal(2, s.Fighters[0].WeaponSupply); // spent per use, hit or miss
        Assert.Equal(1, Count(s, ShotKind.Roll));
        Wait(s, 20);
        Assert.Equal(9, s.Fighters[1].Josh);
        Assert.Equal(WeaponId.ThunderHammer, s.Fighters[1].LastHitWeapon);
    }

    [Fact]
    public void TheJugaadGauntletPullsThenPunchesThenShootsAllCreditedToIt()
    {
        GameState s = Standing(0, 300);
        Arm(s, 0, WeaponId.JugaadGauntlet);
        MoveId[] order = { WeaponTable.Rows[101].Move, WeaponTable.Rows[102].Move, WeaponTable.Rows[103].Move };
        for (int k = 0; k < 6; k++)
        {
            Hold(s, 1, Fix.FromInt(k % 3 == 0 ? 3 : k % 3 == 1 ? 1 : 5));
            s.Fighters[1].Action = FighterAction.Idle;
            s.Fighters[1].HitStop = 0;
            Step(s, Buttons.Light);
            Assert.Equal(order[k % 3], s.Fighters[0].Move);
            if (k % 3 == 2)
            {
                Wait(s, WeaponTable.Rows[103].Startup);
                Assert.Contains(s.Props, p => p.Kind == PropKind.Shot && p.Weapon == WeaponId.GauntletBolt);
            }
            while (s.Fighters[0].Action == FighterAction.Attack) Step(s);
            Assert.Equal(9 - k - 1, s.Fighters[0].WeaponSupply); // one a press, hit or miss
        }
        Assert.Equal(WeaponId.JugaadGauntlet, s.Fighters[1].LastHitWeapon);
        Assert.True(Simulation.WeaponStat(s, 0, WeaponId.JugaadGauntlet, 0) >= 2 * (3 + 8));
        Assert.Equal(0, Simulation.WeaponStat(s, 0, WeaponId.GauntletPull, 0));
    }

    [Fact]
    public void TheGauntletsPullReelsTheTargetIn()
    {
        GameState s = Standing(0, 300);
        Arm(s, 0, WeaponId.JugaadGauntlet);
        Step(s, Buttons.Light);
        Wait(s, WeaponTable.Rows[101].Startup);
        Assert.Equal(3, s.Fighters[1].Josh);
        Assert.True(s.Fighters[1].Velocity.X < Fix.Zero);
    }

    [Fact]
    public void AStinkCloudHidesAFighterFromAMissileAndTheCpu()
    {
        GameState s = Standing(0, 500);
        Put(s, WeaponId.StinkCloudCapsule, ShotKind.Zone, 500, owner: 0);
        Step(s);
        Assert.True(Simulation.IsHidden(s, Rules, 1));
        Assert.False(Simulation.IsHidden(s, Rules, 0));
        Assert.True(s.Fighters[1].SlowFrames >= 10); // coughing

        int missile = Put(s, WeaponId.HomingMissile, ShotKind.Missile, 100, owner: 0, vxCm: 20, yCm: 300);
        Step(s);
        Assert.Equal(Fix.Zero, s.Props[missile].Velocity.Y); // nobody it can see
        Assert.Equal(Buttons.None, SparringBot.Think(s, Rules, 0, CpuLevel.Hard)); // nor can the CPU, from 5 m

        Hold(s, 1, Fix.FromInt(12));
        Step(s);
        Assert.False(Simulation.IsHidden(s, Rules, 1));
        Step(s);
        Assert.NotEqual(Fix.Zero, s.Props[missile].Velocity.Y); // seen again: it steers

        GameState t = Standing(0, 150);
        Put(t, WeaponId.StinkCloudCapsule, ShotKind.Zone, 150, owner: 0);
        Step(t);
        Assert.True(Simulation.IsHidden(t, Rules, 1));
        Assert.NotEqual(Buttons.None, SparringBot.Think(t, Rules, 0, CpuLevel.Hard) & (Buttons.Light | Buttons.Right)); // close enough to see
    }

    [Fact]
    public void TheStinkCapsuleOpensIntoACloudWhereItLands()
    {
        GameState s = Standing(0, 5000);
        Arm(s, 0, WeaponId.StinkCloudCapsule);
        Fire(s, WeaponId.StinkCloudCapsule);
        Wait(s, 70);
        int cloud = Array.FindIndex(s.Props, p => p.Kind == PropKind.Shot && p.Shot == ShotKind.Zone);
        Assert.True(cloud >= 0);
        Assert.True(Weapons.ShotRow(in s.Props[cloud]).Hides);
    }

    [Fact]
    public void AMatkaBurstsOnItsTargetAndLeavesAWall()
    {
        GameState s = Standing(0, 650);
        Arm(s, 0, WeaponId.Matka);
        Fire(s, WeaponId.Matka);
        Wait(s, 60);
        Assert.Equal(Sweet(10), s.Fighters[1].Josh);
        Assert.Equal(1, Count(s, ShotKind.Wall));
    }

    [Fact]
    public void ALotaBonksAndLeavesAPuddleThatSlips()
    {
        GameState s = Standing(0, 600);
        Arm(s, 0, WeaponId.Lota);
        Fire(s, WeaponId.Lota);
        bool slipped = false;
        for (int f = 0; f < 60; f++)
        {
            Step(s);
            slipped |= s.Fighters[1].StunWeapon == (int)WeaponId.Lota;
        }
        Assert.Equal(5, s.Fighters[1].Josh);
        Assert.True(slipped);
        Assert.Equal(1, Count(s, ShotKind.Zone));
    }

    [Fact]
    public void ABananaPeelSlipsWhoeverStepsOnItAndOffTheEdgeIfItIsNear()
    {
        GameState s = Standing(0, 5000);
        int peel = Put(s, WeaponId.BananaPeel, ShotKind.Mine, 100, owner: 1, timer: 0);
        for (int f = 0; f < 10 && s.Props[peel].Kind != PropKind.None; f++) Step(s, Buttons.Right);
        Assert.Equal(PropKind.None, s.Props[peel].Kind);
        Assert.Equal(FighterAction.Stunned, s.Fighters[0].Action);
        Assert.Equal(Weapons.SlipSpeed, s.Fighters[0].Velocity.X);
        Assert.Equal(1, s.Fighters[0].Josh);

        var rules = new MatchRules { Stage = Arena, Weapons = true, Guts = false };
        GameState t = Standing(rules, 1450, -1000);
        Put(t, WeaponId.BananaPeel, ShotKind.Mine, 1500, owner: 1, timer: 0);
        bool off = false;
        for (int f = 0; f < 40 && !off; f++)
        {
            Step(rules, t, Buttons.Right);
            off = !t.Fighters[0].Grounded && t.Fighters[0].Position.X > Fix.FromInt(16);
        }
        Assert.True(off, "slid off the edge");
        Assert.Equal(0, t.Fighters[0].StunWeapon); // and can act: the hold ends off the ground
        Assert.NotEqual(FighterAction.Stunned, t.Fighters[0].Action);
    }

    [Fact]
    public void AGlueBlobSlowsWhoeverItHitsAndLeavesAPatch()
    {
        GameState s = Standing(0, 300);
        Arm(s, 0, WeaponId.GlueGun);
        Fire(s, WeaponId.GlueGun);
        bool slowed = false;
        for (int f = 0; f < 20; f++)
        {
            Step(s);
            slowed |= s.Fighters[1].SlowFrames > 0;
        }
        Assert.True(slowed);
        Assert.Equal(1, s.Fighters[1].Josh);
        Assert.Equal(1, Count(s, ShotKind.Zone));
    }

    [Fact]
    public void DarkEnergyIsSlowPassesItsCasterFadesAfterFourAndAnySwingPopsIt()
    {
        WeaponRow orb = Weapons.Get(WeaponId.DarkEnergy);
        Assert.True(orb.Vx < Rules.Tuning.RunSpeed);

        GameState s = Standing(0, 200, 300, 400, 500, 600);
        int slot = Put(s, WeaponId.DarkEnergy, ShotKind.Arrow, 0, owner: 0, vxCm: 12);
        s.Props[slot].Velocity = new FixVec2(orb.Vx, Fix.Zero);
        for (int f = 0; f < 70; f++) Step(s);
        Assert.Equal(0, s.Fighters[0].Josh); // never its caster
        for (int i = 1; i <= 4; i++) Assert.Equal(12, s.Fighters[i].Josh);
        Assert.Equal(0, s.Fighters[5].Josh); // gone after its fourth
        Assert.Equal(0, Count(s, ShotKind.Arrow));

        GameState t = Standing(0, 5000);
        Arm(t, 0, WeaponId.Belan);
        int pop = Put(t, WeaponId.DarkEnergy, ShotKind.Arrow, 100, owner: 1, vxCm: -1);
        Fire(t, WeaponId.Belan);
        Assert.Equal(PropKind.None, t.Props[pop].Kind);
        Assert.Equal(0, t.Fighters[0].Josh);
    }

    // ---- The CPU and the crates ----

    [Fact]
    public void TheCpuNeverHoldsTheParatsGuard()
    {
        GameState s = Standing(0, 100);
        Arm(s, 0, WeaponId.Parat);
        Step(s, Buttons.Light);
        Step(s, Buttons.Light);
        Assert.Equal(FighterAction.Attack, s.Fighters[0].Action);
        Assert.Equal(Buttons.None, SparringBot.Think(s, Rules, 0, CpuLevel.Hard) & (Buttons.Light | Buttons.Heavy));
    }

    [Fact]
    public void TheTwentyFourRowsOfTheBatchAreInTheCrates()
    {
        WeaponId[] batch =
        {
            WeaponId.MouseTrap, WeaponId.Thali, WeaponId.Tawa, WeaponId.Chimta, WeaponId.Jhaadu, WeaponId.Lota,
            WeaponId.Parat, WeaponId.Matka, WeaponId.GasCylinder, WeaponId.Charpai, WeaponId.HockeyStick, WeaponId.Rake,
            WeaponId.Lathi, WeaponId.Spear, WeaponId.Cutlass, WeaponId.Wrench, WeaponId.ToxicGooLauncher,
            WeaponId.StinkCloudCapsule, WeaponId.VenomTrap, WeaponId.BananaPeel, WeaponId.GlueGun, WeaponId.DarkEnergy,
            WeaponId.JugaadGauntlet, WeaponId.ThunderHammer,
        };
        Assert.Equal(24, batch.Distinct().Count());
        Assert.All(batch, id => Assert.Contains(id, Weapons.CrateList));
        Assert.True(Weapons.CrateList.Length >= 92);
        for (int id = 101; id <= 107; id++)
        {
            Assert.True(WeaponTable.Rows[id].Built);
            Assert.DoesNotContain((WeaponId)id, Weapons.CrateList);
        }
    }
}
