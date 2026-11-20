using Rangbaazi.Sim.Diagnostics;
using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim.Tests;

/// <summary>
/// Weapons (docs/design/combat-spec-v0.3-weapons.md): fast Prop Boxes, touch to take and swap,
/// each weapon's ability, throws, self-hits, sweet spots, the signature bonus, and weapons coming
/// back in a box after a fall. Most tests use a very wide floor and hold back the box timer.
/// </summary>
public class WeaponTests
{
    private static readonly MatchRules Rules = new() { Stage = TestStages.Wide, Weapons = true };

    /// <summary>Fighters standing at these x positions (metres x 100), P1 facing right, the rest left; no boxes due.</summary>
    private static GameState Standing(params int[] xCm) => Standing(Rules, xCm);

    private static GameState Standing(MatchRules rules, params int[] xCm)
    {
        var s = Simulation.CreateInitialState(rules, xCm.Length, 1);
        for (int i = 0; i < xCm.Length; i++)
        {
            ref Fighter f = ref s.Fighters[i];
            f.Position = new FixVec2(Fix.FromRatio(xCm[i], 100), Fix.Zero);
            f.Velocity = FixVec2.Zero;
            f.Grounded = true;
            f.Facing = i == 0 ? 1 : -1;
        }
        s.PropTimer = 1_000_000; // no boxes unless a test asks for them
        return s;
    }

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

    private static void Wait(GameState s, int frames)
    {
        for (int i = 0; i < frames; i++) Step(s);
    }

    private static int Count(GameState s, PropKind kind, ShotKind shot = ShotKind.None) =>
        s.Props.Count(p => p.Kind == kind && (shot == ShotKind.None || p.Shot == shot));

    private static int PutBox(GameState s, WeaponId w, int xCm, int supply = -1)
    {
        int slot = Array.FindIndex(s.Props, p => p.Kind == PropKind.None);
        s.Props[slot] = new Prop
        {
            Kind = PropKind.Box,
            Weapon = w,
            Supply = supply >= 0 ? supply : Weapons.Get(w).Supply,
            Owner = -1,
            Position = new FixVec2(Fix.FromRatio(xCm, 100), Fix.Zero),
            Grounded = true,
        };
        return slot;
    }

    // ---- Prop Boxes: fast -----------------------------------------------------------------------

    [Fact]
    public void TheFirstBoxDropsInOneAndAHalfSecondsAndLandsQuickly()
    {
        var s = Simulation.CreateInitialState(new MatchRules { Stage = Stages.Rooftops, Weapons = true }, 2, 5);
        var rules = new MatchRules { Stage = Stages.Rooftops, Weapons = true };
        int dropped = -1, landed = -1;
        for (int f = 0; f < 300 && landed < 0; f++)
        {
            Simulation.Advance(s, rules, new Buttons[2]);
            int box = Array.FindIndex(s.Props, p => p.Kind == PropKind.Box);
            if (box >= 0 && dropped < 0) dropped = f;
            if (box >= 0 && s.Props[box].Grounded) landed = f;
        }
        Assert.Equal(Weapons.FirstBoxFrames - 1, dropped);
        Assert.InRange(landed - dropped, 1, 60); // lands within a second
    }

    [Fact]
    public void BoxesKeepComingEveryTwoAndAHalfToFourAndAHalfSeconds()
    {
        var rules = new MatchRules { Stage = TestStages.Wide, Weapons = true };
        var s = Simulation.CreateInitialState(rules, 2, 9);
        var drops = new List<int>();
        for (int f = 0; f < 60 * 30; f++)
        {
            int timer = s.PropTimer;
            Simulation.Advance(s, rules, new Buttons[2]);
            if (s.PropTimer > timer) drops.Add(f); // the timer restarts when a box drops
            Assert.True(Count(s, PropKind.Box) <= Weapons.Cap(2), $"too many boxes at frame {f}");
        }
        Assert.True(drops.Count >= 7, $"only {drops.Count} boxes in 30 s");
        // Never sooner than the interval; later only while the cap was full (the timer waits then).
        for (int i = 1; i < drops.Count; i++) Assert.True(drops[i] - drops[i - 1] >= Weapons.BoxIntervalMin);
        Assert.True((drops[^1] - drops[0]) / (drops.Count - 1) <= Weapons.BoxIntervalMax, "boxes came too slowly");
    }

