using System.ComponentModel;
using System.Diagnostics;
using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim.Tests;

/// <summary>
/// The generated weapon table (tools/weapons.json, tools/gen_weapons.py; combat spec v0.4, section 1).
/// The twelve weapons of combat spec v0.3 became rows 1 to 12: the first test holds the numbers
/// their hand-written code had, so a row that drifts from them fails here before it moves a golden.
/// </summary>
public class WeaponTableTests
{
    private static Fix R(long n, long d) => Fix.FromRatio(n, d);

    /// <summary>A launch angle as the old code wrote it: a unit vector in ten-thousandths.</summary>
    private static void AssertMove(WeaponId id, MoveId move, int startup, int active, int recovery, int damage, Fix knockback, Fix growth,
        int launchX, int launchY, int hitstun, Fix near = default, Fix far = default, Fix bottom = default, Fix top = default,
        int impulseFrame = 0, Fix lunge = default)
    {
        WeaponRow row = Weapons.Get(id);
        MoveData m = row.MoveData;
        Assert.Equal(id, row.Id);
        Assert.Equal(move, row.Move);
        Assert.Same(m, Weapons.MoveFor(move));
        Assert.Equal(startup, m.Startup);
        Assert.Equal(active, m.Active);
        Assert.Equal(recovery, m.Recovery);
        Assert.Equal(damage, m.Damage);
        Assert.Equal(knockback, m.BaseKnockback);
        Assert.Equal(growth, m.KnockbackGrowth);
        Assert.Equal(R(launchX, 10000), m.LaunchX);
        Assert.Equal(R(launchY, 10000), m.LaunchY);
        Assert.Equal(hitstun, m.HitstunBonus);
        Assert.Equal(near, m.HitboxNear);
        Assert.Equal(far, m.HitboxFar);
        Assert.Equal(bottom, m.HitboxBottom);
        Assert.Equal(top, m.HitboxTop);
        Assert.Equal(impulseFrame, m.ImpulseFrame);
        Assert.Equal(lunge, m.LungeSpeed);
        Assert.False(m.HitsBothSides);
        Assert.Equal(Fix.Zero, m.RiseSpeed);
        Assert.Equal(Fix.Zero, m.PlungeSpeed);
        Assert.Equal(WeaponEffect.None, m.Effect);
        Assert.Equal(0, m.EffectN);
    }

