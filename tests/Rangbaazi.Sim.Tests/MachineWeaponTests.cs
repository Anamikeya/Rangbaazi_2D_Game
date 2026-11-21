using Rangbaazi.Sim.Diagnostics;
using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim.Tests;

/// <summary>
/// Batch 5 of combat spec v0.4: machines and position tricks (section 2.9). The Chicken's rehits,
/// the Anvil's drop, the Sonic Speaker's pulse, the Portal Disc's gates, the Decoy Crate and the
/// Crate Detonator, the Teleporter's blink, the Swap Ray, the safe spot (section 1.6), and what the
/// CPU does with them.
/// </summary>
public class MachineWeaponTests
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

    /// <summary>A 16 m floor inside an arena's blast lines: 4 m past its ledges is still in the match.</summary>
    private static readonly StageDefinition Ledges = new()
    {
        Id = "test-ledges",
        Platforms = new[] { new FixRect(Fix.FromInt(-8), Fix.FromInt(-2), Fix.FromInt(8), Fix.Zero) },
        SpawnPoints = new[] { new FixVec2(Fix.FromInt(-2), Fix.Zero), new FixVec2(Fix.FromInt(2), Fix.Zero) },
        BlastZone = Arena.BlastZone,
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

    private static void Wait(GameState s, int frames) => Wait(Rules, s, frames);

    private static void Wait(MatchRules rules, GameState s, int frames)
    {
        for (int i = 0; i < frames; i++) Step(rules, s);
    }

    /// <summary>Fighter 0 presses attack, and the frames pass up to and including the move's startup frame.</summary>
    private static void Fire(GameState s, WeaponId w) => Fire(Rules, s, w);

    private static void Fire(MatchRules rules, GameState s, WeaponId w, Buttons held = Buttons.None)
    {
        Step(rules, s, Buttons.Light | held);
        for (int i = 0; i < Weapons.Get(w).Startup; i++) Step(rules, s, held);
    }

    /// <summary>Puts fighter <paramref name="i"/> back where it stood, standing still and free to act.</summary>
    private static void Hold(GameState s, int i, Fix x, Fix y = default)
    {
        ref Fighter f = ref s.Fighters[i];
        f.Position = new FixVec2(x, y);
        f.Velocity = FixVec2.Zero;
        f.Grounded = true;
        if (f.Action == FighterAction.Hitstun) f.Action = FighterAction.Idle;
    }

    /// <summary>A prop of this flight in the last free slot from the end, lying on the floor, with its row's timer.</summary>
    private static int Put(GameState s, WeaponId w, ShotKind shot, int xCm, int owner, int yCm = 0, int supply = 0, int timer = -1)
    {
        int slot = s.Props.Length - 1;
        while (s.Props[slot].Kind != PropKind.None) slot--;
        s.Props[slot] = new Prop
        {
            Kind = PropKind.Shot, Shot = shot, Weapon = w, Owner = owner, OwnerClear = true, Supply = supply,
            Position = new FixVec2(Cm(xCm), Cm(yCm)), Grounded = yCm == 0,
            Timer = timer >= 0 ? timer : Weapons.StartTimer(Weapons.Get(w)),
        };
        return slot;
    }

    private static int SlotOf(GameState s, ShotKind shot)
    {
        for (int i = 0; i < s.Props.Length; i++)
            if (s.Props[i].Kind == PropKind.Shot && s.Props[i].Shot == shot) return i;
        return -1;
    }

    private static int Count(GameState s, ShotKind shot) => s.Props.Count(p => p.Kind == PropKind.Shot && p.Shot == shot);

    // ---- The Chicken ----

    /// <summary>It runs along the ground pecking everyone it touches, each of them again every 30 frames, and is gone at the end of its run.</summary>
    [Fact]
    public void TheChickenPecksTwiceInASecondAndIsGoneAtTheEnd()
    {
        GameState s = Standing(0, 5000);
        Arm(s, 0, WeaponId.Chicken);
        Fire(s, WeaponId.Chicken);
        int slot = SlotOf(s, ShotKind.Roll);
        Assert.True(slot >= 0);
        Assert.Equal(WeaponId.Chicken, s.Props[slot].Weapon);
        Assert.Equal(1, s.Fighters[0].WeaponSupply);

        // Fighter 1 is kept on the chicken for a second: two pecks, one every 30 frames.
        while (s.Props[slot].Age < 59)
        {
            Hold(s, 1, s.Props[slot].Position.X + s.Props[slot].Velocity.X);
            s.Fighters[1].HitStop = 0;
            Step(s);
        }
        Assert.Equal(2, s.Fighters[1].HitCount);
        Assert.Equal(2 * Weapons.Get(WeaponId.Chicken).Damage, s.Fighters[1].Josh);

        Hold(s, 1, Fix.FromInt(-50));
        Wait(s, Weapons.Get(WeaponId.Chicken).Life);
        Assert.Equal(PropKind.None, s.Props[slot].Kind); // gone: it doesn't lie there as a loose weapon
    }

    /// <summary>One swing breaks it, a bat's too: a breakable shot in a bat's swing is popped, not returned.</summary>
    [Fact]
    public void ASwingPopsTheChicken()
    {
        GameState s = Standing(0, 300);
        Arm(s, 1, WeaponId.BaseballBat);
        int slot = Put(s, WeaponId.Chicken, ShotKind.Roll, 180, owner: 0);
        s.Props[slot].Timer = 0;
        Step(s, Buttons.None, Buttons.Light);
        for (int i = 0; i < Weapons.Get(WeaponId.BaseballBat).Startup && s.Props[slot].Kind != PropKind.None; i++)
        {
            s.Props[slot].Position = new FixVec2(Cm(180), Fix.Zero);
            Step(s);
        }
        Assert.Equal(PropKind.None, s.Props[slot].Kind);
    }

    // ---- The Anvil ----

    /// <summary>It hangs 9 m above its mark, over the nearest opponent in front, for 45 frames, then falls through everyone under it, popping them upwards, and is gone when it lands.</summary>
    [Fact]
    public void TheAnvilHangsThenFallsThroughEveryoneUnderItsMark()
    {
        GameState s = Standing(0, 380, 420);
        Arm(s, 0, WeaponId.Anvil);
        Fire(s, WeaponId.Anvil);
        int slot = SlotOf(s, ShotKind.Drop);
        Assert.True(slot >= 0);
        Assert.Equal(new FixVec2(Cm(380), Fix.FromInt(9)), s.Props[slot].Position); // over fighter 1, who stands still
        Assert.Equal(44, s.Props[slot].Timer); // the frame it appeared was its first of 45
        Assert.Equal(Weapons.Get(WeaponId.Anvil).Supply - 1, s.Fighters[0].WeaponSupply);

        for (int i = 0; i < 44; i++) Step(s);
        Assert.Equal(new FixVec2(Cm(380), Fix.FromInt(9)), s.Props[slot].Position); // still hanging
        Assert.Equal(0, s.Props[slot].Timer);
        Assert.Equal(0, s.Fighters[1].Josh);

        bool[] hit = new bool[3];
        for (int i = 0; i < 120 && s.Props[slot].Kind != PropKind.None; i++)
        {
            Step(s);
            for (int d = 1; d <= 2; d++)
            {
                if (hit[d] || s.Fighters[d].Josh == 0) continue;
                hit[d] = true;
                Assert.Equal(Weapons.Get(WeaponId.Anvil).Damage, s.Fighters[d].Josh);
                Fighter f = s.Fighters[d];
                Assert.True(f.Velocity.Y > Fix.Abs(f.Velocity.X) * 5, "popped upwards, at 80 degrees");
            }
        }
        Assert.True(hit[1] && hit[2], "both under the mark were hit");
        Assert.Equal(0, s.Fighters[0].Josh);
        Assert.Equal(PropKind.None, s.Props[slot].Kind); // gone on landing
    }

    /// <summary>
    /// The anvil's mark: over the nearest opponent in front within 8 m, where they will be 40
    /// frames on at their speed, kept 2 to 6 m ahead; nobody to aim at (only someone behind, a
    /// teammate, someone too far or hidden in a cloud): 4 m ahead.
    /// </summary>
    [Theory]
    [InlineData(new[] { 0, 300 }, new[] { 0, 0 }, 300)]       // standing 3 m ahead
    [InlineData(new[] { 0, 300, 500 }, new[] { 0, 0, 0 }, 300)] // the nearest of two
    [InlineData(new[] { 0, 300 }, new[] { 0, 5 }, 500)]       // walking away at 0.05 m a frame: 2 m further on
    [InlineData(new[] { 0, 300 }, new[] { 0, -5 }, 200)]      // walking in: no nearer than 2 m
    [InlineData(new[] { 0, 100 }, new[] { 0, 0 }, 200)]       // at fist range: 2 m
    [InlineData(new[] { 0, 750 }, new[] { 0, 0 }, 600)]       // far: no further than 6 m
    [InlineData(new[] { 0, 900 }, new[] { 0, 0 }, 400)]       // beyond 8 m: nobody to aim at
    [InlineData(new[] { 0, -300 }, new[] { 0, 0 }, 400)]      // behind
    public void TheAnvilMarksTheNearestOpponentInFrontWhereTheyWillBe(int[] xCm, int[] vxCm, int markCm)
    {
        GameState s = Standing(xCm);
        for (int i = 0; i < xCm.Length; i++) s.Fighters[i].Velocity = new FixVec2(Cm(vxCm[i]), Fix.Zero);
        FixVec2 mark = Simulation.DropSpot(s, Rules, 0);
        Assert.True(Fix.Abs(mark.X - Cm(markCm)) < Cm(1), $"marked {mark.X}, not {markCm} cm"); // a speed in cm is not exact in 16.16
        Assert.Equal(Fix.FromInt(9), mark.Y);
    }

    [Fact]
    public void TheAnvilDoesntMarkATeammateOrSomeoneHiddenInACloud()
    {
        var teams = new MatchRules { Stage = TestStages.Wide, Weapons = true, Guts = false, Teams = new[] { 1, 1, 2 } };
        GameState s = Standing(teams, 0, 300, 500);
        Assert.Equal(Cm(500), Simulation.DropSpot(s, teams, 0).X); // past the teammate, the opponent
        Put(s, WeaponId.StinkCloudCapsule, ShotKind.Zone, 500, owner: 1);
        Assert.Equal(Fix.FromInt(4), Simulation.DropSpot(s, teams, 0).X); // hidden: nobody to aim at
    }

    /// <summary>On the highest platform of the tallest arena the anvil still appears inside the blast zone, and falls.</summary>
    [Fact]
    public void TheAnvilOnTheHighestPlatformOfTheTallestStageStillAppearsAndFalls()
    {
        StageDefinition tallest = ArenaStages.All.OrderByDescending(a => a.BlastZone.MaxY - a.BlastZone.MinY).First();
        FixRect top = tallest.Platforms.OrderByDescending(p => p.MaxY).First();
        var rules = new MatchRules { Stage = tallest, Weapons = true };
        GameState s = Simulation.CreateInitialState(rules, 2, 1);
        s.PropTimer = 1_000_000;
        ref Fighter f = ref s.Fighters[0];
        f.Position = new FixVec2((top.MinX + top.MaxX) / 2, top.MaxY);
        f.Velocity = FixVec2.Zero;
        f.Grounded = true;
        f.Facing = 1;
        s.Fighters[1].Position = new FixVec2(tallest.BlastZone.MinX + Fix.FromInt(3), top.MaxY);
        Arm(s, 0, WeaponId.Anvil);
        Fire(rules, s, WeaponId.Anvil);
        int slot = SlotOf(s, ShotKind.Drop);
        Assert.True(slot >= 0, "it appeared");
        FixVec2 at = s.Props[slot].Position;
        Assert.True(at.Y <= tallest.BlastZone.MaxY - Fix.FromInt(2));
        Assert.True(at.X <= tallest.BlastZone.MaxX - Fix.FromInt(2));
        Wait(rules, s, 46);
        Assert.Equal(PropKind.Shot, s.Props[slot].Kind);
        Wait(rules, s, 5);
        Assert.True(s.Props[slot].Kind == PropKind.None || s.Props[slot].Position.Y < at.Y, "it falls");
    }

    // ---- The Sonic Speaker ----

    /// <summary>It pulses on the match clock (every frame that is a multiple of 45), in front only, never at its owner.</summary>
    [Fact]
    public void TheSpeakerPulsesOnTheMatchClockInFrontOnly()
    {
        GameState s = Standing(0, 300, -300);
        Arm(s, 0, WeaponId.SonicSpeaker);
        Fire(s, WeaponId.SonicSpeaker);
        int slot = SlotOf(s, ShotKind.Speaker);
        Assert.True(slot >= 0);
        Assert.Equal(1, s.Props[slot].Supply); // it faces the way its owner faced
        Assert.Equal(Weapons.Get(WeaponId.SonicSpeaker).Life - 1, s.Props[slot].Timer);

        int pulses = 0;
        for (int i = 0; i < 200; i++)
        {
            int frame = s.Frame;
            int before = s.Fighters[1].HitCount;
            Step(s);
            if (s.Fighters[1].HitCount > before)
            {
                Assert.Equal(0, frame % 45);
                pulses++;
            }
            Hold(s, 1, Cm(300));
            Hold(s, 2, Cm(-300));
            Hold(s, 0, Fix.Zero);
        }
        Assert.True(pulses >= 4, $"{pulses} pulses in 200 frames");
        Assert.Equal(0, s.Fighters[2].HitCount); // behind it
        Assert.Equal(0, s.Fighters[0].HitCount); // its owner
        Assert.Equal(WeaponId.SonicSpeaker, s.Fighters[1].LastHitWeapon);
    }

    [Fact]
    public void ASecondSpeakerRemovesTheFirstAndTwoSwingsBreakOne()
    {
        GameState s = Standing(0, 5000);
        Arm(s, 0, WeaponId.SonicSpeaker);
        Fire(s, WeaponId.SonicSpeaker);
        int first = SlotOf(s, ShotKind.Speaker);
        Wait(s, 30);
        Hold(s, 0, Fix.FromInt(-3));
        Fire(s, WeaponId.SonicSpeaker);
        Assert.Equal(1, Count(s, ShotKind.Speaker)); // one per owner
        int second = SlotOf(s, ShotKind.Speaker);
        Assert.True(s.Props[second].Position.X < Fix.Zero, "the new one stays, the old one went");
        Assert.Equal(first == second ? PropKind.Shot : PropKind.None, s.Props[first].Kind);

        // Two swings break it: fighter 1 jabs it twice.
        GameState t = Standing(0, 200);
        int speaker = Put(t, WeaponId.SonicSpeaker, ShotKind.Speaker, 130, owner: 0, supply: -1); // facing away from the jabber
        Step(t, Buttons.None, Buttons.Light);
        Wait(t, 25);
        Assert.Equal(1, t.Props[speaker].IgnoreFighter);
        Assert.Equal(PropKind.Shot, t.Props[speaker].Kind);
        Step(t, Buttons.None, Buttons.Light);
        Wait(t, 25);
        Assert.Equal(PropKind.None, t.Props[speaker].Kind);
    }

    /// <summary>A fighter takes one pulse a frame at most, from the speaker in the lowest slot; nobody is shoved twice within 45 frames.</summary>
    [Fact]
    public void TwoSpeakersNeverShoveOneFighterTwiceWithinFortyFiveFrames()
    {
        GameState s = Standing(-200, 200, 600);
        int a = Put(s, WeaponId.SonicSpeaker, ShotKind.Speaker, -100, owner: 0, supply: 1);
        int b = Put(s, WeaponId.SonicSpeaker, ShotKind.Speaker, 500, owner: 2, supply: -1);
        int lower = Math.Min(a, b);
        int last = -1000;
        int pulses = 0;
        for (int i = 0; i < 300; i++)
        {
            int frame = s.Frame;
            int before = s.Fighters[1].HitCount;
            Step(s);
            int got = s.Fighters[1].HitCount - before;
            Assert.True(got <= 1, "two pulses in one frame");
            if (got == 1)
            {
                Assert.True(frame - last >= 45, "shoved twice within 45 frames");
                Assert.Equal(s.Props[lower].Owner + 1, s.Fighters[1].LastHitBy); // the lower slot's
                last = frame;
                pulses++;
            }
            Hold(s, 1, Cm(200));
            Hold(s, 0, Cm(-200));
            Hold(s, 2, Cm(600));
        }
        Assert.True(pulses >= 6);
    }

    [Fact]
    public void ASpeakerNeverPulsesAtItsOwnersTeam()
    {
        var rules = new MatchRules { Stage = TestStages.Wide, Weapons = true, Guts = false, Teams = new[] { 1, 1, 2 } };
        GameState s = Standing(rules, -100, 200, 300);
        Put(s, WeaponId.SonicSpeaker, ShotKind.Speaker, 0, owner: 0, supply: 1);
        for (int i = 0; i < 100; i++)
        {
            Step(rules, s);
            Hold(s, 1, Cm(200));
            Hold(s, 2, Cm(300));
        }
        Assert.Equal(0, s.Fighters[1].HitCount); // a teammate
        Assert.True(s.Fighters[2].HitCount > 0);
    }

    /// <summary>It doesn't pulse at someone in the air with no platform anywhere below; over a platform it does.</summary>
    [Theory]
    [InlineData(1000, false)] // off the side of the Graybox, over the pit
    [InlineData(800, true)]   // in the air over the stage
    public void ASpeakerLeavesAFighterWithNothingBelowAlone(int xCm, bool pulsed)
    {
        var rules = new MatchRules { Stage = Stages.Graybox, Weapons = true, Guts = false };
        GameState s = Standing(rules, 0, 0);
        int slot = Put(s, WeaponId.SonicSpeaker, ShotKind.Speaker, 700, owner: 0, supply: 1);
        while (s.Frame % 45 != 0) Step(rules, s);
        s.Fighters[1].Position = new FixVec2(Cm(xCm), Cm(50));
        s.Fighters[1].Velocity = FixVec2.Zero;
        s.Fighters[1].Grounded = false;
        Step(rules, s);
        Assert.Equal(pulsed, s.Fighters[1].HitCount > 0);
        Assert.Equal(PropKind.Shot, s.Props[slot].Kind);
    }

    /// <summary>A fighter recovering from 4 m outside a ledge against a speaker at the ledge facing out gets back.</summary>
    [Theory]
    [InlineData("graybox", 100)]
    [InlineData("graybox", -100)]
    [InlineData("test-ledges", 100)]
    public void ASpeakerFacingOutCantHoldARecoveringFighterOut(string stageId, int heightCm)
    {
        StageDefinition stage = stageId == "graybox" ? Stages.Graybox : Ledges;
        var rules = new MatchRules { Stage = stage, Weapons = true };
        FixRect floor = stage.Platforms[0];
        GameState s = Simulation.CreateInitialState(rules, 2, 1);
        s.PropTimer = 1_000_000;
        s.Fighters[0].Position = new FixVec2(floor.MinX + Fix.One, floor.MaxY);
        s.Fighters[0].Grounded = true;
        int slot = s.Props.Length - 1;
        s.Props[slot] = new Prop
        {
            Kind = PropKind.Shot, Shot = ShotKind.Speaker, Weapon = WeaponId.SonicSpeaker, Owner = 0, OwnerClear = true, Supply = 1,
            Position = new FixVec2(floor.MaxX - Fix.Half, floor.MaxY), Grounded = true, Timer = 100_000,
        };
        ref Fighter d = ref s.Fighters[1];
        d.Position = new FixVec2(floor.MaxX + Fix.FromInt(4), floor.MaxY + Cm(heightCm));
        d.Velocity = FixVec2.Zero;
        d.Grounded = false;
        d.Facing = -1;
        var inputs = new Buttons[2];
        bool back = false;
        for (int f = 0; f < 600 && !back; f++)
        {
            inputs[1] = SparringBot.Think(s, rules, 1, CpuLevel.Hard);
            Simulation.Advance(s, rules, inputs);
            Assert.Equal(rules.Stocks, s.Fighters[1].Stocks);
            back = s.Fighters[1].Grounded && s.Fighters[1].Position.Y == floor.MaxY;
        }
        Assert.True(back, $"never got back on {stage.Id}");
    }

    // ---- The Portal Disc ----

    /// <summary>In one, out of the other standing still with its jumps back, and no bounce back.</summary>
    [Fact]
    public void AFighterGoesInOneGateAndOutOfTheOtherStandingStill()
    {
        GameState s = Standing(-200, 5000, 6000);
        int a = Put(s, WeaponId.PortalDisc, ShotKind.Gate, 0, owner: 2);
        int b = Put(s, WeaponId.PortalDisc, ShotKind.Gate, 1000, owner: 2, yCm: 150);
        ref Fighter f = ref s.Fighters[0];
        f.Velocity = new FixVec2(Cm(30), Fix.Zero);
        f.AirJumpsLeft = 0;
        f.AirDodgeUsed = true;
        f.RecoveryUsed = true;
        for (int i = 0; i < 20 && s.Fighters[0].Position.X < Fix.FromInt(5); i++)
            Step(s, Buttons.Right);
        Fighter after = s.Fighters[0];
        Assert.Equal(s.Props[b].Position, after.Position);
        Assert.Equal(FixVec2.Zero, after.Velocity);
        Assert.Equal(Rules.Tuning.AirJumps, after.AirJumpsLeft);
        Assert.False(after.AirDodgeUsed);
        Assert.False(after.RecoveryUsed);
        Assert.Equal(1, s.Props[a].HitMask & 1);
        Assert.Equal(1, s.Props[b].HitMask & 1);

        // It stays at the second gate: no bounce back while it touches it.
        Wait(s, 5);
        Assert.True(s.Fighters[0].Position.X > Fix.FromInt(9));
        // Stepping off clears the mark, so it can go through again.
        Hold(s, 0, Fix.FromInt(20));
        Step(s);
        Assert.Equal(0, s.Props[b].HitMask & 1);
    }

    [Fact]
    public void AGateWithNoPartnerDoesNothingAndShotsDontPass()
    {
        GameState s = Standing(0, 5000);
        Put(s, WeaponId.PortalDisc, ShotKind.Gate, 0, owner: 1);
        Wait(s, 3);
        Assert.Equal(Fix.Zero, s.Fighters[0].Position.X);
    }

    [Fact]
    public void AThirdGateClosesTheFirstAndTheOpenerStepsOffFirst()
    {
        GameState s = Standing(0, 5000);
        Arm(s, 0, WeaponId.PortalDisc, 3);
        Fire(s, WeaponId.PortalDisc);
        int first = SlotOf(s, ShotKind.Gate);
        Assert.Equal(FixVec2.Zero, s.Props[first].Position); // at the opener's feet
        Wait(s, 20);
        Hold(s, 0, Fix.FromInt(6));
        Fire(s, WeaponId.PortalDisc);
        Assert.Equal(2, Count(s, ShotKind.Gate));
        Assert.Equal(Fix.FromInt(6), s.Fighters[0].Position.X); // standing in the gate it opened: not sent back
        Wait(s, 20);
        Hold(s, 0, Fix.FromInt(-6));
        Fire(s, WeaponId.PortalDisc);
        Assert.Equal(2, Count(s, ShotKind.Gate));
        Assert.DoesNotContain(s.Props, p => p.Kind == PropKind.Shot && p.Shot == ShotKind.Gate && p.Position.X == Fix.Zero); // the oldest closed
        Assert.Contains(s.Props, p => p.Kind == PropKind.Shot && p.Shot == ShotKind.Gate && p.Position.X == Fix.FromInt(-6));
        Assert.Equal(0, s.Fighters[0].WeaponSupply);
    }

    /// <summary>A gate opens only on a safe spot: over the pit, or beside the blast line, the use fizzles and spends nothing.</summary>
    [Theory]
    [InlineData("pit")]
    [InlineData("blast line")]
    [InlineData("high")]
    public void AGateOnAnUnsafeSpotFizzlesAndSpendsNothing(string where)
    {
        StageDefinition stage = where == "blast line" ? Arena : Stages.Graybox;
        var rules = new MatchRules { Stage = stage, Weapons = true, Guts = false };
        GameState s = Standing(rules, 0, -500);
        ref Fighter f = ref s.Fighters[0];
        f.Position = where switch
        {
            "pit" => new FixVec2(Fix.FromInt(13), Fix.FromInt(-2)),
            "blast line" => new FixVec2(Cm(1560), Fix.Zero),
            _ => new FixVec2(Fix.Zero, Fix.FromInt(6)),
        };
        f.Grounded = where == "blast line";
        Arm(s, 0, WeaponId.PortalDisc);
        Fire(rules, s, WeaponId.PortalDisc);
        Assert.Equal(0, Count(s, ShotKind.Gate));
        Assert.Equal(WeaponId.PortalDisc, s.Fighters[0].Weapon);
        Assert.Equal(2, s.Fighters[0].WeaponSupply);
    }

    // ---- The Decoy Crate and the Crate Detonator ----

    /// <summary>It looks like a box with the last crate's weapon, falls and rests; it springs on the first other fighter to touch it, never on its owner.</summary>
    [Fact]
    public void ADecoySpringsOnAnotherFighterNeverOnItsOwner()
    {
        GameState s = Standing(0, 5000);
        s.LastBoxWeapon = WeaponId.Uzi;
        Arm(s, 0, WeaponId.DecoyCrate);
        s.Fighters[0].Grounded = false;
        s.Fighters[0].Position = new FixVec2(Fix.Zero, Fix.One);
        Fire(s, WeaponId.DecoyCrate);
        int slot = SlotOf(s, ShotKind.Decoy);
        Assert.True(slot >= 0);
        Assert.Equal((int)WeaponId.Uzi, s.Props[slot].Supply); // what it pretends to hold
        FixRect box = Simulation.PropBox(in s.Props[slot]);
        Assert.Equal(Weapons.BoxHalf * 2, box.MaxX - box.MinX); // a Prop Box's size
        Assert.Equal(Weapons.BoxHalf * 2, box.MaxY - box.MinY);
        Wait(s, 60);
        Assert.True(s.Props[slot].Grounded, "it falls and rests like a box");

        // Its owner walks over it: nothing.
        for (int i = 0; i < 30; i++) Step(s, Buttons.Right);
        Assert.Equal(PropKind.Shot, s.Props[slot].Kind);
        Assert.Equal(0, s.Fighters[0].Josh);

        // Another fighter touches it: punched skywards, and it is gone.
        Hold(s, 0, Fix.FromInt(-20));
        Hold(s, 1, s.Props[slot].Position.X + Fix.Half);
        Step(s);
        Assert.Equal(PropKind.None, s.Props[slot].Kind);
        Assert.Equal(Weapons.Get(WeaponId.DecoyCrate).Damage, s.Fighters[1].Josh);
        Assert.True(s.Fighters[1].Velocity.Y > Fix.Zero);
        Assert.Equal(1, s.Fighters[1].LastHitBy);
        Assert.Equal(WeaponId.DecoyCrate, s.Fighters[1].LastHitWeapon);
    }

    [Fact]
    public void ADecoyLooksLikeABelanBeforeAnyBoxAndLastsTwentySeconds()
    {
        GameState s = Standing(0, 5000);
        Arm(s, 0, WeaponId.DecoyCrate);
        Fire(s, WeaponId.DecoyCrate);
        int slot = SlotOf(s, ShotKind.Decoy);
        Assert.Equal((int)WeaponId.Belan, s.Props[slot].Supply);
        Hold(s, 0, Fix.FromInt(-20));
        Wait(s, 1198);
        Assert.Equal(PropKind.Shot, s.Props[slot].Kind);
        Wait(s, 2);
        Assert.Equal(PropKind.None, s.Props[slot].Kind);
    }

    /// <summary>A decoy waits for someone able to take a box: not attacking, not dodging; nor does a teammate spring it.</summary>
    [Fact]
    public void ADecoyWaitsForSomeoneAbleToTakeABox()
    {
        var rules = new MatchRules { Stage = TestStages.Wide, Weapons = true, Guts = false, Teams = new[] { 1, 1, 2 } };
        GameState s = Standing(rules, -2000, 0, 3000);
        int slot = Put(s, WeaponId.DecoyCrate, ShotKind.Decoy, 0, owner: 0, supply: (int)WeaponId.Uzi);
        Wait(rules, s, 5);
        Assert.Equal(PropKind.Shot, s.Props[slot].Kind); // a teammate stands on it
        Hold(s, 1, Fix.FromInt(-10));
        Hold(s, 2, Fix.Zero);
        s.Fighters[2].Action = FighterAction.Dodge;
        s.Fighters[2].ActionFrame = 0;
        Step(rules, s);
        Assert.Equal(PropKind.Shot, s.Props[slot].Kind); // dodging
        Hold(s, 2, Fix.Zero);
        s.Fighters[2].Action = FighterAction.Idle;
        Step(rules, s);
        Assert.Equal(PropKind.None, s.Props[slot].Kind);
        Assert.Equal(Weapons.Get(WeaponId.DecoyCrate).Damage, s.Fighters[2].Josh);
    }

    /// <summary>The Detonator puts down a rigged crate that nobody springs; the next press bursts it and its owner's Decoy Crates, nobody else's.</summary>
    [Fact]
    public void TheDetonatorPlacesThenBurstsAndTakesTheOwnersDecoyCratesWithIt()
    {
        GameState s = Standing(0, 5000, 6000);
        Arm(s, 0, WeaponId.CrateDetonator);
        Fire(s, WeaponId.CrateDetonator);
        int rigged = SlotOf(s, ShotKind.Decoy);
        Assert.True(rigged >= 0);
        Assert.Equal(WeaponId.CrateDetonator, s.Props[rigged].Weapon);
        Assert.Equal(5, s.Fighters[0].WeaponSupply);
        Wait(s, 20);
        Hold(s, 0, Fix.FromInt(-10));
        Hold(s, 1, s.Props[rigged].Position.X);
        Wait(s, 5);
        Assert.Equal(PropKind.Shot, s.Props[rigged].Kind); // a rigged crate does nothing when touched
        Assert.Equal(0, s.Fighters[1].Josh);

        int decoy = Put(s, WeaponId.DecoyCrate, ShotKind.Decoy, 3000, owner: 0, supply: 1);
        int theirs = Put(s, WeaponId.DecoyCrate, ShotKind.Decoy, 4000, owner: 2, supply: 1);
        Hold(s, 2, Cm(3050)); // fighter 2 stands by fighter 0's Decoy Crate
        s.Fighters[2].Action = FighterAction.Attack; // (and can't spring it)
        s.Fighters[2].Move = MoveId.Jab;
        s.Fighters[2].ActionFrame = 0;
        Fire(s, WeaponId.CrateDetonator);
        Assert.Equal(PropKind.None, s.Props[rigged].Kind);
        Assert.Equal(PropKind.None, s.Props[decoy].Kind);
        Assert.Equal(PropKind.Shot, s.Props[theirs].Kind);
        Assert.Equal(16, s.Fighters[1].Josh);
        Assert.Equal(16, s.Fighters[2].Josh); // the Decoy Crate burst with the Detonator's numbers
        Assert.Equal(WeaponId.CrateDetonator, s.Fighters[1].LastHitWeapon);
        Assert.Equal(4, s.Fighters[0].WeaponSupply);
    }

    [Fact]
    public void TheDetonatorsBurstHitsItsOwnerStandingInIt()
    {
        GameState s = Standing(0, 5000);
        Arm(s, 0, WeaponId.CrateDetonator);
        Fire(s, WeaponId.CrateDetonator);
        Wait(s, 20);
        Fire(s, WeaponId.CrateDetonator);
        Assert.Equal(16, s.Fighters[0].Josh);
        Assert.Equal(0, Count(s, ShotKind.Decoy));
    }

    // ---- The Teleporter ----

    [Fact]
    public void TheTeleporterBlinksFiveMetresAheadOntoAPlatform()
    {
        GameState s = Standing(0, 5000);
        Arm(s, 0, WeaponId.Teleporter);
        Fire(s, WeaponId.Teleporter);
        Fighter f = s.Fighters[0];
        Assert.Equal(new FixVec2(Fix.FromInt(5), Fix.Zero), f.Position);
        Assert.Equal(FixVec2.Zero, f.Velocity);
        Assert.True(f.Grounded);
        Assert.Equal(2, f.WeaponSupply);
    }

    /// <summary>Over nothing it fizzles: nothing spent, the weapon stays, nobody moves.</summary>
    [Fact]
    public void TheTeleporterFizzlesOverNothingWithoutSpending()
    {
        var rules = new MatchRules { Stage = Stages.Graybox, Weapons = true, Guts = false };
        GameState s = Standing(rules, 0, 500);
        ref Fighter f = ref s.Fighters[0];
        f.Position = new FixVec2(Fix.FromInt(-14), Fix.FromInt(4));
        f.Grounded = false;
        f.Facing = -1; // away from the stage
        Arm(s, 0, WeaponId.Teleporter);
        Fire(rules, s, WeaponId.Teleporter);
        Assert.Equal(3, s.Fighters[0].WeaponSupply);
        Assert.Equal(WeaponId.Teleporter, s.Fighters[0].Weapon);
        Assert.True(s.Fighters[0].Position.X < Fix.FromInt(-13));
    }

    /// <summary>It works in the air and off the stage, so it is a recovery too: back on the stage with the jumps given back.</summary>
    [Fact]
    public void TheTeleporterRecoversFromOffTheStage()
    {
        var rules = new MatchRules { Stage = Stages.Graybox, Weapons = true, Guts = false };
        GameState s = Standing(rules, 0, -500);
        ref Fighter f = ref s.Fighters[0];
        f.Position = new FixVec2(Fix.FromInt(12), Fix.FromInt(-1));
        f.Velocity = new FixVec2(Fix.Zero, Cm(-10));
        f.Grounded = false;
        f.Facing = -1;
        f.AirJumpsLeft = 0;
        f.RecoveryUsed = true;
        f.AirDodgeUsed = true;
        Arm(s, 0, WeaponId.Teleporter);
        Fire(rules, s, WeaponId.Teleporter, held: Buttons.Up);
        Fighter after = s.Fighters[0];
        Assert.Equal(Fix.Zero, after.Position.Y); // on the stage top
        Assert.True(after.Position.X <= Fix.FromInt(9));
        Assert.True(after.Grounded);
        Assert.Equal(rules.Tuning.AirJumps, after.AirJumpsLeft);
        Assert.False(after.RecoveryUsed);
        Assert.Equal(2, after.WeaponSupply);
    }

    /// <summary>Up + Light blinks up onto the platform above; Up + Heavy is still the recovery.</summary>
    [Fact]
    public void UpAndLightBlinksUpAndUpAndHeavyIsStillTheRecovery()
    {
        var rules = new MatchRules { Stage = Stages.Graybox, Weapons = true, Guts = false };
        GameState s = Standing(rules, 400, -500);
        Arm(s, 0, WeaponId.Teleporter);
        Fire(rules, s, WeaponId.Teleporter, held: Buttons.Up);
        Assert.Equal(new FixVec2(Fix.FromInt(4), Fix.FromRatio(11, 4)), s.Fighters[0].Position); // straight up onto the right platform

        GameState t = Standing(rules, 0, -500);
        Arm(t, 0, WeaponId.Teleporter);
        Step(rules, t, Buttons.Up | Buttons.Heavy);
        Assert.Equal(MoveId.UpHeavy, t.Fighters[0].Move);
        Assert.Equal(3, t.Fighters[0].WeaponSupply);
    }

    [Fact]
    public void ABlinkEndsARoot()
    {
        GameState s = Standing(0, 5000);
        Arm(s, 0, WeaponId.Teleporter);
        s.Fighters[0].RootFrames = 200;
        s.Fighters[0].StunWeapon = (int)WeaponId.Net;
        Fire(s, WeaponId.Teleporter);
        Assert.Equal(0, s.Fighters[0].RootFrames);
        Assert.Equal(0, s.Fighters[0].StunWeapon);
        Assert.Equal(Fix.FromInt(5), s.Fighters[0].Position.X);
    }

    // ---- The Swap Ray ----

    [Fact]
    public void TheSwapRaySwapsTheShooterAndTheFighterItHits()
    {
        GameState s = Standing(0, 500);
        Arm(s, 0, WeaponId.SwapRay);
        Fire(s, WeaponId.SwapRay);
        for (int i = 0; i < 30 && s.Fighters[1].Josh == 0; i++) Step(s);
        Assert.Equal(4, s.Fighters[1].Josh);
        Assert.Equal(Fix.FromInt(5), s.Fighters[0].Position.X);
        Assert.Equal(Fix.Zero, s.Fighters[1].Position.X);
        Assert.Equal(FixVec2.Zero, s.Fighters[0].Velocity);
        Assert.Equal(FixVec2.Zero, s.Fighters[1].Velocity);
        Assert.NotEqual(FighterAction.Hitstun, s.Fighters[1].Action); // a soft hit
    }

    /// <summary>Either spot unsafe (the target over the pit, the shooter beside the blast line): no swap, only the damage.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheSwapFailsWhenEitherSpotIsUnsafe(bool targetUnsafe)
    {
        // Beside the blast line (1.6 m inside it) is unsafe: the target there, or the shooter.
        var rules = new MatchRules { Stage = Arena, Weapons = true, Guts = false };
        GameState s = Standing(rules, targetUnsafe ? 1060 : -1560, targetUnsafe ? 1560 : -1060);
        Arm(s, 0, WeaponId.SwapRay);
        FixVec2 shooter = s.Fighters[0].Position;
        Fire(rules, s, WeaponId.SwapRay);
        for (int i = 0; i < 30 && s.Fighters[1].Josh == 0; i++) Step(rules, s);
        Assert.Equal(4, s.Fighters[1].Josh); // the damage only
        Assert.Equal(shooter.X, s.Fighters[0].Position.X);
    }

    /// <summary>A stage with a striker hazard standing on its floor, from 4 m to 6 m.</summary>
    private static readonly StageDefinition HazardStage = new()
    {
        Id = "test-hazard",
        Platforms = Arena.Platforms,
        SpawnPoints = Arena.SpawnPoints,
        BlastZone = Arena.BlastZone,
        Hazards = new[] { new Hazard(HazardKind.Hit, new FixRect(Fix.FromInt(4), Fix.Zero, Fix.FromInt(6), Fix.FromInt(2)), 1) },
    };

    /// <summary>
    /// A swap is refused while the target stands in a hazard that is warning; while the hazard is
    /// idle it goes ahead. (Hazard 0's place in its cycle is the match frame itself.)
    /// </summary>
    [Theory]
    [InlineData(Hazards.WarnAt - 21, false)]
    [InlineData(100, true)]
    public void ASwapIsRefusedInsideAWarningHazard(int frame, bool swaps)
    {
        var rules = new MatchRules { Stage = HazardStage, Weapons = true, Guts = false, Hazards = true };
        GameState s = Standing(rules, 0, 500);
        s.Frame = frame;
        Arm(s, 0, WeaponId.SwapRay);
        Fire(rules, s, WeaponId.SwapRay);
        for (int i = 0; i < 30 && s.Fighters[1].Josh == 0; i++) Step(rules, s);
        Assert.Equal(4, s.Fighters[1].Josh);
        int at = Hazards.CycleAt(s.Frame - 1, 0);
        Assert.Equal(swaps, at < Hazards.WarnAt || at >= Hazards.CooldownAt); // the case is what it says
        Assert.Equal(swaps ? Fix.FromInt(5) : Fix.Zero, s.Fighters[0].Position.X);
    }

    [Theory]
    [InlineData(Hazards.WarnAt - 1, true)]
    [InlineData(Hazards.WarnAt, false)]
    [InlineData(Hazards.ActiveAt + 5, false)]
    [InlineData(Hazards.CooldownAt, true)]
    public void ASpotInAHazardIsSafeOnlyWhileTheHazardIsIdleOrCooling(int frame, bool safe)
    {
        var rules = new MatchRules { Stage = HazardStage, Weapons = true, Hazards = true };
        GameState s = Simulation.CreateInitialState(rules, 2, 1);
        s.Frame = frame;
        Assert.Equal(safe, Simulation.SafeSpot(s, rules, new FixVec2(Fix.FromInt(5), Fix.Zero)));
        Assert.True(Simulation.SafeSpot(s, rules, new FixVec2(Fix.FromInt(-5), Fix.Zero))); // away from it
        var off = new MatchRules { Stage = HazardStage, Weapons = true };
        Assert.True(Simulation.SafeSpot(s, off, new FixVec2(Fix.FromInt(5), Fix.Zero))); // hazards off: scenery
    }

    // ---- The safe spot (1.6) ----

    [Fact]
    public void ASafeSpotIsInsideTheBlastZoneWithAPlatformBelowWithinFourMetres()
    {
        var rules = new MatchRules { Stage = Stages.Graybox, Weapons = true };
        GameState s = Simulation.CreateInitialState(rules, 2, 1);
        Assert.True(Simulation.SafeSpot(s, rules, new FixVec2(Fix.Zero, Fix.Zero)));
        Assert.True(Simulation.SafeSpot(s, rules, new FixVec2(Fix.Zero, Cm(390))));
        Assert.False(Simulation.SafeSpot(s, rules, new FixVec2(Fix.Zero, Cm(410))));        // 4.1 m above the floor (no platform over x = 0)
        Assert.True(Simulation.SafeSpot(s, rules, new FixVec2(Fix.FromInt(4), Cm(600))));  // 3.25 m above the right platform
        Assert.False(Simulation.SafeSpot(s, rules, new FixVec2(Fix.FromInt(12), Fix.Zero))); // over the pit
        var arena = new MatchRules { Stage = Arena, Weapons = true };
        Assert.False(Simulation.SafeSpot(s, arena, new FixVec2(Cm(1530), Fix.Zero)));       // 1.9 m inside the blast line
        Assert.True(Simulation.SafeSpot(s, arena, new FixVec2(Cm(1510), Fix.Zero)));
    }

    // ---- The CPU ----

    /// <summary>Off the stage with a Teleporter, the CPU blinks back onto it.</summary>
    [Fact]
    public void TheCpuBlinksBackWithTheTeleporter()
    {
        var rules = new MatchRules { Stage = Stages.Graybox, Weapons = true };
        GameState s = Standing(rules, -500, 0);
        ref Fighter f = ref s.Fighters[1];
        f.Position = new FixVec2(Fix.FromInt(11), Fix.FromInt(-1));
        f.Velocity = new FixVec2(Fix.Zero, Cm(-5));
        f.Grounded = false;
        Arm(s, 1, WeaponId.Teleporter);
        var inputs = new Buttons[2];
        for (int i = 0; i < 60 && !s.Fighters[1].Grounded; i++)
        {
            inputs[1] = SparringBot.Think(s, rules, 1, CpuLevel.Hard);
            Simulation.Advance(s, rules, inputs);
        }
        Assert.True(s.Fighters[1].Grounded, "it got back");
        Assert.Equal(2, s.Fighters[1].WeaponSupply); // by blinking
        Assert.Equal(rules.Tuning.AirJumps, s.Fighters[1].AirJumpsLeft);
    }

    /// <summary>On the stage the CPU throws the Teleporter at an opponent instead.</summary>
    [Fact]
    public void TheCpuThrowsTheTeleporterOnTheStage()
    {
        var rules = new MatchRules { Stage = Stages.Graybox, Weapons = true };
        GameState s = Standing(rules, -300, 0);
        Arm(s, 1, WeaponId.Teleporter);
        var inputs = new Buttons[2];
        bool thrown = false;
        for (int i = 0; i < 60 && !thrown; i++)
        {
            inputs[1] = SparringBot.Think(s, rules, 1, CpuLevel.Hard);
            Simulation.Advance(s, rules, inputs);
            thrown = s.Fighters[1].Move == MoveId.WeaponThrow;
            Assert.NotEqual(Weapons.Get(WeaponId.Teleporter).Move, s.Fighters[1].Move);
        }
        Assert.True(thrown);
    }

    /// <summary>The CPU presses the Detonator again once an opponent is within 1.5 m of its crate (and it is clear of the blast itself).</summary>
    [Theory]
    [InlineData(550, true)]
    [InlineData(900, false)]
    public void TheCpuBurstsItsCrateWhenAnOpponentIsBesideIt(int opponentCm, bool presses)
    {
        var rules = new MatchRules { Stage = Stages.Graybox, Weapons = true };
        GameState s = Standing(rules, 0, opponentCm);
        Arm(s, 0, WeaponId.CrateDetonator);
        Put(s, WeaponId.CrateDetonator, ShotKind.Decoy, 500, owner: 0, supply: 1);
        Buttons b = SparringBot.Think(s, rules, 0, CpuLevel.Hard);
        Assert.Equal(presses, (b & Buttons.Light) != 0);
    }

    /// <summary>All its crates go off together, so the CPU doesn't press while it stands by any one of them.</summary>
    [Fact]
    public void TheCpuHoldsItsBurstWhileItStandsByOneOfItsCrates()
    {
        var rules = new MatchRules { Stage = Stages.Graybox, Weapons = true };
        GameState s = Standing(rules, 0, 550);
        Arm(s, 0, WeaponId.CrateDetonator);
        Put(s, WeaponId.CrateDetonator, ShotKind.Decoy, 500, owner: 0, supply: 1);
        Put(s, WeaponId.DecoyCrate, ShotKind.Decoy, 60, owner: 0, supply: 1);
        Assert.Equal(Buttons.None, SparringBot.Think(s, rules, 0, CpuLevel.Hard) & Buttons.Light);
    }

    /// <summary>Empty-handed, the CPU walks to a decoy that isn't its own as to a box; its own it leaves alone.</summary>
    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void TheCpuWalksToADecoyThatIsntItsOwn(int owner, bool walksToIt)
    {
        var rules = new MatchRules { Stage = Stages.Graybox, Weapons = true };
        GameState s = Standing(rules, 0, -800);
        Put(s, WeaponId.DecoyCrate, ShotKind.Decoy, 300, owner: owner, supply: 1);
        Buttons b = SparringBot.Think(s, rules, 0, CpuLevel.Hard);
        Assert.Equal(walksToIt ? Buttons.Right : Buttons.Left, b & (Buttons.Left | Buttons.Right));
    }
}