    [Fact]
    public void NoBoxDropsWhileTheCapIsFull()
    {
        GameState s = Standing(-1000, 1000);
        for (int i = 0; i < Weapons.Cap(2); i++) PutBox(s, WeaponId.Belan, i * 200);
        s.PropTimer = 1;
        Wait(s, 100);
        Assert.Equal(Weapons.Cap(2), Count(s, PropKind.Box));
        Assert.Equal(1, s.PropTimer); // the timer waits
        Assert.True(Weapons.Cap(15) == 8 && Weapons.Cap(2) == 4);
    }

    [Fact]
    public void ABoxNeverHoldsTheSameWeaponTwiceInARow()
    {
        var rules = new MatchRules { Stage = TestStages.Wide, Weapons = true };
        var s = Simulation.CreateInitialState(rules, 2, 3);
        var seen = new List<WeaponId>();
        for (int f = 0; f < 60 * 180; f++)
        {
            WeaponId last = s.LastBoxWeapon;
            Simulation.Advance(s, rules, new Buttons[2]);
            if (s.LastBoxWeapon != last) seen.Add(s.LastBoxWeapon);
            for (int i = 0; i < s.Props.Length; i++) s.Props[i] = default; // keep under the cap
        }
        Assert.True(seen.Count > 10);
        for (int i = 1; i < seen.Count; i++) Assert.NotEqual(seen[i - 1], seen[i]);
        Assert.Equal(Weapons.Count - 1, seen.Distinct().Count()); // every weapon turns up
    }

    [Fact]
    public void BoxesVanishAfterTwelveSeconds()
    {
        GameState s = Standing(-1000);
        PutBox(s, WeaponId.Uzi, 0);
        Wait(s, Weapons.ItemLifeFrames - 1);
        Assert.Equal(1, Count(s, PropKind.Box));
        Step(s);
        Assert.Equal(0, Count(s, PropKind.Box));
    }

    // ---- Holding -------------------------------------------------------------------------------

    [Fact]
    public void TouchingABoxTakesItsWeapon()
    {
        GameState s = Standing(0);
        PutBox(s, WeaponId.Shotgun, 50);
        Step(s);
        Assert.Equal(WeaponId.Shotgun, s.Fighters[0].Weapon);
        Assert.Equal(4, s.Fighters[0].WeaponSupply);
        Assert.Equal(0, Count(s, PropKind.Box));
    }

    [Fact]
    public void TouchingAnotherWeaponSwapsAndTheOldOneIsFreeForAnyoneElse()
    {
        GameState s = Standing(0, 300);
        Arm(s, 0, WeaponId.Uzi, 17);
        PutBox(s, WeaponId.Belan, 30);
        Step(s);
        Assert.Equal(WeaponId.Belan, s.Fighters[0].Weapon);
        int dropped = Array.FindIndex(s.Props, p => p.Kind == PropKind.Item);
        Assert.Equal(WeaponId.Uzi, s.Props[dropped].Weapon);
        Assert.Equal(17, s.Props[dropped].Supply);

        // It ignores the one who dropped it for a second ...
        Wait(s, 30);
        Assert.Equal(WeaponId.Belan, s.Fighters[0].Weapon);
        // ... but anyone else takes it at once.
        s.Fighters[1].Position = s.Props[dropped].Position;
        Step(s);
        Assert.Equal(WeaponId.Uzi, s.Fighters[1].Weapon);
        Assert.Equal(17, s.Fighters[1].WeaponSupply);
    }

    [Fact]
    public void YouKeepYourWeaponWhenHit()
    {
        GameState s = Standing(0, 100);
        Arm(s, 1, WeaponId.BaseballBat);
        Step(s, Buttons.Light);
        Wait(s, 10);
        Assert.True(s.Fighters[1].Josh > 0);
        Assert.Equal(WeaponId.BaseballBat, s.Fighters[1].Weapon);
    }

    [Fact]
    public void TheRecoveryStaysUpHeavyWhileArmed()
    {
        GameState s = Standing(0);
        Arm(s, 0, WeaponId.Shotgun);
        Step(s, Buttons.Up | Buttons.Heavy);
        Assert.Equal(MoveId.UpHeavy, s.Fighters[0].Move);
        Assert.Equal(WeaponId.Shotgun, s.Fighters[0].Weapon);
    }