    [Fact]
    public void TheTwelveBuiltRowsEqualTheConstantsOfCombatSpecV03()
    {
        // Frame data, as the MoveData statics of Weapons.cs had it.
        AssertMove(WeaponId.Belan, MoveId.BelanBonk, 7, 3, 14, 8, R(12, 100), R(25, 10000), 6428, 7660, 10, R(1, 5), R(13, 10), R(3, 5), Fix.FromInt(2));
        AssertMove(WeaponId.BaseballBat, MoveId.BatSwing, 12, 4, 20, 16, R(17, 100), R(26, 10000), 8192, 5736, 4, Fix.Zero, R(9, 5), R(2, 5), R(8, 5));
        AssertMove(WeaponId.Shotgun, MoveId.ShotgunBlast, 12, 2, 30, 18, R(20, 100), R(28, 10000), 9063, 4226, 4, R(2, 5), R(9, 2), Fix.Half, R(3, 2), 12, R(-15, 100));
        AssertMove(WeaponId.BowlingBall, MoveId.BallRoll, 14, 0, 20, 14, R(18, 100), R(24, 10000), 8660, 5000, 4);
        AssertMove(WeaponId.Uzi, MoveId.UziFire, 3, 0, 8, 2, R(5, 100), R(5, 10000), 9848, 1736, -6);
        AssertMove(WeaponId.CherryBombs, MoveId.BombLob, 8, 0, 14, 12, R(18, 100), R(25, 10000), 5000, 8660, 4);
        AssertMove(WeaponId.Bow, MoveId.BowShot, 10, 0, 14, 10, R(15, 100), R(24, 10000), 9397, 3420, 2);
        AssertMove(WeaponId.Boomerang, MoveId.BoomerangThrow, 8, 0, 12, 8, R(14, 100), R(22, 10000), 8192, 5736, 2);
        AssertMove(WeaponId.Trampoline, MoveId.PadPlace, 6, 0, 10, 0, Fix.Zero, Fix.Zero, 8192, 5736, 0);
        AssertMove(WeaponId.Mines, MoveId.MinePlant, 6, 0, 8, 16, R(20, 100), R(26, 10000), 5000, 8660, 4);
        AssertMove(WeaponId.HomingMissile, MoveId.MissileFire, 14, 0, 22, 15, R(19, 100), R(27, 10000), 5000, 8660, 4);
        AssertMove(WeaponId.BlackHoleBomb, MoveId.HoleLob, 10, 0, 16, 20, R(25, 100), R(30, 10000), 3420, 9397, 4);

        // The catalogue: supply, what it fires, throw damage.
        (WeaponId Id, int Supply, ShotKind Fires, int Throw)[] catalogue =
        {
            (WeaponId.Belan, 12, ShotKind.None, 6), (WeaponId.BaseballBat, 8, ShotKind.None, 9), (WeaponId.BowlingBall, 1, ShotKind.Roll, 12),
            (WeaponId.Shotgun, 4, ShotKind.None, 9), (WeaponId.Uzi, 30, ShotKind.Bullet, 6), (WeaponId.CherryBombs, 3, ShotKind.Bomb, 9),
            (WeaponId.Bow, 6, ShotKind.Arrow, 6), (WeaponId.Boomerang, 4, ShotKind.Boomerang, 6), (WeaponId.Trampoline, 3, ShotKind.Pad, 9),
            (WeaponId.Mines, 3, ShotKind.Mine, 6), (WeaponId.HomingMissile, 2, ShotKind.Missile, 12), (WeaponId.BlackHoleBomb, 2, ShotKind.Hole, 9),
        };
        Assert.Equal(12, catalogue.Length);
        for (int i = 0; i < catalogue.Length; i++)
        {
            WeaponRow row = Weapons.Get(catalogue[i].Id);
            Assert.Equal(i + 1, (int)row.Id); // the twelve keep their numbers
            Assert.Equal(catalogue[i].Supply, row.Supply);
            Assert.Equal(catalogue[i].Fires, row.Flight);
            Assert.Equal(catalogue[i].Throw, row.Throw);
            Assert.True(row.Built && row.Crate);
            Assert.Equal(row.Id, row.Credit);
            Assert.Equal(2, row.Weight);
            Assert.Equal(catalogue[i].Fires == ShotKind.None ? (row.Id == WeaponId.Shotgun ? WeaponUse.Blast : WeaponUse.Swing) : WeaponUse.Shoot, row.Use);
        }

        // The throw is still a shared move, not a row.
        MoveData thrown = Weapons.MoveFor(MoveId.WeaponThrow)!;
        Assert.Equal((6, 0, 12, 9, 4), (thrown.Startup, thrown.Active, thrown.Recovery, thrown.Damage, thrown.HitstunBonus));
        Assert.Equal((R(16, 100), R(26, 10000), R(8192, 10000), R(5736, 10000)), (thrown.BaseKnockback, thrown.KnockbackGrowth, thrown.LaunchX, thrown.LaunchY));
        Assert.Null(Weapons.RowOfMove(MoveId.WeaponThrow));
        Assert.Equal((R(32, 100), R(6, 100), Fix.One, R(3, 5)), (Weapons.ThrowVx, Weapons.ThrowVy, Weapons.HandHeight, Weapons.HandReach));

        // Belan and bat: melee, a hit of durability each, their sweet spots.
        WeaponRow belan = Weapons.Get(WeaponId.Belan), bat = Weapons.Get(WeaponId.BaseballBat), shotgun = Weapons.Get(WeaponId.Shotgun);
        Assert.Equal((WeaponSpend.Hit, WeaponSweet.Midattack, 6), (belan.Spend, belan.Sweet, belan.SweetStun));
        Assert.Equal((WeaponSpend.Hit, WeaponSweet.Tip, WeaponDefence.Bat), (bat.Spend, bat.Sweet, bat.Defence));
        Assert.Equal(R(13, 10), Fix.FromRaw(bat.SweetArg));                                  // BatTip
        Assert.Equal((R(9397, 10000), R(3420, 10000)), (bat.SweetLaunchX, bat.SweetLaunchY)); // the "six": 20 degrees
        Assert.Equal((Fix.Zero, Fix.Zero, 0), (belan.SweetLaunchX, belan.SweetLaunchY, bat.SweetStun));
        Assert.Equal((WeaponSpend.Use, WeaponSweet.Pointblank, 10), (shotgun.Spend, shotgun.Sweet, shotgun.FarDamage));
        Assert.Equal((R(6, 5), Fix.FromInt(3)), (Fix.FromRaw(shotgun.SweetArg), shotgun.FarDist)); // PointBlank, ShotgunFar

        // The bowling ball: heavy, never runs out, leaves the hand, rests as an item, "Strike!".
        WeaponRow ball = Weapons.Get(WeaponId.BowlingBall);
        Assert.Equal((80, WeaponSpend.Never, true, true, true, true), (ball.Carry, ball.Spend, ball.Leave, ball.Rest, ball.Feet, ball.Sweet == WeaponSweet.Strike));
        Assert.Equal((R(22, 100), Fix.Zero, 180, R(3, 10)), (ball.Vx, ball.Vy, ball.Life, ball.ShotHalf)); // RollSpeed, RollFrames

        // The Uzi: its interval, walking, the chip hits, the spread, the streak.
        WeaponRow uzi = Weapons.Get(WeaponId.Uzi);
        Assert.Equal((5, true, true, WeaponSweet.Streak, 6L), (uzi.Interval, uzi.Walk, uzi.Chip, uzi.Sweet, uzi.SweetArg));
        Assert.Equal((R(70, 100), 20, R(3, 20)), (uzi.Vx, uzi.Life, uzi.ShotHalf)); // BulletSpeed, BulletFrames
        Assert.Equal((WeaponSpreadBy.Burst, 5), (uzi.SpreadBy, uzi.SpreadLen));
        Fix[] spread = { Fix.Zero, R(366, 10000), R(-366, 10000), R(732, 10000), R(-732, 10000) };
        for (int i = 0; i < spread.Length; i++) Assert.Equal(spread[i], WeaponTable.Spreads[uzi.SpreadAt + i]);

        // Cherry bombs, the bow, the boomerang.
        WeaponRow bombs = Weapons.Get(WeaponId.CherryBombs), bow = Weapons.Get(WeaponId.Bow), boomerang = Weapons.Get(WeaponId.Boomerang);
        Assert.Equal((R(14, 100), R(18, 100), R(1, 100), 50, R(1, 5), R(6, 5)), (bombs.Vx, bombs.Vy, bombs.Gravity, bombs.Fuse, bombs.ShotHalf, bombs.BlastHalf));
        Assert.Equal((true, WeaponSweet.Direct), (bombs.Touch, bombs.Sweet));
        Assert.Equal((R(55, 100), R(4, 1000), 50, R(1, 4), true), (bow.Vx, bow.Gravity, bow.Life, bow.ShotHalf, bow.Pierce));
        Assert.Equal((WeaponSweet.Longshot, 20L), (bow.Sweet, bow.SweetArg)); // LongShotFrames
        Assert.Equal((R(50, 100), R(125, 10000), 150, R(3, 10), true, false), (boomerang.Vx, boomerang.Drag, boomerang.Life, boomerang.ShotHalf, boomerang.Leave, boomerang.Rest));
        Assert.Equal((WeaponSweet.Return, R(12, 100)), (boomerang.Sweet, Weapons.BoomerangClimb));

        // The trampoline, the mines, the missile, the black hole.
        WeaponRow pad = Weapons.Get(WeaponId.Trampoline), mine = Weapons.Get(WeaponId.Mines), missile = Weapons.Get(WeaponId.HomingMissile), hole = Weapons.Get(WeaponId.BlackHoleBomb);
        Assert.Equal((600, R(42, 100), R(9, 20), true, WeaponSweet.None), (pad.Fuse, pad.PadLaunch, pad.ShotHalf, pad.Feet, pad.Sweet)); // PadFrames, PadLaunch
        Assert.Equal((45, 900, R(1, 4), R(6, 5), true, WeaponSweet.Direct), (mine.Fuse, mine.Life, mine.ShotHalf, mine.BlastHalf, mine.Feet, mine.Sweet));
        Assert.Equal((R(20, 100), R(8, 1000), 240, R(1, 4), R(6, 5), true), (missile.Vx, missile.Turn, missile.Life, missile.ShotHalf, missile.BlastHalf, missile.Touch));
        Assert.Equal((WeaponSweet.Direct, R(1, 5)), (missile.Sweet, Weapons.MissileLevel));
        Assert.Equal((R(14, 100), R(18, 100), R(1, 100), 110, 80), (hole.Vx, hole.Vy, hole.Gravity, hole.Fuse, hole.PullFrames));
        Assert.Equal((R(9, 2), R(11, 100), R(21, 10), R(1, 4), false), (hole.Reach, hole.Pull, hole.BlastHalf, hole.ShotHalf, hole.Touch));

        // The CPU's ranges: RangedReach, RangedMin and UziBurst of SparringBot.
        (WeaponId Id, Fix Min, Fix Max)[] ranges =
        {
            (WeaponId.Belan, Fix.Zero, Fix.Zero), (WeaponId.BaseballBat, Fix.Zero, Fix.Zero), (WeaponId.BowlingBall, Fix.Zero, Fix.FromInt(12)),
            (WeaponId.Shotgun, Fix.Zero, Fix.FromInt(4)), (WeaponId.Uzi, Fix.Zero, Fix.FromInt(9)), (WeaponId.CherryBombs, R(5, 2), Fix.FromInt(6)),
            (WeaponId.Bow, Fix.Zero, Fix.FromInt(15)), (WeaponId.Boomerang, Fix.Zero, Fix.FromInt(8)), (WeaponId.Trampoline, Fix.Zero, Fix.Zero),
            (WeaponId.Mines, Fix.Zero, Fix.Zero), (WeaponId.HomingMissile, Fix.FromInt(3), Fix.FromInt(14)), (WeaponId.BlackHoleBomb, R(5, 2), Fix.FromInt(6)),
        };
        foreach (var r in ranges) Assert.Equal((r.Min, r.Max), (Weapons.Get(r.Id).CpuMin, Weapons.Get(r.Id).CpuMax));
        Assert.Equal(8, uzi.CpuBurst);

        // Nothing but the ball is heavy, and nothing but the Uzi repeats, among the twelve.
        for (WeaponId id = WeaponId.Belan; id <= WeaponId.BlackHoleBomb; id++)
        {
            Assert.Equal(id == WeaponId.BowlingBall ? 80 : 100, Weapons.Get(id).Carry);
            Assert.Equal(id == WeaponId.Uzi ? 5 : 0, Weapons.Get(id).Interval);
            Assert.Equal(1, Weapons.Get(id).Pellets);
        }
    }

