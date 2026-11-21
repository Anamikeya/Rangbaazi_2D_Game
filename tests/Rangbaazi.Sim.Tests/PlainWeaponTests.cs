using Rangbaazi.Sim.Diagnostics;
using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim.Tests;

/// <summary>
/// The plain weapons of combat spec v0.4, sections 2.1 to 2.3 (batch 2): one behaviour test per
/// archetype (swing, lunge and carry, blast and <c>first</c>, repeating fire, pellets and spread,
/// arrow, bomb, boomerang, bat), the sweet-spot rules they add, and the shove test of section 2.2.
/// Most tests use a very wide floor and hold back the box timer.
/// </summary>
public class PlainWeaponTests
{
    private static readonly MatchRules Rules = new() { Stage = TestStages.Wide, Weapons = true, Guts = false };

    private static Fix Cm(int cm) => Fix.FromRatio(cm, 100);

    /// <summary>Fighters standing at these x positions (metres x 100), P1 facing right, the rest left; no boxes due.</summary>
    private static GameState Standing(params int[] xCm)
    {
        var s = Simulation.CreateInitialState(Rules, xCm.Length, 1);
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

    private static void Arm(GameState s, int fighter, WeaponId w, int supply = -1)
    {
        s.Fighters[fighter].Weapon = w;
        s.Fighters[fighter].WeaponSupply = supply >= 0 ? supply : Weapons.Get(w).Supply;
    }

    private static void Step(GameState s, params Buttons[] inputs)
    {
        var all = new Buttons[s.Fighters.Length];
        inputs.CopyTo(all, 0);
        Simulation.Advance(s, Rules, all);
    }

    private static void Wait(GameState s, int frames)
    {
        for (int i = 0; i < frames; i++) Step(s);
    }

    /// <summary>Fighter 0 presses attack once and the move plays to its end.</summary>
    private static void Use(GameState s)
    {
        Step(s, Buttons.Light);
        for (int i = 0; i < 200 && s.Fighters[0].Action == FighterAction.Attack; i++) Step(s);
    }

    /// <summary>Fighter 0 presses attack, and the frames pass up to and including the move's startup frame: a shot is out, a hitbox has hit.</summary>
    private static void Fire(GameState s, WeaponId w)
    {
        Step(s, Buttons.Light);
        Wait(s, Weapons.Get(w).Startup);
    }

    private static int Count(GameState s, ShotKind shot) => s.Props.Count(p => p.Kind == PropKind.Shot && p.Shot == shot);

    private static int Sweet(int damage) => damage * Weapons.SweetPercent / 100;

    /// <summary>A shot of this weapon in flight, owned by fighter 0 and already clear of them, in the last prop slot.</summary>
    private static ref Prop PutShot(GameState s, WeaponId w, int xCm, int vxCm)
    {
        WeaponRow row = Weapons.Get(w);
        s.Props[^1] = new Prop
        {
            Kind = PropKind.Shot, Shot = row.Flight, Weapon = w, Owner = 0, OwnerClear = true,
            Position = new FixVec2(Cm(xCm), Weapons.HandHeight - row.ShotHalf),
            Velocity = new FixVec2(Cm(vxCm), Fix.Zero),
            Timer = row.Fuse,
        };
        return ref s.Props[^1];
    }

    // ---- Swings (2.1) ----

    [Fact]
    public void ASwingHitsInItsBoxOnItsActiveFramesOnly()
    {
        WeaponRow plank = Weapons.Get(WeaponId.Plank);
        GameState s = Standing(0, 100);
        Arm(s, 0, WeaponId.Plank);
        Step(s, Buttons.Light);
        Wait(s, plank.Startup - 1);
        Assert.Equal(0, s.Fighters[1].Josh);            // still winding up
        Step(s);
        Assert.Equal(plank.Damage, s.Fighters[1].Josh); // its first active frame
        Assert.Equal(plank.Supply - 1, s.Fighters[0].WeaponSupply); // a hit of durability

        // Someone who steps into the box once the active frames are over isn't hit.
        GameState late = Standing(0, 5000);
        Arm(late, 0, WeaponId.Plank);
        Step(late, Buttons.Light);
        Wait(late, plank.Startup + plank.Active);
        late.Fighters[1].Position = new FixVec2(Fix.One, Fix.Zero);
        Wait(late, plank.Recovery);
        Assert.Equal(0, late.Fighters[1].Josh);
        Assert.Equal(4, late.Fighters[0].WeaponSupply);

        // And someone beyond its reach never is.
        GameState far = Standing(0, 230);
        Arm(far, 0, WeaponId.Plank);
        Use(far);
        Assert.Equal(0, far.Fighters[1].Josh);
    }

    [Fact]
    public void ThePlankBreaksAfterFourHits()
    {
        GameState s = Standing(0, 100);
        Arm(s, 0, WeaponId.Plank);
        for (int i = 0; i < 4; i++)
        {
            Use(s);
            s.Fighters[1].Position = new FixVec2(Fix.One, Fix.Zero); // back in reach, standing
            s.Fighters[1].Velocity = FixVec2.Zero;
            s.Fighters[1].Grounded = true;
            s.Fighters[1].Action = FighterAction.Idle;
            s.Fighters[1].HitStop = 0;
            s.Fighters[0].HitStop = 0;
        }
        Assert.Equal(0, s.Fighters[0].WeaponSupply);
        Step(s, Buttons.Light);
        Assert.Equal(MoveId.WeaponThrow, s.Fighters[0].Move); // empty: the next press throws it
    }

    [Theory]
    [InlineData(WeaponId.Gada, 150, 20)]
    [InlineData(WeaponId.SilBatta, 100, 23)]
    public void ASwingThatHitsBothSidesHitsBehindAndCostsOnePerFighterHit(WeaponId w, int xCm, int damage)
    {
        GameState s = Standing(0, xCm, -xCm);
        Arm(s, 0, w);
        Fire(s, w);
        Assert.Equal(damage, s.Fighters[1].Josh);
        Assert.Equal(damage, s.Fighters[2].Josh);
        Assert.True(s.Fighters[1].Velocity.X > Fix.Zero && s.Fighters[2].Velocity.X < Fix.Zero, "each is launched away from the wielder");
        Assert.Equal(Weapons.Get(w).Supply - 2, s.Fighters[0].WeaponSupply);
    }

    [Fact]
    public void BoxingGlovesCounterSomeoneMidAttack()
    {
        GameState calm = Standing(0, 100), busy = Standing(0, 100);
        Arm(calm, 0, WeaponId.BoxingGloves);
        Arm(busy, 0, WeaponId.BoxingGloves);
        Step(calm, Buttons.Light);
        Step(busy, Buttons.Light, Buttons.Heavy); // P2 starts a slow heavy at the same moment
        Wait(calm, 3);
        Wait(busy, 3);
        Assert.Equal(7, calm.Fighters[1].Josh);
        Assert.Equal(Sweet(7), busy.Fighters[1].Josh);
        Assert.True(busy.Fighters[1].LastHitSweet);
    }

    [Fact]
    public void TheKatarsSweetSpotIsAHitFromBehind()
    {
        GameState front = Standing(0, 120), back = Standing(0, 120);
        back.Fighters[1].Facing = 1; // looking away from the attacker
        Arm(front, 0, WeaponId.Katar);
        Arm(back, 0, WeaponId.Katar);
        Fire(front, WeaponId.Katar);
        Fire(back, WeaponId.Katar);
        Assert.Equal(5, front.Fighters[1].Josh);
        Assert.False(front.Fighters[1].LastHitSweet);
        Assert.Equal(Sweet(5), back.Fighters[1].Josh);
        Assert.True(back.Fighters[1].LastHitSweet);
    }

    [Theory]
    [InlineData(WeaponId.Trident, 120, 210, 12)]
    [InlineData(WeaponId.CricketBat, 80, 180, 13)]
    public void ATipHitLaunchesAtTheRowsOwnSweetAngle(WeaponId w, int nearCm, int tipCm, int damage)
    {
        GameState body = Standing(0, nearCm), tip = Standing(0, tipCm);
        Arm(body, 0, w);
        Arm(tip, 0, w);
        Fire(body, w);
        Fire(tip, w);
        Assert.Equal(damage, body.Fighters[1].Josh);
        Assert.Equal(Sweet(damage), tip.Fighters[1].Josh);
        Fighter b = body.Fighters[1], t = tip.Fighters[1];
        Assert.True(t.Velocity.Y * b.Velocity.X > b.Velocity.Y * t.Velocity.X, "the tip launches steeper");
        // The tip's own angle, exactly: the launch is the row's sweet vector.
        WeaponRow row = Weapons.Get(w);
        Assert.True(Fix.Abs(t.Velocity.Y * row.SweetLaunchX - t.Velocity.X * row.SweetLaunchY) < Fix.FromRatio(1, 1000));
    }

    [Fact]
    public void TheKatanasTipIsASweetSpotAtItsOrdinaryAngle()
    {
        // The draw dashes forward first, so the tip is measured from where the dash has carried it.
        GameState tip = Standing(0, 290), body = Standing(0, 160);
        Arm(tip, 0, WeaponId.Katana);
        Arm(body, 0, WeaponId.Katana);
        Fire(tip, WeaponId.Katana);
        Fire(body, WeaponId.Katana);
        Assert.Equal(11, body.Fighters[1].Josh);
        Assert.Equal(Sweet(11), tip.Fighters[1].Josh);
        Fighter b = body.Fighters[1], t = tip.Fighters[1];
        Assert.True(Fix.Abs(t.Velocity.Y * b.Velocity.X - b.Velocity.Y * t.Velocity.X) < Fix.FromRatio(1, 1000), "the same angle, tip or not");
    }

    [Fact]
    public void TheSledgehammerHitsHardestAndItsGuardBreakIsStoredForLater()
    {
        GameState s = Standing(0, 120);
        Arm(s, 0, WeaponId.Sledgehammer);
        Fire(s, WeaponId.Sledgehammer);
        int damage = Weapons.Get(WeaponId.Sledgehammer).Damage;
        Assert.Equal(damage, s.Fighters[1].Josh);
        Assert.True(Weapons.Get(WeaponId.Sledgehammer).GuardBreak);
        Assert.All(Weapons.CrateList, id => Assert.True(Weapons.Get(id).Damage <= damage));
    }

    // ---- Lunges and carry ----

    [Fact]
    public void TheKatanaDashesAboutTwoMetresAndTheTalwarStepsForward()
    {
        GameState s = Standing(0);
        Arm(s, 0, WeaponId.Katana);
        Use(s);
        Fix dash = s.Fighters[0].Position.X;
        Assert.True(dash > Cm(150) && dash < Cm(220), $"the Katana moved {dash}");

        GameState t = Standing(0);
        Arm(t, 0, WeaponId.Talwar);
        Use(t);
        Fix step = t.Fighters[0].Position.X;
        Assert.True(step > Cm(40) && step < dash, $"the Talwar moved {step}");

        GameState k = Standing(0);
        Arm(k, 0, WeaponId.Katar);
        Step(k, Buttons.Light);
        Step(k);
        Assert.Equal(Weapons.Get(WeaponId.Katar).Lunge, k.Fighters[0].Velocity.X); // its lunge is on frame 1
    }

    [Theory]
    [InlineData(WeaponId.Minigun, 60)]
    [InlineData(WeaponId.Sledgehammer, 80)]
    [InlineData(WeaponId.SilBatta, 85)]
    [InlineData(WeaponId.Gada, 85)]
    [InlineData(WeaponId.Broadsword, 90)]
    [InlineData(WeaponId.Katana, 100)]
    public void AHeavyWeaponSlowsItsCarrier(WeaponId w, int percent)
    {
        GameState s = Standing(0);
        Arm(s, 0, w);
        for (int i = 0; i < 40; i++) Step(s, Buttons.Right);
        Assert.Equal(Rules.Tuning.RunSpeed * percent / 100, s.Fighters[0].Velocity.X);
        Assert.Equal(percent, Weapons.Get(w).Carry);
    }

    [Theory]
    [InlineData(WeaponId.Magnum357)]
    [InlineData(WeaponId.SniperRifle)]
    [InlineData(WeaponId.ConfettiCannon)]
    [InlineData(WeaponId.RPG)]
    public void AKickPushesTheShooterBackOnTheFrameItFires(WeaponId w)
    {
        GameState s = Standing(0);
        Arm(s, 0, w);
        Step(s, Buttons.Light);
        Wait(s, Weapons.Get(w).Startup - 1);
        Assert.Equal(Fix.Zero, s.Fighters[0].Velocity.X);
        Step(s);
        Assert.Equal(Weapons.Get(w).Lunge, s.Fighters[0].Velocity.X);
        Assert.True(s.Fighters[0].Velocity.X < Fix.Zero);
        Assert.Equal(Weapons.Get(w).Supply - 1, s.Fighters[0].WeaponSupply);
    }

    // ---- Blasts, and `first` (2.2, 1.7) ----

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void TheSniperHitsTheNearerOfTwoOnItsLineAndNotTheFarther(int near, int far)
    {
        GameState s = Standing(0, near == 1 ? 500 : 1300, near == 1 ? 1300 : 500);
        Arm(s, 0, WeaponId.SniperRifle);
        Fire(s, WeaponId.SniperRifle);
        WeaponRow sniper = Weapons.Get(WeaponId.SniperRifle);
        Assert.Equal(sniper.Damage, s.Fighters[near].Josh);
        Assert.Equal(0, s.Fighters[far].Josh);
        Assert.Equal(sniper.Supply - 1, s.Fighters[0].WeaponSupply); // spent when fired, hit or miss
    }

    [Fact]
    public void TwoFightersTheSameDistanceAwayTheLowerIndexTakesAFirstBlast()
    {
        GameState s = Standing(0, 500, 500);
        Arm(s, 0, WeaponId.SniperRifle);
        Fire(s, WeaponId.SniperRifle);
        Assert.Equal((Weapons.Get(WeaponId.SniperRifle).Damage, 0), (s.Fighters[1].Josh, s.Fighters[2].Josh));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void ADodgingNearerFighterDoesNotShieldTheFartherFromAFirstBlast(int near, int far)
    {
        GameState s = Standing(0, near == 1 ? 500 : 1300, near == 1 ? 1300 : 500);
        Arm(s, 0, WeaponId.SniperRifle);
        var inputs = new Buttons[3];
        inputs[0] = Buttons.Light;
        int startup = Weapons.Get(WeaponId.SniperRifle).Startup;
        for (int f = 0; f <= startup; f++)
        {
            inputs[near] = f == startup - 10 ? Buttons.Dodge : Buttons.None; // ten frames into a spot dodge when the shot comes: past the parry window
            Simulation.Advance(s, Rules, inputs);
            inputs[0] = Buttons.None;
        }
        Assert.Equal(0, s.Fighters[near].Josh);
        Assert.Equal(FighterAction.Dodge, s.Fighters[near].Action);
        Assert.Equal(Sweet(Weapons.Get(WeaponId.SniperRifle).Damage), s.Fighters[far].Josh); // and 10 m or more is the rifle's sweet spot
        Assert.True(s.Fighters[far].LastHitSweet);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void AParryingNearerFighterDoesShieldTheFartherFromAFirstBlast(int near, int far)
    {
        GameState s = Standing(0, near == 1 ? 500 : 1300, near == 1 ? 1300 : 500);
        Arm(s, 0, WeaponId.SniperRifle);
        var inputs = new Buttons[3];
        inputs[0] = Buttons.Light;
        int startup = Weapons.Get(WeaponId.SniperRifle).Startup;
        for (int f = 0; f <= startup; f++)
        {
            inputs[near] = f == startup - 2 ? Buttons.Dodge : Buttons.None; // two frames into the dodge: a parry
            Simulation.Advance(s, Rules, inputs);
            inputs[0] = Buttons.None;
        }
        Assert.Equal(0, s.Fighters[near].Josh);
        Assert.Equal(0, s.Fighters[far].Josh);
        Assert.Equal(FighterAction.Stunned, s.Fighters[0].Action); // the sniper was parried
        Assert.Equal(FighterAction.Idle, s.Fighters[near].Action);
    }

    [Fact]
    public void ABlastWithoutFirstStillHitsEveryoneInItsBox()
    {
        GameState shotgun = Standing(0, 150, 250);
        Arm(shotgun, 0, WeaponId.Shotgun);
        Fire(shotgun, WeaponId.Shotgun);
        Assert.True(shotgun.Fighters[1].Josh > 0 && shotgun.Fighters[2].Josh > 0);

        GameState confetti = Standing(0, 150, 300);
        Arm(confetti, 0, WeaponId.ConfettiCannon);
        Fire(confetti, WeaponId.ConfettiCannon);
        Assert.Equal((8, 8), (confetti.Fighters[1].Josh, confetti.Fighters[2].Josh));
        Assert.Equal(2, confetti.Fighters[0].WeaponSupply); // one use, however many it hit
    }

    [Fact]
    public void TheSpringGloveShovesOnlyTheNearestFighterWithinSixMetres()
    {
        GameState s = Standing(0, 200, 400);
        Arm(s, 0, WeaponId.SpringGloveLauncher);
        Fire(s, WeaponId.SpringGloveLauncher);
        Assert.Equal((Weapons.Get(WeaponId.SpringGloveLauncher).Damage, 0), (s.Fighters[1].Josh, s.Fighters[2].Josh));

        GameState far = Standing(0, 700);
        Arm(far, 0, WeaponId.SpringGloveLauncher);
        Use(far);
        Assert.Equal(0, far.Fighters[1].Josh);
        Assert.Equal(Weapons.Get(WeaponId.SpringGloveLauncher).Supply - 1, far.Fighters[0].WeaponSupply);
    }

    [Fact]
    public void TheMagnumsSweetSpotIsATargetInTheAir()
    {
        GameState ground = Standing(0, 500), air = Standing(0, 500);
        Arm(ground, 0, WeaponId.Magnum357);
        Arm(air, 0, WeaponId.Magnum357);
        Step(ground, Buttons.Light);
        Step(air, Buttons.Light);
        for (int i = 0; i < 14; i++)
        {
            Step(ground);
            air.Fighters[1].Position = new FixVec2(Fix.FromInt(5), Fix.FromRatio(1, 4)); // held just off the floor
            air.Fighters[1].Velocity = FixVec2.Zero;
            air.Fighters[1].Grounded = false;
            Step(air);
        }
        Assert.Equal(16, ground.Fighters[1].Josh);
        Assert.Equal(Sweet(16), air.Fighters[1].Josh);

        GameState beyond = Standing(0, 1000); // it reaches 9 m and no further
        Arm(beyond, 0, WeaponId.Magnum357);
        Use(beyond);
        Assert.Equal(0, beyond.Fighters[1].Josh);
    }

    [Theory]
    [InlineData(WeaponId.PressureCooker, 300)]
    [InlineData(WeaponId.Dhol, 280)]
    public void ABlastOnBothSidesHitsEveryoneAroundItForOneUse(WeaponId w, int xCm)
    {
        int damage = Weapons.Get(w).Damage;
        GameState s = Standing(0, xCm, -xCm);
        Arm(s, 0, w);
        Step(s, Buttons.Light);
        Wait(s, Weapons.Get(w).Startup - 1);
        Assert.Equal((0, 0), (s.Fighters[1].Josh, s.Fighters[2].Josh)); // the wind-up everyone can see
        Step(s);
        Assert.Equal((damage, damage), (s.Fighters[1].Josh, s.Fighters[2].Josh));
        Assert.True(s.Fighters[1].Velocity.X > Fix.Zero && s.Fighters[2].Velocity.X < Fix.Zero);
        Assert.Equal(Weapons.Get(w).Supply - 1, s.Fighters[0].WeaponSupply);
    }

    [Fact]
    public void TheDholDazesSomeoneItCatchesMidAttack()
    {
        GameState calm = Standing(0, 200), busy = Standing(0, 200);
        Arm(calm, 0, WeaponId.Dhol);
        Arm(busy, 0, WeaponId.Dhol);
        Step(calm, Buttons.Light);
        Step(busy, Buttons.Light);
        int startup = Weapons.Get(WeaponId.Dhol).Startup;
        Wait(calm, startup - 1);
        Wait(busy, startup - 9);
        for (int i = 0; i < 8; i++) Step(busy, Buttons.None, i == 0 ? Buttons.Heavy : Buttons.None); // P2 is winding up a heavy when the beat lands
        Step(calm);
        Step(busy);
        Assert.Equal(Weapons.Get(WeaponId.Dhol).Damage, calm.Fighters[1].Josh);
        Assert.Equal(Sweet(Weapons.Get(WeaponId.Dhol).Damage), busy.Fighters[1].Josh);
        Assert.Equal(calm.Fighters[1].HitstunFrames + 8, busy.Fighters[1].HitstunFrames);
    }

    // ---- Repeating fire (2.3) ----

    [Fact]
    public void TheTommyGunFiresEverySixFramesWhileHeldAndStopsWhenReleased()
    {
        GameState s = Standing(0);
        Arm(s, 0, WeaponId.TommyGun);
        for (int i = 0; i < 30; i++) Step(s, Buttons.Light);
        Assert.Equal(36 - 5, s.Fighters[0].WeaponSupply); // shots on frames 5, 11, 17, 23, 29
        Wait(s, 30);
        Assert.Equal(31, s.Fighters[0].WeaponSupply);
        Assert.Equal(FighterAction.Idle, s.Fighters[0].Action);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheBurstRifleFiresThreeOnATapAndThreeOnAHold(bool held)
    {
        GameState s = Standing(0, 300);
        Arm(s, 0, WeaponId.BurstRifle);
        Step(s, Buttons.Light);
        var shotFrames = new List<int>();
        for (int f = 1; f < 70; f++)
        {
            int before = s.Fighters[0].WeaponSupply;
            Step(s, held ? Buttons.Light : Buttons.None);
            if (s.Fighters[0].WeaponSupply < before) shotFrames.Add(f);
        }
        Assert.Equal(new[] { 6, 10, 14 }, shotFrames);
        Assert.Equal(12, s.Fighters[0].WeaponSupply);
        Assert.Equal(FighterAction.Idle, s.Fighters[0].Action);
        Assert.Equal(4 + 4 + 5, s.Fighters[1].Josh); // all three on one target: the third is the sweet spot
    }

    [Fact]
    public void TheMinigunSpinsUpThenOverheatsAfterTwentyFourShots()
    {
        GameState s = Standing(0);
        Arm(s, 0, WeaponId.Minigun);
        for (int i = 0; i < 30; i++) Step(s, Buttons.Light);
        Assert.Equal(45, s.Fighters[0].WeaponSupply); // half a second of spin-up
        Step(s, Buttons.Light);
        Assert.Equal(44, s.Fighters[0].WeaponSupply);
        for (int i = 0; i < 200; i++) Step(s, Buttons.Light);
        Assert.Equal(45 - 24, s.Fighters[0].WeaponSupply);
        Assert.Equal(FighterAction.Idle, s.Fighters[0].Action); // and holding on doesn't start it again: that takes a fresh press
    }

    /// <summary>Fighter 0 holds attack for this many frames; the upward speed of every bullet, in the order they left.</summary>
    private static List<Fix> HoldAndCollect(GameState s, int frames)
    {
        var up = new List<Fix>();
        for (int f = 0; f < frames; f++)
        {
            Step(s, Buttons.Light);
            foreach (Prop p in s.Props)
                if (p.Kind == PropKind.Shot && p.Shot == ShotKind.Bullet && p.Age == 1) up.Add(p.Velocity.Y);
        }
        return up;
    }

    // ---- Pellets and spread ----

    [Fact]
    public void NinjaStarsLeaveAsThreePropsWithThreeUpwardSpeeds()
    {
        GameState s = Standing(0);
        Arm(s, 0, WeaponId.NinjaStars);
        Fire(s, WeaponId.NinjaStars);
        Prop[] stars = s.Props.Where(p => p.Kind == PropKind.Shot).ToArray();
        Assert.Equal(3, stars.Length);
        Assert.All(stars, p => Assert.Equal((ShotKind.Bullet, WeaponId.NinjaStars), (p.Shot, p.Weapon)));
        Assert.Equal(new[] { Cm(-6), Fix.Zero, Cm(6) }, stars.Select(p => p.Velocity.Y).OrderBy(v => v.Raw));
        Assert.Equal(4, s.Fighters[0].WeaponSupply); // one throw, three stars
    }

    [Fact]
    public void TheAksFifthShotOfABurstFliesHigherThanItsFirstAndItHoldsTheLast()
    {
        GameState s = Standing(0);
        Arm(s, 0, WeaponId.AK47);
        List<Fix> up = HoldAndCollect(s, 5 + 7 * 7 + 1);
        Assert.Equal(8, up.Count);
        Assert.Equal(Fix.Zero, up[0]);
        Assert.True(up[4] > up[0]);
        for (int i = 1; i < 6; i++) Assert.True(up[i] > up[i - 1], "each shot climbs");
        Assert.Equal(up[5], up[6]); // then it stays on the last entry
        Assert.Equal(up[5], up[7]);
    }

    [Fact]
    public void TheTommyGunsTenthAndEleventhShotsStayWide()
    {
        GameState s = Standing(0);
        Arm(s, 0, WeaponId.TommyGun);
        List<Fix> up = HoldAndCollect(s, 5 + 6 * 12 + 1);
        Assert.Equal(13, up.Count);
        Assert.Equal(Fix.Zero, up[0]);
        Assert.Equal((Cm(8), Cm(-8)), (up[7], up[8]));   // the list's last two
        Assert.Equal((Cm(8), Cm(-8)), (up[9], up[10]));  // the 10th and 11th: still wide, not back at 0
        Assert.Equal((Cm(8), Cm(-8)), (up[11], up[12]));
    }

    [Fact]
    public void TheMinigunsSpreadGoesRoundItsList()
    {
        GameState s = Standing(0);
        Arm(s, 0, WeaponId.Minigun);
        List<Fix> up = HoldAndCollect(s, 30 + 3 * 8 + 1);
        Assert.Equal(9, up.Count);
        Assert.Equal((Fix.Zero, Cm(9), Cm(-9)), (up[0], up[5], up[6]));
        Assert.Equal((up[0], up[1]), (up[7], up[8])); // seven entries, then round again
    }

    [Fact]
    public void TheRayGunsBoltFreezesItsTargetLikeARealHit()
    {
        GameState s = Standing(0, 400);
        Arm(s, 0, WeaponId.RayGun);
        Use(s);
        Assert.Equal(6, s.Fighters[1].Josh);
        Assert.Equal(FighterAction.Hitstun, s.Fighters[1].Action);
        Assert.False(Weapons.Get(WeaponId.RayGun).Chip);
        Assert.Equal(9, s.Fighters[0].WeaponSupply); // one bolt a press: it doesn't repeat
    }

    // ---- Arrows ----

    [Fact]
    public void TheCrossbowBoltStopsInTheFirstOfTwoFighters()
    {
        GameState s = Standing(0, 400, 700);
        Arm(s, 0, WeaponId.Crossbow);
        Use(s);
        Assert.Equal(14, s.Fighters[1].Josh);
        Assert.Equal(0, s.Fighters[2].Josh);
        Assert.Equal(0, Count(s, ShotKind.Arrow));

        GameState bow = Standing(0, 400, 700); // the Bow's arrow still goes through both
        Arm(bow, 0, WeaponId.Bow);
        Use(bow);
        Wait(bow, 20);
        Assert.Equal((10, 10), (bow.Fighters[1].Josh, bow.Fighters[2].Josh));
    }

    [Fact]
    public void TheCrossbowsSweetSpotIsABoltThatFlewTwelveFrames()
    {
        GameState s = Standing(0, 1500);
        Arm(s, 0, WeaponId.Crossbow);
        Use(s);
        Assert.Equal(Sweet(14), s.Fighters[1].Josh);
    }

    [Fact]
    public void TheSlingshotsPebbleArcsAndALongArcIsItsSweetSpot()
    {
        GameState s = Standing(0, 400);
        Arm(s, 0, WeaponId.Slingshot);
        Fire(s, WeaponId.Slingshot);
        Prop pebble = s.Props.Single(p => p.Kind == PropKind.Shot);
        Assert.Equal(ShotKind.Arrow, pebble.Shot);
        Assert.True(pebble.Velocity.Y > Fix.Zero, "it leaves upwards");
        Wait(s, 12);
        Assert.Equal(5, s.Fighters[1].Josh);
        Assert.Equal(0, Count(s, ShotKind.Arrow)); // it stopped in them

        GameState far = Standing(0, 950);
        Arm(far, 0, WeaponId.Slingshot);
        Fire(far, WeaponId.Slingshot);
        Wait(far, 25);
        Assert.Equal(Sweet(5), far.Fighters[1].Josh);
        Assert.True(far.Fighters[1].LastHitSweet);
    }

    // ---- Bombs and rockets ----

    [Fact]
    public void StickGrenadesDoNotBurstOnTouchAndDoWhenTheFuseEnds()
    {
        GameState s = Standing(0, 300);
        ref Prop grenade = ref PutShot(s, WeaponId.StickGrenades, 300, 0);
        grenade.Timer = 30;
        Wait(s, 29);
        Assert.Equal(0, s.Fighters[1].Josh);              // it has been lying on them the whole time
        Assert.Equal(1, Count(s, ShotKind.Bomb));
        Step(s);
        Assert.Equal(Weapons.Get(WeaponId.StickGrenades).Damage, s.Fighters[1].Josh);
        Assert.Equal(0, Count(s, ShotKind.Bomb));
        Assert.False(s.Fighters[1].LastHitSweet);         // no direct hits: it never bursts on anyone
    }

    [Fact]
    public void AStickGrenadeCountsDownItsFuseFromTheThrow()
    {
        GameState s = Standing(0);
        WeaponRow grenades = Weapons.Get(WeaponId.StickGrenades);
        Arm(s, 0, WeaponId.StickGrenades);
        Fire(s, WeaponId.StickGrenades);
        Assert.Equal(1, Count(s, ShotKind.Bomb));
        Assert.Equal(grenades.Supply - 1, s.Fighters[0].WeaponSupply);
        Wait(s, grenades.Fuse - 2);
        Assert.Equal(1, Count(s, ShotKind.Bomb));
        Prop grenade = s.Props.First(p => p.Kind == PropKind.Shot);
        Assert.True(grenade.Position.X > Cm(500), $"it is lobbed about 6 m: {grenade.Position.X}");
        Assert.False(grenade.Grounded); // it bursts at the end of its arc, at about hand height, before it lands
        Step(s);
        Assert.Equal(0, Count(s, ShotKind.Bomb)); // its fuse after it left the hand
    }

    [Fact]
    public void TheRpgFliesStraightPastAFighterAboveItAndBurstsOnTheOneInItsWay()
    {
        GameState s = Standing(0, 500, 1500);
        Arm(s, 0, WeaponId.RPG);
        Step(s, Buttons.Light);
        for (int f = 0; f < 60 && s.Fighters[2].Josh == 0; f++)
        {
            // Fighter 1 hangs 4 m up, right over the rocket's path: a homing missile would climb to them.
            s.Fighters[1].Position = new FixVec2(Fix.FromInt(5), Fix.FromInt(4));
            s.Fighters[1].Velocity = FixVec2.Zero;
            s.Fighters[1].Grounded = false;
            Step(s);
            foreach (Prop p in s.Props)
                if (p.Kind == PropKind.Shot) Assert.Equal((ShotKind.Missile, Fix.Zero, Weapons.Get(WeaponId.RPG).Vx), (p.Shot, p.Velocity.Y, p.Velocity.X));
        }
        Assert.Equal(0, s.Fighters[1].Josh);
        Assert.Equal(Sweet(18), s.Fighters[2].Josh); // a direct hit
        Assert.Equal(1, s.Fighters[0].WeaponSupply);
    }

    [Fact]
    public void AnRpgNobodyStopsBurstsAtTheEndOfItsFlight()
    {
        GameState s = Standing(0);
        Arm(s, 0, WeaponId.RPG);
        Fire(s, WeaponId.RPG);
        Wait(s, 68);
        Assert.Equal(1, Count(s, ShotKind.Missile));
        Wait(s, 2);
        Assert.Equal(0, Count(s, ShotKind.Missile));
    }

    // ---- Boomerangs ----

    [Fact]
    public void TheChappalStingsOnTheWayOutAndComesBackToTheHandWithOneLess()
    {
        GameState s = Standing(0, 300);
        Arm(s, 0, WeaponId.Chappal);
        Fire(s, WeaponId.Chappal);
        Assert.Equal(WeaponId.None, s.Fighters[0].Weapon); // it has left the hand
        Assert.Equal(1, Count(s, ShotKind.Boomerang));
        Fix furthest = Fix.Zero;
        for (int f = 0; f < 100; f++)
        {
            Step(s);
            foreach (Prop p in s.Props)
                if (p.Kind == PropKind.Shot) furthest = Fix.Max(furthest, p.Position.X);
        }
        Assert.True(furthest > Cm(450) && furthest < Cm(600), $"it flies about 5 m: {furthest}");
        Assert.True(s.Fighters[1].Josh >= 5, "it hit on the way out");
        Assert.Equal(WeaponId.Chappal, s.Fighters[0].Weapon); // caught
        Assert.Equal(5, s.Fighters[0].WeaponSupply);
        Assert.Equal(0, s.Fighters[0].Josh);
        Assert.Equal(0, Count(s, ShotKind.Boomerang));
    }

    // ---- Bats ----

    [Fact]
    public void TheCricketBatReturnsAnArrowForFree()
    {
        GameState s = Standing(0, 300);
        Arm(s, 1, WeaponId.CricketBat);
        Step(s, Buttons.None, Buttons.Light);
        Wait(s, Weapons.Get(WeaponId.CricketBat).Startup - 1);
        PutShot(s, WeaponId.Bow, 100, 55); // it meets the bat as it comes round
        Step(s);
        Assert.Equal(1, s.Props[^1].Owner);
        Assert.True(s.Props[^1].Velocity.X < Fix.Zero);
        Wait(s, 10);
        Assert.Equal(0, s.Fighters[1].Josh);
        Assert.Equal(10, s.Fighters[0].Josh);       // back into the one who shot it
        Assert.Equal(8, s.Fighters[1].WeaponSupply); // a return costs a bat nothing
    }

    [Fact]
    public void TheRacketReturnsABulletForOneOfItsSupply()
    {
        GameState s = Standing(0, 300);
        Arm(s, 1, WeaponId.BadmintonRacket);
        Step(s, Buttons.None, Buttons.Light);
        Wait(s, Weapons.Get(WeaponId.BadmintonRacket).Startup - 1);
        PutShot(s, WeaponId.Uzi, 200, 70);
        Step(s);
        Assert.Equal(1, s.Props[^1].Owner);
        Assert.True(s.Props[^1].Velocity.X < Fix.Zero);
        Assert.Equal(19, s.Fighters[1].WeaponSupply);
        Wait(s, 10);
        Assert.Equal(0, s.Fighters[1].Josh);
        Assert.Equal(2, s.Fighters[0].Josh);
    }

    [Theory]
    [InlineData(WeaponId.RPG, 40, 22)]  // it bursts: a direct hit on the one who tried
    [InlineData(WeaponId.Bow, 55, 10)]  // 10 damage: too heavy for a racket
    public void TheRacketLetsAnythingHeavyThrough(WeaponId shot, int vxCm, int damage)
    {
        GameState s = Standing(0, 300);
        Arm(s, 1, WeaponId.BadmintonRacket);
        Step(s, Buttons.None, Buttons.Light);
        Wait(s, Weapons.Get(WeaponId.BadmintonRacket).Startup - 1);
        PutShot(s, shot, 200, vxCm);
        Wait(s, 3);
        Assert.Equal(damage, s.Fighters[1].Josh);
        Assert.Equal(20, s.Fighters[1].WeaponSupply);
        Assert.Equal(0, s.Fighters[0].Josh);
    }

    [Fact]
    public void AnEmptyRacketReturnsNothing()
    {
        GameState s = Standing(0, 300);
        Arm(s, 1, WeaponId.BadmintonRacket, 1);
        s.Fighters[1].Move = Weapons.Get(WeaponId.BadmintonRacket).Move; // mid-swing with its last one just spent on a hit
        s.Fighters[1].Action = FighterAction.Attack;
        s.Fighters[1].ActionFrame = Weapons.Get(WeaponId.BadmintonRacket).Startup - 1;
        s.Fighters[1].WeaponSupply = 0;
        PutShot(s, WeaponId.Uzi, 200, 70);
        Wait(s, 3);
        Assert.Equal(2, s.Fighters[1].Josh);
    }

    // ---- Shoves (2.2) ----

    /// <summary>
    /// The defender stands 3 m inside the right ledge at this Josh; the attacker, at the weapon's
    /// range to its left, fires once. Afterwards the defender plays to get back: off the stage what
    /// the CPU presses (its air jump, the recovery, an air dodge), over it a drift to the middle.
    /// True if it loses the stock.
    /// </summary>
    private static bool ShoveKnocksOut(StageDefinition stage, WeaponId w, int rangeCm, int josh, out Fix launch)
    {
        var rules = new MatchRules { Stage = stage, Weapons = true };
        FixRect floor = stage.Platforms[0];
        Fix x = floor.MaxX - Fix.FromInt(3);
        GameState s = Simulation.CreateInitialState(rules, 2, 1);
        s.PropTimer = 1_000_000;
        ref Fighter a = ref s.Fighters[0];
        a.Position = new FixVec2(x - Cm(rangeCm), floor.MaxY);
        a.Velocity = FixVec2.Zero;
        a.Grounded = true;
        a.Facing = 1;
        a.Weapon = w;
        a.WeaponSupply = Weapons.Get(w).Supply;
        ref Fighter d = ref s.Fighters[1];
        d.Position = new FixVec2(x, floor.MaxY);
        d.Velocity = FixVec2.Zero;
        d.Grounded = true;
        d.Facing = -1;
        d.Josh = josh;

        var inputs = new Buttons[2];
        inputs[0] = Buttons.Light;
        Simulation.Advance(s, rules, inputs);
        inputs[0] = Buttons.None;
        bool hit = false;
        launch = Fix.Zero;
        for (int f = 0; f < 900; f++)
        {
            Fighter def = s.Fighters[1];
            if (def.Stocks < rules.Stocks) return true;
            if (def.Action == FighterAction.Hitstun && !hit)
            {
                hit = true;
                launch = def.Velocity.X * def.Velocity.X + def.Velocity.Y * def.Velocity.Y; // the shove, squared
            }
            if (hit && def.Grounded && def.Action == FighterAction.Idle && def.Velocity.X == Fix.Zero) return false; // back on its feet
            bool off = def.Position.X < floor.MinX || def.Position.X > floor.MaxX || (!def.Grounded && def.Position.Y < floor.MaxY);
            bool acts = def.Action is FighterAction.Idle or FighterAction.Attack;
            inputs[1] = !hit || !acts ? Buttons.None
                : off ? SparringBot.Think(s, rules, 1, CpuLevel.Hard)
                : def.Position.X * 2 > floor.MinX + floor.MaxX ? Buttons.Left : Buttons.Right;
            Simulation.Advance(s, rules, inputs);
        }
        Assert.True(hit, $"{w} never reached the defender");
        return s.Fighters[1].Stocks < rules.Stocks;
    }

    public static IEnumerable<object[]> Shoves()
    {
        foreach (string stage in new[] { "graybox", "rooftops" })
        {
            yield return new object[] { stage, WeaponId.ConfettiCannon, 150 };
            yield return new object[] { stage, WeaponId.ConfettiCannon, 350 };
            yield return new object[] { stage, WeaponId.SpringGloveLauncher, 150 };
            yield return new object[] { stage, WeaponId.SpringGloveLauncher, 550 };
            yield return new object[] { stage, WeaponId.Charpai, 120 }; // batch 4: the Charpai's swing and the Parat's bash
            yield return new object[] { stage, WeaponId.Parat, 100 };
            yield return new object[] { stage, WeaponId.SonicSpeaker, 300 }; // batch 5: the speaker's pulse
            yield return new object[] { stage, WeaponId.SonicSpeaker, 550 };
        }
    }

    [Theory]
    [MemberData(nameof(Shoves))]
    public void AShoveNeverKnocksOutAFreshFighterThreeMetresInsideALedge(string stageId, WeaponId w, int rangeCm)
    {
        StageDefinition stage = stageId == "graybox" ? Stages.Graybox : ArenaStages.ById(stageId)!;
        Assert.False(ShoveKnocksOut(stage, w, rangeCm, josh: 0, out Fix launch), $"{w} knocked out a fighter at 0 Josh on {stageId}");
        Assert.True(launch > Fix.Zero);
    }

    [Fact]
    public void TheSpringGloveGivesTheSameShoveAtAnyJosh()
    {
        ShoveKnocksOut(Stages.Graybox, WeaponId.SpringGloveLauncher, 300, josh: 0, out Fix fresh);
        ShoveKnocksOut(Stages.Graybox, WeaponId.SpringGloveLauncher, 300, josh: 150, out Fix worn);
        // Launch speeds within 10% of each other: their squares within 21%.
        Assert.True(worn >= fresh && worn * 100 <= fresh * 121, $"0 Josh: {fresh}, 150 Josh: {worn} (speed squared)");

        // A swing of about the same damage, for scale: its launch more than doubles.
        ShoveKnocksOut(Stages.Graybox, WeaponId.Talwar, 120, josh: 0, out Fix lightSwing);
        ShoveKnocksOut(Stages.Graybox, WeaponId.Talwar, 120, josh: 150, out Fix heavySwing);
        Assert.True(heavySwing > lightSwing * 4);
    }

    // ---- Every row of the batch ----

    [Fact]
    public void TheThirtyOnePlainWeaponsAreInTheCrates()
    {
        WeaponId[] batch =
        {
            WeaponId.BoxingGloves, WeaponId.Slingshot, WeaponId.Plank, WeaponId.NinjaStars, WeaponId.StickGrenades, WeaponId.Chappal,
            WeaponId.PressureCooker, WeaponId.SilBatta, WeaponId.CricketBat, WeaponId.BadmintonRacket, WeaponId.Pitchfork, WeaponId.Trout,
            WeaponId.Gada, WeaponId.Talwar, WeaponId.Katar, WeaponId.Katana, WeaponId.Broadsword, WeaponId.Trident, WeaponId.Crossbow,
            WeaponId.SniperRifle, WeaponId.Sledgehammer, WeaponId.Magnum357, WeaponId.TommyGun, WeaponId.AK47, WeaponId.BurstRifle,
            WeaponId.RPG, WeaponId.RayGun, WeaponId.Minigun, WeaponId.Dhol, WeaponId.ConfettiCannon, WeaponId.SpringGloveLauncher,
        };
        Assert.Equal(31, batch.Distinct().Count());
        Assert.All(batch, id => Assert.Contains(id, Weapons.CrateList));
        Assert.True(Weapons.CrateList.Length >= 12 + 31);
    }
}