    [Fact]
    public void TheBowlingBallSlowsItsCarrier()
    {
        GameState plain = Standing(0), heavy = Standing(0);
        Arm(heavy, 0, WeaponId.BowlingBall);
        for (int i = 0; i < 60; i++)
        {
            Step(plain, Buttons.Right);
            Step(heavy, Buttons.Right);
        }
        Assert.Equal(Rules.Tuning.RunSpeed * 4 / 5, heavy.Fighters[0].Velocity.X);
        Assert.Equal(Rules.Tuning.RunSpeed, plain.Fighters[0].Velocity.X);
    }

    [Fact]
    public void DyingWithAWeaponSendsItBackInABoxWithWhatWasLeft()
    {
        var rules = new MatchRules { Stage = Stages.Graybox, Weapons = true };
        GameState s = Standing(rules, 0, 300);
        Arm(s, 0, WeaponId.CherryBombs, 2);
        s.Fighters[0].Position = new FixVec2(Fix.Zero, Fix.FromInt(-30)); // below the blast zone
        Step(s, rules);
        Assert.Equal(WeaponId.None, s.Fighters[0].Weapon);
        Assert.Equal(1, Count(s, PropKind.PendingBox));
        for (int i = 0; i < Weapons.ReturnFrames; i++) Step(s, rules);
        int box = Array.FindIndex(s.Props, p => p.Kind == PropKind.Box);
        Assert.True(box >= 0);
        Assert.Equal(WeaponId.CherryBombs, s.Props[box].Weapon);
        Assert.Equal(2, s.Props[box].Supply);
    }

    // ---- Each weapon's ability -----------------------------------------------------------------

    [Fact]
    public void TheUziFiresWhileHeldAndStopsWhenReleased()
    {
        GameState s = Standing(0);
        Arm(s, 0, WeaponId.Uzi);
        for (int i = 0; i < 30; i++) Step(s, Buttons.Light);
        Assert.Equal(30 - 6, s.Fighters[0].WeaponSupply); // shots on frames 3, 8, 13, 18, 23, 28
        Wait(s, 20);
        Assert.Equal(24, s.Fighters[0].WeaponSupply);
        Assert.Equal(FighterAction.Idle, s.Fighters[0].Action);
    }

    [Fact]
    public void UziBulletsChipWithoutFreezingTheTarget()
    {
        GameState s = Standing(0, 500);
        Arm(s, 0, WeaponId.Uzi);
        for (int i = 0; i < 12; i++)
        {
            Step(s, Buttons.Light);
            Assert.Equal(0, s.Fighters[1].HitStop);
        }
        Assert.Equal(2, s.Fighters[1].Josh);
    }

    [Fact]
    public void TheBowlingBallRollsThroughEveryoneThenLiesThereForAnyone()
    {
        GameState s = Standing(0, 300, 500);
        s.Fighters[2].Facing = -1;
        Arm(s, 0, WeaponId.BowlingBall);
        Step(s, Buttons.Light);
        Assert.Equal(WeaponId.BowlingBall, s.Fighters[0].Weapon);
        Wait(s, Weapons.BallRoll.Startup);
        Assert.Equal(WeaponId.None, s.Fighters[0].Weapon); // it left the hand
        Assert.Equal(1, Count(s, PropKind.Shot, ShotKind.Roll));
        Wait(s, 40);
        Assert.Equal(14, s.Fighters[1].Josh);
        Assert.Equal(14 * Weapons.SweetPercent / 100, s.Fighters[2].Josh); // "Strike!": the second pin is a sweet spot
        Wait(s, Weapons.RollFrames);
        Assert.Equal(1, Count(s, PropKind.Item));
        Assert.Equal(WeaponId.BowlingBall, s.Props.First(p => p.Kind == PropKind.Item).Weapon);
    }

    [Fact]
    public void TheShotgunIsStrongestPointBlankWeakFarAndKicksTheShooterBack()
    {
        GameState near = Standing(0, 100), far = Standing(0, 400);
        Arm(near, 0, WeaponId.Shotgun);
        Arm(far, 0, WeaponId.Shotgun);
        Step(near, Buttons.Heavy);
        Step(far, Buttons.Heavy);
        Wait(near, 12);
        Wait(far, 12);
        Assert.Equal(18 * Weapons.SweetPercent / 100, near.Fighters[1].Josh);
        Assert.Equal(Weapons.ShotgunFarDamage, far.Fighters[1].Josh);
        Assert.True(far.Fighters[0].Velocity.X < Fix.Zero, "the blast pushes the shooter back");
        Assert.Equal(3, near.Fighters[0].WeaponSupply);
    }