    [Fact]
    public void TheTableHoldsTheEmptyRowTheHundredWeaponsAndTheSevenHiddenRows()
    {
        Assert.Equal(108, WeaponTable.Count);
        Assert.Equal(WeaponTable.Count, WeaponTable.Rows.Length);
        Assert.Equal(WeaponTable.Count, Weapons.Count);
        Assert.Equal(WeaponId.None, WeaponTable.Rows[0].Id);
        Assert.False(WeaponTable.Rows[0].Crate || WeaponTable.Rows[0].Built);
        for (int i = 0; i < WeaponTable.Rows.Length; i++)
        {
            WeaponRow row = WeaponTable.Rows[i];
            Assert.Equal(i, (int)row.Id); // indexed by id, and an id fits a byte
            Assert.True(i <= byte.MaxValue && (int)row.Move <= byte.MaxValue);
            Assert.Equal(WeaponTable.ColumnCount, row.Columns.Length);
            Assert.True(row.Supply <= sbyte.MaxValue, $"{row.Id}: a prop's supply is a signed byte in STATE");
            if (i == 0) continue;
            Assert.Equal(i <= 100, row.Crate);                         // 1 to 100 drop from crates, 101 to 107 are parts of weapons
            Assert.Equal(row.Crate, row.Credit == row.Id);             // a hidden row's hits are recorded under its weapon
            Assert.True(WeaponTable.Rows[(int)row.Credit].Crate, $"{row.Id} is credited to a row that is not a weapon");
            Assert.Equal(i <= 12 ? (int)row.Move : 7 + i, (int)row.Move); // rows after the twelve: 7 + the id
            Assert.NotEqual(MoveId.WeaponThrow, row.Move);
            Assert.True(row.Move >= MoveId.BelanBonk, $"{row.Id} has a fist move");
        }
        Assert.Equal(7, WeaponTable.Rows.Count(r => r.Id != WeaponId.None && !r.Crate));
        Assert.Equal(12, WeaponTable.Rows.Count(r => r.Weight == 1 && r.Crate)); // the twelve utility rows drop half as often
        Assert.All(WeaponTable.Rows.Where(r => r.Crate), r => Assert.True(r.Weight is 1 or 2));
    }