    [Fact]
    public void CherryBombsBurstOnTheFighterTheyTouch()
    {
        GameState s = Standing(0, 600);
        Arm(s, 0, WeaponId.CherryBombs);
        Step(s, Buttons.Light);
        Wait(s, 60);
        Assert.Equal(12 * Weapons.SweetPercent / 100, s.Fighters[1].Josh); // a direct hit
        Assert.Equal(0, s.Fighters[0].Josh);
        Assert.Equal(2, s.Fighters[0].WeaponSupply);
    }

    [Fact]
    public void ABombsBlastHitsItsThrowerToo()
    {
        GameState s = Standing(0);
        s.Props[0] = new Prop
        {
            Kind = PropKind.Shot, Shot = ShotKind.Bomb, Weapon = WeaponId.CherryBombs, Owner = 0,
            Position = new FixVec2(Fix.Half, Fix.Zero), Grounded = true, Timer = 1,
        };
        Step(s);
        Assert.Equal(12, s.Fighters[0].Josh);
        Assert.Equal(FighterAction.Hitstun, s.Fighters[0].Action);
        Assert.True(s.Fighters[0].Velocity.X < Fix.Zero, "launched away from the blast");
    }

    [Fact]
    public void ArrowsPierceAndALongShotIsASweetSpot()
    {
        GameState s = Standing(0, 400, 700, 1400);
        Arm(s, 0, WeaponId.Bow);
        Step(s, Buttons.Light);
        Wait(s, 40);
        Assert.Equal(10, s.Fighters[1].Josh);
        Assert.Equal(10, s.Fighters[2].Josh);                               // it went straight through
        Assert.Equal(10 * Weapons.SweetPercent / 100, s.Fighters[3].Josh);  // from far away
        Assert.Equal(5, s.Fighters[0].WeaponSupply);
    }

    [Fact]
    public void TheBelanDizziesLongerWhenItBonksSomeoneMidAttack()
    {
        GameState calm = Standing(0, 100), busy = Standing(0, 100);
        Arm(calm, 0, WeaponId.Belan);
        Arm(busy, 0, WeaponId.Belan);
        Step(calm, Buttons.Light);
        Step(busy, Buttons.Light, Buttons.Heavy); // P2 starts a slow heavy at the same moment
        Wait(calm, Weapons.BelanBonk.Startup);
        Wait(busy, Weapons.BelanBonk.Startup);
        Assert.Equal(8, calm.Fighters[1].Josh);
        Assert.Equal(8 + 1 + 10, calm.Fighters[1].HitstunFrames + 0); // 8 + Josh/8 + the dizzy bonus
        Assert.Equal(8 * Weapons.SweetPercent / 100, busy.Fighters[1].Josh);
        Assert.Equal(8 + 1 + 10 + 6, busy.Fighters[1].HitstunFrames);
        Assert.Equal(11, calm.Fighters[0].WeaponSupply);
    }

    [Fact]
    public void TheBatsTipHitsASixFlatAndFar()
    {
        GameState tip = Standing(0, 190), body = Standing(0, 100);
        Arm(tip, 0, WeaponId.BaseballBat);
        Arm(body, 0, WeaponId.BaseballBat);
        Step(tip, Buttons.Light);
        Step(body, Buttons.Light);
        Wait(tip, Weapons.BatSwing.Startup);
        Wait(body, Weapons.BatSwing.Startup);
        Assert.Equal(16 * Weapons.SweetPercent / 100, tip.Fighters[1].Josh);
        Assert.Equal(16, body.Fighters[1].Josh);
        Fighter t = tip.Fighters[1], b = body.Fighters[1];
        Assert.True(t.Velocity.Y * b.Velocity.X < b.Velocity.Y * t.Velocity.X, "the tip launches flatter");
    }

    [Fact]
    public void TheBatBatsABulletBackAtItsShooter()
    {
        GameState s = Standing(0, 300);
        Arm(s, 0, WeaponId.Uzi);
        Arm(s, 1, WeaponId.BaseballBat);
        s.Fighters[1].Facing = -1;
        Step(s, Buttons.None, Buttons.Light);       // the batter starts swinging first ...
        Wait(s, 8);
        Step(s, Buttons.Light);                     // ... so one bullet meets the bat as it comes round
        for (int i = 0; i < 40; i++) Step(s);
        Assert.Equal(0, s.Fighters[1].Josh);        // batted away, it never touched the batter
        Assert.Equal(2, s.Fighters[0].Josh);        // and it hit the one who fired it
    }