    [Fact]
    public void EveryMoveIsUniqueAndRowOfMoveRoundTrips()
    {
        var seen = new HashSet<MoveId>();
        for (int i = 1; i < WeaponTable.Rows.Length; i++)
        {
            WeaponRow row = WeaponTable.Rows[i];
            Assert.True(seen.Add(row.Move), $"move {(int)row.Move} is used twice");
            Assert.Same(row, Weapons.RowOfMove(row.Move));
            Assert.Same(row.MoveData, FighterTuning.Default.Moves[row.Move]);
        }
        foreach (MoveId fist in new[] { MoveId.None, MoveId.Jab, MoveId.NeutralHeavy, MoveId.SideHeavy, MoveId.UpHeavy, MoveId.DownHeavy, MoveId.GroundSlam, MoveId.WeaponThrow })
            Assert.Null(Weapons.RowOfMove(fist));
        Assert.Null(Weapons.RowOfMove((MoveId)200));
    }

    [Fact]
    public void TheCrateListIsTheBuiltWeaponsInIdOrderWithNoHiddenRow()
    {
        WeaponId[] list = Weapons.CrateList;
        Assert.True(list.Length >= 12);
        for (int i = 0; i < list.Length; i++)
        {
            WeaponRow row = Weapons.Get(list[i]);
            Assert.True(row.Built && row.Crate, $"{row.Id} can't be in a crate");
            if (i > 0) Assert.True(list[i - 1] < list[i]);
        }
        Assert.Equal(WeaponTable.Rows.Count(r => r.Built && r.Crate), list.Length);
        Assert.All(WeaponTable.Rows.Where(r => !r.Crate), r => Assert.DoesNotContain(r.Id, list));
    }