    // ---- Throwing --------------------------------------------------------------------------------

    [Fact]
    public void DropWeaponThrowsItOnceAndItIsGone()
    {
        GameState s = Standing(0, 300);
        Arm(s, 0, WeaponId.Shotgun);
        Step(s, Buttons.DropWeapon);
        Wait(s, 30);
        Assert.Equal(WeaponId.None, s.Fighters[0].Weapon);
        Assert.Equal(9, s.Fighters[1].Josh); // a medium throw
        Assert.Equal(0, s.Props.Count(p => p.Kind != PropKind.None));
    }

    [Fact]
    public void AnEmptyWeaponIsThrownByTheAttackButtons()
    {
        GameState s = Standing(0);
        Arm(s, 0, WeaponId.Uzi, 0);
        Step(s, Buttons.Light);
        Assert.Equal(MoveId.WeaponThrow, s.Fighters[0].Move);
    }

    [Fact]
    public void AThrownWeaponHittingSomeoneInTheAirIsABullseye()
    {
        GameState s = Standing(0, 300);
        Arm(s, 0, WeaponId.BowlingBall);
        s.Fighters[1].Position = new FixVec2(Fix.FromInt(3), Fix.FromRatio(1, 2));
        s.Fighters[1].Grounded = false;
        s.Fighters[1].Velocity = new FixVec2(Fix.Zero, Fix.FromRatio(1, 10));
        Step(s, Buttons.DropWeapon);
        Wait(s, 12);
        Assert.Equal(12 * Weapons.SweetPercent / 100, s.Fighters[1].Josh);
    }

    // ---- Your own weapon ----------------------------------------------------------------------------

    [Fact]
    public void AFighterHitsALittleHarderWithTheirOwnWeapon()
    {
        var own = new MatchRules { Stage = TestStages.Wide, Weapons = true, Signatures = new[] { WeaponId.BaseballBat, WeaponId.None } };
        var other = new MatchRules { Stage = TestStages.Wide, Weapons = true, Signatures = new[] { WeaponId.Bow, WeaponId.None } };
        GameState a = Standing(own, 0, 100), b = Standing(other, 0, 100);
        Arm(a, 0, WeaponId.BaseballBat);
        Arm(b, 0, WeaponId.BaseballBat);
        Step(a, own, Buttons.Light);
        Step(b, other, Buttons.Light);
        for (int i = 0; i < Weapons.BatSwing.Startup; i++)
        {
            Step(a, own);
            Step(b, other);
        }
        Assert.Equal(16, b.Fighters[1].Josh);
        Assert.Equal(16 * Weapons.SignaturePercent / 100, a.Fighters[1].Josh); // 17: a small edge, not a big one
    }

    [Fact]
    public void EachWeaponHasAnOwnerInTheCast()
    {
        Assert.Equal(WeaponId.Bow, Weapons.SignatureFor("suhani"));
        Assert.Equal(WeaponId.Belan, Weapons.SignatureFor("akshat"));
        Assert.Equal(WeaponId.None, Weapons.SignatureFor("nobody"));
        var owners = new[] { "akshat", "shubham", "shahn", "salik", "rishabh", "ankit", "suhani" }.Select(Weapons.SignatureFor);
        Assert.Equal(7, owners.Distinct().Count()); // the five newer weapons have no owner yet
    }

    // ---- The newer weapons: boomerang, trampoline, mines, homing missile, black-hole bomb ----------

    private static Fix Cm(int cm) => Fix.FromRatio(cm, 100);

    [Fact]
    public void TheBoomerangHitsOnItsWayOutAndComesBackToTheHand()
    {
        GameState s = Standing(0, 500);
        Arm(s, 0, WeaponId.Boomerang);
        Step(s, Buttons.Light);
        Wait(s, 12);
        Assert.Equal(WeaponId.None, s.Fighters[0].Weapon); // it has left the hand
        Assert.Equal(1, Count(s, PropKind.Shot, ShotKind.Boomerang));
        Wait(s, 120);
        Assert.True(s.Fighters[1].Josh >= 9, "it hit on the way out");
        Assert.Equal(0, s.Fighters[0].Josh);
        Assert.Equal(WeaponId.Boomerang, s.Fighters[0].Weapon); // caught
        Assert.Equal(3, s.Fighters[0].WeaponSupply);
        Assert.Equal(0, Count(s, PropKind.Shot));
    }