    /// <summary>
    /// Tuning (spec v0.4, batch 6) moves startups. A kick (a backward lunge on a gun or a blast) must
    /// still come on the frame the shot leaves, and a lunge forward before the active frames end, or
    /// a row tuned faster pushes its wielder at the wrong moment.
    /// </summary>
    [Fact]
    public void EveryLungeAndKickStillLandsInItsMove()
    {
        foreach (WeaponRow row in WeaponTable.Rows.Where(r => r.Id != WeaponId.None && r.Lunge != Fix.Zero))
        {
            if (row.Lunge < Fix.Zero)
                Assert.True(row.LungeAt == row.Startup, $"{row.Id}: its kick is on frame {row.LungeAt}, its shot on {row.Startup}");
            else
                Assert.True(row.LungeAt < row.Startup + (row.Active > 0 ? row.Active : 1), $"{row.Id}: its lunge on frame {row.LungeAt} comes after its swing");
        }
    }

    [Fact]
    public void NoTwoCrateRowsAreTheSameWeaponUnderTwoNames()
    {
        // Equal in every gameplay column: everything but the row's identity (id, move, credit, crate, weight, built).
        const int identity = 6;
        WeaponRow[] crates = WeaponTable.Rows.Where(r => r.Crate).ToArray();
        for (int a = 0; a < crates.Length; a++)
            for (int b = a + 1; b < crates.Length; b++)
                Assert.False(crates[a].Columns[identity..].SequenceEqual(crates[b].Columns[identity..]), $"{crates[a].Id} and {crates[b].Id} are the same weapon");
    }

    [Fact]
    public void TheFlightFlagsAreWhatSection13Says()
    {
        ShotKind[] flies = { ShotKind.Roll, ShotKind.Bullet, ShotKind.Bomb, ShotKind.Arrow, ShotKind.Thrown, ShotKind.Boomerang, ShotKind.Missile, ShotKind.Hole, ShotKind.Lob };
        ShotKind[] placed = { ShotKind.Pad, ShotKind.Mine, ShotKind.Zone, ShotKind.Wall, ShotKind.Speaker, ShotKind.Gate, ShotKind.Decoy };
        foreach (ShotKind k in Enum.GetValues<ShotKind>())
        {
            Assert.Equal(flies.Contains(k), WeaponTable.Flies(k));
            Assert.Equal(placed.Contains(k), WeaponTable.Placed(k));
        }
        Assert.Equal(18, Enum.GetValues<ShotKind>().Length);
        Assert.Equal((1, 5, 10, 17), ((int)ShotKind.Roll, (int)ShotKind.Thrown, (int)ShotKind.Hole, (int)ShotKind.Decoy)); // the first ten keep their numbers
    }

    private static readonly MatchRules Rules = new() { Stage = TestStages.Wide, Weapons = true, Guts = false };

    /// <summary>
    /// Every weapon a crate can hold, one loop: a fighter armed with it faces a target at the row's
    /// CPU range (or 1 m) and presses attack until it is empty or 600 frames pass. Nothing throws,
    /// the supply goes down, and something comes of it: a swing or a blast hurts the target, a row
    /// that shoots puts a prop on the stage.
    /// </summary>
    [Fact]
    public void EveryWeaponACrateCanHoldDoesSomething()
    {
        foreach (WeaponId id in Weapons.CrateList)
        {
            WeaponRow row = Weapons.Get(id);
            Fix distance = row.CpuMax > Fix.Zero ? row.CpuMax : Fix.One;
            GameState s = Simulation.CreateInitialState(Rules, 2, 5);
            s.PropTimer = 1_000_000;
            s.Fighters[0].Position = FixVec2.Zero;
            s.Fighters[0].Facing = 1;
            s.Fighters[0].Grounded = true;
            s.Fighters[0].Weapon = id;
            s.Fighters[0].WeaponSupply = row.Supply;
            s.Fighters[1].Position = new FixVec2(distance, Fix.Zero);
            s.Fighters[1].Grounded = true;
            if (row.ArmedOnly) s.Fighters[1].Weapon = WeaponId.Belan; // a magnet only pulls the armed

            bool sawProp = false, hurt = false, used = false, moved = false;
            for (int f = 0; f < 600; f++)
            {
                bool armed = s.Fighters[0].Weapon == id && s.Fighters[0].WeaponSupply > 0;
                Simulation.Advance(s, Rules, new[] { armed && f % 2 == 0 ? Buttons.Light : Buttons.None, Buttons.None });
                sawProp |= s.Props.Any(p => p.Kind == PropKind.Shot && p.Shot == row.Flight && p.Shot != ShotKind.None);
                ref Fighter target = ref s.Fighters[1];
                // Hurt, given a status, or moved by a spray's drift or lift.
                hurt |= target.Josh > 0 || target.SlowFrames > 0 || target.RootFrames > 0 || target.DotFrames > 0 || target.MarkFrames > 0
                    || (row.Use == WeaponUse.Spray && (target.Position.X != distance || target.Velocity.Y > Fix.Zero));
                used |= s.Fighters[0].Weapon != id || s.Fighters[0].WeaponSupply < row.Supply;
                moved |= s.Fighters[0].Position.X != Fix.Zero; // a blink moves its wielder
                // Keep the target where it was, standing, so every use is at the same range.
                s.Fighters[1].Position = new FixVec2(distance, Fix.Zero);
                s.Fighters[1].Velocity = FixVec2.Zero;
                s.Fighters[1].Grounded = true;
                if (s.Fighters[1].Action == FighterAction.Hitstun && s.Fighters[1].HitStop == 0) s.Fighters[1].Action = FighterAction.Idle;
            }
            Assert.True(used, $"{id} was never used");
            // A Decoy Crate put down beside the target springs on it at once, so it is seen as a hit.
            if (row.Use == WeaponUse.Shoot) Assert.True(sawProp || (row.Flight == ShotKind.Decoy && hurt), $"{id} never put a shot on the stage");
            else if (row.Use == WeaponUse.Blink) Assert.True(moved, $"{id} never moved its wielder");
            else Assert.True(hurt, $"{id} never hurt a target at its range");
        }
    }