    [Fact]
    public void ABoomerangComingBackIsASweetSpot()
    {
        GameState s = Standing(0, 300);
        s.Props[0] = new Prop
        {
            Kind = PropKind.Shot, Shot = ShotKind.Boomerang, Weapon = WeaponId.Boomerang, Owner = 0, OwnerClear = true,
            Position = new FixVec2(Cm(420), Cm(70)), Velocity = new FixVec2(-Weapons.BoomerangSpeed, Fix.Zero), Timer = 1, Supply = 2,
        };
        Wait(s, 4);
        Assert.Equal(8 * Weapons.SweetPercent / 100, s.Fighters[1].Josh);
        Assert.True(s.Fighters[1].Velocity.X < Fix.Zero, "knocked the way it was flying");
    }

    [Fact]
    public void ABoomerangNobodyCatchesLiesThereWithItsThrowsLeft()
    {
        GameState s = Standing(0);
        Arm(s, 0, WeaponId.Belan); // hands full: it can't be caught
        s.Props[0] = new Prop
        {
            Kind = PropKind.Shot, Shot = ShotKind.Boomerang, Weapon = WeaponId.Boomerang, Owner = 0, OwnerClear = true,
            Position = new FixVec2(Cm(6000), Cm(70)), Timer = 1, Supply = 2, Age = Weapons.BoomerangFrames - 1,
        };
        s.Props[1] = s.Props[0] with { Supply = 0, Position = new FixVec2(Cm(7000), Cm(70)) };
        Wait(s, 2);
        Assert.Equal(0, Count(s, PropKind.Shot));
        Prop left = Assert.Single(s.Props, p => p.Kind == PropKind.Item); // the empty one is gone
        Assert.Equal(WeaponId.Boomerang, left.Weapon);
        Assert.Equal(2, left.Supply);
    }

    [Fact]
    public void ATrampolineBouncesAnyoneOnItButNotItsOwnerUntilTheyStepOff()
    {
        GameState s = Standing(0, 800);
        Arm(s, 0, WeaponId.Trampoline);
        Step(s, Buttons.Light);
        Wait(s, 30);
        Assert.Equal(1, Count(s, PropKind.Shot, ShotKind.Pad));
        Assert.Equal(2, s.Fighters[0].WeaponSupply);
        Assert.True(s.Fighters[0].Grounded, "the one who put it down is not thrown by it");

        int pad = Array.FindIndex(s.Props, p => p.Shot == ShotKind.Pad);
        s.Fighters[1].Position = new FixVec2(s.Props[pad].Position.X, Fix.Zero);
        Step(s);
        Assert.Equal(Weapons.PadLaunch, s.Fighters[1].Velocity.Y);
        Assert.False(s.Fighters[1].Grounded);
        Assert.Equal(0, s.Fighters[1].Josh); // it never hurts

        Wait(s, Weapons.PadFrames);
        Assert.Equal(0, Count(s, PropKind.Shot, ShotKind.Pad)); // gone after 10 s
    }

    [Fact]
    public void AMineArmsAfterAMomentThenBurstsOnWhoeverTouchesIt()
    {
        GameState s = Standing(0, 600);
        s.Props[0] = new Prop
        {
            Kind = PropKind.Shot, Shot = ShotKind.Mine, Weapon = WeaponId.Mines, Owner = 0, OwnerClear = true,
            Position = new FixVec2(Cm(600), Fix.Zero), Grounded = true, Timer = Weapons.MineArmFrames,
        };
        Wait(s, Weapons.MineArmFrames - 2);
        Assert.Equal(0, s.Fighters[1].Josh); // not armed yet
        Wait(s, 3);
        Assert.Equal(16 * Weapons.SweetPercent / 100, s.Fighters[1].Josh); // stepping on it is a direct hit
        Assert.Equal(0, Count(s, PropKind.Shot));
    }

    [Fact]
    public void AMineBurstsOnItsPlanterToo()
    {
        GameState s = Standing(0);
        s.Props[0] = new Prop
        {
            Kind = PropKind.Shot, Shot = ShotKind.Mine, Weapon = WeaponId.Mines, Owner = 0, OwnerClear = true,
            Position = FixVec2.Zero, Grounded = true,
        };
        Step(s);
        Assert.Equal(16, s.Fighters[0].Josh);
    }

    [Fact]
    public void AMineNobodyFoundBurstsByItselfAfterFifteenSeconds()
    {
        GameState s = Standing(0, 150);
        s.Props[0] = new Prop
        {
            Kind = PropKind.Shot, Shot = ShotKind.Mine, Weapon = WeaponId.Mines, Owner = 0, OwnerClear = true,
            Position = new FixVec2(Cm(260), Fix.Zero), Grounded = true, Age = Weapons.MineFrames - 2,
        };
        Step(s);
        Assert.Equal(1, Count(s, PropKind.Shot, ShotKind.Mine));
        Wait(s, 2);
        Assert.Equal(0, Count(s, PropKind.Shot));
        Assert.Equal(16, s.Fighters[1].Josh); // within its blast, but not standing on it: no sweet spot
        Assert.Equal(0, s.Fighters[0].Josh);
    }

    [Fact]
    public void PlantingAMineDoesNotSetItOffUnderThePlanter()
    {
        GameState s = Standing(0);
        Arm(s, 0, WeaponId.Mines);
        Step(s, Buttons.Light);
        Wait(s, Weapons.MineArmFrames + 60);
        Assert.Equal(1, Count(s, PropKind.Shot, ShotKind.Mine));
        Assert.Equal(0, s.Fighters[0].Josh);
        Assert.Equal(2, s.Fighters[0].WeaponSupply);
    }

    [Fact]
    public void TheHomingMissileTurnsRoundToFindSomeoneBehindTheShooter()
    {
        GameState s = Standing(0, -600);
        Arm(s, 0, WeaponId.HomingMissile);
        Step(s, Buttons.Light); // facing right, away from them
        Wait(s, 200);
        Assert.Equal(15 * Weapons.SweetPercent / 100, s.Fighters[1].Josh);
        Assert.Equal(0, s.Fighters[0].Josh);
        Assert.Equal(1, s.Fighters[0].WeaponSupply);
        Assert.Equal(0, Count(s, PropKind.Shot));
    }

    [Fact]
    public void AMissileWithNobodyToChaseBurstsAfterFourSeconds()
    {
        GameState s = Standing(0);
        Arm(s, 0, WeaponId.HomingMissile);
        Step(s, Buttons.Light);
        Wait(s, 100);
        Assert.Equal(1, Count(s, PropKind.Shot, ShotKind.Missile));
        Wait(s, Weapons.MissileFrames);
        Assert.Equal(0, Count(s, PropKind.Shot));
        Assert.Equal(0, s.Fighters[0].Josh);
    }

    [Fact]
    public void TheBlackHolePullsEveryoneInThenBursts()
    {
        GameState s = Standing(3000, 300, -300);
        s.Props[0] = new Prop
        {
            Kind = PropKind.Shot, Shot = ShotKind.Hole, Weapon = WeaponId.BlackHoleBomb, Owner = 0, OwnerClear = true,
            Position = FixVec2.Zero, Grounded = true, Timer = Weapons.HoleFuse,
        };
        Wait(s, Weapons.HoleFuse - Weapons.HolePullFrames - 2);
        Assert.Equal(Cm(300), s.Fighters[1].Position.X); // not pulling yet
        Wait(s, 32);
        Assert.True(Fix.Abs(s.Fighters[1].Position.X) < Cm(100), "pulled in from the right");
        Assert.True(Fix.Abs(s.Fighters[2].Position.X) < Cm(100), "pulled in from the left");
        Assert.Equal(0, s.Fighters[1].Josh);
        Wait(s, Weapons.HolePullFrames);
        Assert.Equal(20, s.Fighters[1].Josh);
        Assert.Equal(20, s.Fighters[2].Josh);
        Assert.Equal(0, s.Fighters[0].Josh); // 30 m away
        Assert.Equal(0, Count(s, PropKind.Shot));
    }