    // ---- The generator ----

    /// <summary>Runs a Python script from the repo root; null when there is no Python on this machine.</summary>
    private static (int Exit, string Output)? Python(params string[] args)
    {
        foreach (string exe in new[] { "python", "python3", "py" })
        {
            try
            {
                var start = new ProcessStartInfo(exe) { WorkingDirectory = RepoRoot(), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (string a in args) start.ArgumentList.Add(a);
                using Process p = Process.Start(start)!;
                string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode == 9009) continue; // the Windows "python" stub that only opens the store
                return (p.ExitCode, output);
            }
            catch (Win32Exception)
            {
            }
        }
        return null;
    }

    /// <summary>
    /// The generated files are what tools/gen_weapons.py writes from tools/weapons.json today. (The
    /// table golden already fails when one sim's file is stale; this fails when both are.)
    /// </summary>
    [Fact]
    public void TheGeneratedFilesAreUpToDate()
    {
        var run = Python(Path.Combine("tools", "gen_weapons.py"), "--check");
        if (run == null) return; // no Python here: the table golden still guards the two sims against each other
        Assert.True(run.Value.Exit == 0, "tools/gen_weapons.py --check: " + run.Value.Output);
    }

    /// <summary>
    /// The generator's own tests (tools/test_gen_weapons.py): its rounding (-0.15 is -9830, -0.0366
    /// is -2398, both towards zero), and one bad file per validation rule of section 1.1, each refused.
    /// </summary>
    [Fact]
    public void TheGeneratorRoundsTowardsZeroAndRefusesBadFiles()
    {
        var run = Python(Path.Combine("tools", "test_gen_weapons.py"));
        if (run == null) return;
        Assert.True(run.Value.Exit == 0, "tools/test_gen_weapons.py: " + run.Value.Output);
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Rangbaazi.sln"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Rangbaazi.sln not found above the test directory.");
    }
}