    [Fact]
    public void RollbackThroughABrawlWithTheNewerWeaponsMatchesTheOriginalTimeline()
    {
        // Four bots handed the five newer weapons in turn, with an 8-frame misprediction every 7 frames.
        var rules = MatchRules.Room(Stages.Rooftops, 1);
        const int frames = 60 * 40;
        var reference = Simulation.CreateInitialState(rules, 4, 91);
        var live = Simulation.CreateInitialState(rules, 4, 91);
        Buttons[] Think(GameState g) => Enumerable.Range(0, 4).Select(p => SparringBot.Think(g, rules, p, CpuLevel.Hard)).ToArray();
        var seen = new int[11];
        for (int f = 0; f < frames; f++)
        {
            if (f % 90 == 0)
                foreach (GameState g in new[] { reference, live })
                    for (int p = 0; p < 4; p++)
                    {
                        if (!g.Fighters[p].IsActive) continue;
                        var w = (WeaponId)((int)WeaponId.Boomerang + (f / 90 + p) % 5);
                        g.Fighters[p].Weapon = w;
                        g.Fighters[p].WeaponSupply = Weapons.Get(w).Supply;
                    }
            Buttons[] inputs = Think(reference);
            Simulation.Advance(reference, rules, inputs);
            foreach (Prop p in reference.Props)
                if (p.Kind == PropKind.Shot) seen[(int)p.Shot]++;

            if (f % 7 == 0)
            {
                GameState saved = live.Clone();
                for (int k = 0; k < 8; k++) Simulation.Advance(live, rules, new Buttons[] { Buttons.Light, Buttons.DropWeapon, Buttons.Right, Buttons.Heavy });
                live.CopyFrom(saved);
            }
            Simulation.Advance(live, rules, inputs);
            Assert.Equal(reference.Checksum(), live.Checksum());
        }
        foreach (ShotKind kind in new[] { ShotKind.Boomerang, ShotKind.Pad, ShotKind.Mine, ShotKind.Missile, ShotKind.Hole })
            Assert.True(seen[(int)kind] > 0, $"no {kind} was ever on the stage");
    }

    // ---- Off, and rollback ----------------------------------------------------------------------------

    [Fact]
    public void WithWeaponsOffNoBoxEverDrops()
    {
        var rules = new MatchRules { Stage = TestStages.Wide };
        var s = Simulation.CreateInitialState(rules, 2, 1);
        for (int i = 0; i < 600; i++) Simulation.Advance(s, rules, new Buttons[2]);
        Assert.All(s.Props, p => Assert.Equal(PropKind.None, p.Kind));
    }

    [Fact]
    public void RollbackThroughAWeaponBrawlMatchesTheOriginalTimeline()
    {
        // Four bots on a room arena with weapons, with an 8-frame misprediction every 7 frames.
        var rules = MatchRules.Room(Stages.Rooftops, 1, new[] { WeaponId.Uzi, WeaponId.Bow, WeaponId.None, WeaponId.BaseballBat });
        const int frames = 60 * 40;
        var reference = Simulation.CreateInitialState(rules, 4, 77);
        var live = Simulation.CreateInitialState(rules, 4, 77);
        var levels = new[] { CpuLevel.Hard, CpuLevel.Normal, CpuLevel.Hard, CpuLevel.Easy };
        Buttons[] Think(GameState g) => Enumerable.Range(0, 4).Select(p => SparringBot.Think(g, rules, p, levels[p])).ToArray();
        int armed = 0, shots = 0;
        for (int f = 0; f < frames; f++)
        {
            Buttons[] inputs = Think(reference);
            Simulation.Advance(reference, rules, inputs);
            armed += reference.Fighters.Count(x => x.Weapon != WeaponId.None);
            shots += Count(reference, PropKind.Shot);

            if (f % 7 == 0)
            {
                GameState saved = live.Clone();
                for (int k = 0; k < 8; k++) Simulation.Advance(live, rules, new Buttons[] { Buttons.Light, Buttons.DropWeapon, Buttons.Right, Buttons.Heavy });
                live.CopyFrom(saved);
            }
            Simulation.Advance(live, rules, inputs);
            Assert.Equal(reference.Checksum(), live.Checksum());
        }
        // Since the sides moved in to the screen's edge (17.2 m), the bots are knocked out about every
        // 5 s here and drop their weapons each time, so they hold one for a few hundred frames.
        Assert.True(armed > 150, $"the bots were armed for only {armed} fighter-frames");
        Assert.True(shots > 100, $"only {shots} shot-frames: the bots hardly fired");
    }
}
