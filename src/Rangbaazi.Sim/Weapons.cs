using System;
using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim;

/// <summary>The weapons (docs/design/combat-spec-v0.3-weapons.md). 0 is empty-handed.</summary>
public enum WeaponId : byte
{
    None,
    Belan,
    BaseballBat,
    BowlingBall,
    Shotgun,
    Uzi,
    CherryBombs,
    Bow,
}

/// <summary>What a projectile prop is.</summary>
public enum ShotKind : byte
{
    None,
    /// <summary>The bowling ball rolling along the ground; it passes through the fighters it hits.</summary>
    Roll,
    /// <summary>An Uzi bullet: straight and fast, through platforms, gone on its first hit.</summary>
    Bullet,
    /// <summary>A lobbed cherry bomb with a fuse.</summary>
    Bomb,
    /// <summary>A bow's arrow: fast, dips a little, pierces through everyone it hits.</summary>
    Arrow,
    /// <summary>A thrown weapon: it hits once, or lands, and is gone.</summary>
    Thrown,
}

public enum PropKind : byte
{
    None,
    /// <summary>A Prop Box falling or resting on a platform; touching it takes its weapon.</summary>
    Box,
    /// <summary>A fighter's weapon waiting to drop back in a box after they lost a stock.</summary>
    PendingBox,
    /// <summary>A loose weapon lying on the stage (dropped by a swap, or a bowling ball at rest).</summary>
    Item,
    /// <summary>A projectile in flight (see <see cref="ShotKind"/>).</summary>
    Shot,
}

/// <summary>
/// One box, loose weapon or projectile. A plain value type, kept in a fixed array in
/// <see cref="GameState.Props"/> so copying the state copies it. Position is the bottom centre.
/// </summary>
public struct Prop
{
    public PropKind Kind;
    public WeaponId Weapon;
    public ShotKind Shot;
    public int Supply;          // ammo or durability the weapon carries
    public int Owner;           // the fighter who fired or threw it, or -1
    public bool OwnerClear;     // the release grace is over: it can hit its owner now
    public FixVec2 Position;
    public FixVec2 Velocity;
    public bool Grounded;
    public int Timer;           // frames: age of a box or item, fuse of a bomb, wait of a pending box
    public int Age;             // frames in flight
    public int HitMask;         // one bit per fighter already hit
    public int Hits;            // fighters hit by this shot so far
    public int IgnoreFighter;   // 1 + the fighter who just dropped it, or 0
    public int IgnoreTimer;

    internal readonly void AddTo(ref StateHasher h)
    {
        h.Add((int)Kind);
        h.Add((int)Weapon);
        h.Add((int)Shot);
        h.Add(Supply);
        h.Add(Owner);
        h.Add(OwnerClear);
        h.Add(Position.X.Raw);
        h.Add(Position.Y.Raw);
        h.Add(Velocity.X.Raw);
        h.Add(Velocity.Y.Raw);
        h.Add(Grounded);
        h.Add(Timer);
        h.Add(Age);
        h.Add(HitMask);
        h.Add(Hits);
        h.Add(IgnoreFighter);
        h.Add(IgnoreTimer);
    }
}

/// <summary>One weapon's fixed data. Immutable, outside the rollback state.</summary>
public sealed class WeaponData
{
    public required WeaponId Id { get; init; }

    /// <summary>The attack: <see cref="MoveId"/> of its frame data in <see cref="WeaponMoves"/>.</summary>
    public required MoveId Move { get; init; }

    /// <summary>Ammo (shots) or durability (hits that connect) a fresh weapon carries.</summary>
    public required int Supply { get; init; }

    /// <summary>The projectile its attack fires at the move's startup frame, or None for melee.</summary>
    public ShotKind Fires { get; init; }

    /// <summary>Damage when thrown: light 6, medium 9, heavy 12.</summary>
    public int ThrowDamage { get; init; } = 9;
}

/// <summary>
/// The weapon catalogue, the weapons' moves and the props' rules. Shared by every fighter; numbers
/// are first guesses for playtests (combat spec v0.3).
/// </summary>
public static class Weapons
{
    public const int Count = 8; // None plus seven weapons
    public const int MaxProps = 16;

    // Prop Boxes: fast, because the game is fast.
    public const int FirstBoxFrames = 90;        // 1.5 s into the match
    public const int BoxIntervalMin = 150;       // then one every 2.5 to 4.5 s
    public const int BoxIntervalMax = 270;       // exclusive
    public const int ItemLifeFrames = 720;       // an untouched box or loose weapon vanishes after 12 s
    public const int ReturnFrames = 60;          // a dead fighter's weapon is back in a box after 1 s
    public const int NoSwapBackFrames = 60;      // a dropped weapon ignores its dropper for 1 s
    public const int SignaturePercent = 110;     // a fighter's own weapon hits 10% harder (GDD B4 affinity)
    public const int SweetPercent = 125;         // sweet-spot hits: 1.25x damage and base knockback

    public static readonly Fix BoxHalf = Fix.FromRatio(2, 5);            // boxes and items are 0.8 m
    public static readonly Fix BoxDropHeight = Fix.FromInt(6);           // boxes appear 6 m above a platform
    public static readonly Fix EdgeMargin = Fix.Half;

    /// <summary>At most this many boxes and loose weapons at once: 3 + 1 per 2 fighters, up to 8.</summary>
    public static int Cap(int fighters) => Math32.Min(8, 3 + fighters / 2);

    // Projectiles.
    public static readonly Fix ShotGravity = Fix.FromRatio(1, 100);
    public static readonly Fix ArrowGravity = Fix.FromRatio(4, 1000);
    public static readonly Fix MaxFall = Fix.FromRatio(30, 100);
    public static readonly Fix RollSpeed = Fix.FromRatio(22, 100);
    public const int RollFrames = 180;
    public static readonly Fix BulletSpeed = Fix.FromRatio(70, 100);
    public const int BulletFrames = 20;
    public const int UziInterval = 5;
    public static readonly Fix BombVx = Fix.FromRatio(14, 100);
    public static readonly Fix BombVy = Fix.FromRatio(18, 100);
    public const int BombFuse = 50;
    public static readonly Fix BlastHalf = Fix.FromRatio(6, 5);          // a 2.4 m blast
    public static readonly Fix ArrowSpeed = Fix.FromRatio(55, 100);
    public const int ArrowFrames = 50;
    public const int LongShotFrames = 20;                                // an arrow that flew 11 m+ is a long shot
    public static readonly Fix ThrowVx = Fix.FromRatio(32, 100);
    public static readonly Fix ThrowVy = Fix.FromRatio(6, 100);
    public static readonly Fix HandHeight = Fix.One;
    public static readonly Fix HandReach = Fix.FromRatio(3, 5);
    public static readonly Fix ShotgunFar = Fix.FromInt(3);              // beyond this the blast does 10
    public static readonly Fix PointBlank = Fix.FromRatio(6, 5);         // the shotgun's sweet spot
    public static readonly Fix BatTip = Fix.FromRatio(13, 10);           // the bat's sweet spot
    public const int ShotgunFarDamage = 10;
    public const int UziStreakSweet = 6;

    /// <summary>The Uzi's fixed spread pattern (vertical speed per shot): 0, +3, -3, +6, -6 degrees.</summary>
    private static readonly Fix[] UziSpread =
    {
        Fix.Zero, Fix.FromRatio(366, 10000), Fix.FromRatio(-366, 10000), Fix.FromRatio(732, 10000), Fix.FromRatio(-732, 10000),
    };

    public static Fix UziSpreadAt(int shot) => UziSpread[shot % UziSpread.Length];

    /// <summary>Half the width (and half the height) of a shot's box.</summary>
    public static Fix ShotHalf(ShotKind s) => s switch
    {
        ShotKind.Roll => Fix.FromRatio(3, 10),
        ShotKind.Bullet => Fix.FromRatio(3, 20),
        ShotKind.Bomb => Fix.FromRatio(1, 5),
        ShotKind.Arrow => Fix.FromRatio(1, 4),
        _ => Fix.FromRatio(3, 10),
    };

    private static readonly WeaponData[] Catalogue =
    {
        new() { Id = WeaponId.None, Move = MoveId.None, Supply = 0 },
        new() { Id = WeaponId.Belan, Move = MoveId.BelanBonk, Supply = 12, ThrowDamage = 6 },
        new() { Id = WeaponId.BaseballBat, Move = MoveId.BatSwing, Supply = 8, ThrowDamage = 9 },
        new() { Id = WeaponId.BowlingBall, Move = MoveId.BallRoll, Supply = 1, Fires = ShotKind.Roll, ThrowDamage = 12 },
        new() { Id = WeaponId.Shotgun, Move = MoveId.ShotgunBlast, Supply = 4, ThrowDamage = 9 },
        new() { Id = WeaponId.Uzi, Move = MoveId.UziFire, Supply = 30, Fires = ShotKind.Bullet, ThrowDamage = 6 },
        new() { Id = WeaponId.CherryBombs, Move = MoveId.BombLob, Supply = 3, Fires = ShotKind.Bomb, ThrowDamage = 9 },
        new() { Id = WeaponId.Bow, Move = MoveId.BowShot, Supply = 6, Fires = ShotKind.Arrow, ThrowDamage = 6 },
    };

    public static WeaponData Get(WeaponId id) => Catalogue[(int)id];

    /// <summary>
    /// Each weapon's owner: the fighter who uses it best (+10% damage). The rest of the cast get
    /// theirs as their own weapons are added.
    /// </summary>
    public static WeaponId SignatureFor(string? character) => character switch
    {
        "akshat" => WeaponId.Belan,        // the chef
        "shubham" => WeaponId.BaseballBat, // the gully slugger
        "shahn" => WeaponId.BowlingBall,   // Shan, the street brawler
        "salik" => WeaponId.Shotgun,       // the robber
        "rishabh" => WeaponId.Uzi,         // the tech gunner
        "ankit" => WeaponId.CherryBombs,   // the clown
        "suhani" => WeaponId.Bow,          // the rooftop archer
        _ => WeaponId.None,
    };

    // Launch angles as unit vectors.
    private static Fix C(int v) => Fix.FromRatio(v, 10000);

    public static readonly MoveData BelanBonk = new()
    {
        Startup = 7, Active = 3, Recovery = 14,
        Damage = 8, BaseKnockback = Fix.FromRatio(12, 100), KnockbackGrowth = Fix.FromRatio(25, 10000),
        LaunchX = C(6428), LaunchY = C(7660), // 50 degrees
        HitstunBonus = 10, // dizzy
        HitboxNear = Fix.FromRatio(1, 5), HitboxFar = Fix.FromRatio(13, 10),
        HitboxBottom = Fix.FromRatio(3, 5), HitboxTop = Fix.FromInt(2),
    };

    public static readonly MoveData BatSwing = new()
    {
        Startup = 12, Active = 4, Recovery = 20,
        Damage = 16, BaseKnockback = Fix.FromRatio(17, 100), KnockbackGrowth = Fix.FromRatio(26, 10000),
        LaunchX = C(8192), LaunchY = C(5736), // 35 degrees
        HitstunBonus = 4,
        HitboxNear = Fix.Zero, HitboxFar = Fix.FromRatio(9, 5),
        HitboxBottom = Fix.FromRatio(2, 5), HitboxTop = Fix.FromRatio(8, 5),
    };

    public static readonly MoveData ShotgunBlast = new()
    {
        Startup = 12, Active = 2, Recovery = 30,
        Damage = 18, BaseKnockback = Fix.FromRatio(20, 100), KnockbackGrowth = Fix.FromRatio(28, 10000),
        LaunchX = C(9063), LaunchY = C(4226), // 25 degrees
        HitstunBonus = 4,
        HitboxNear = Fix.FromRatio(2, 5), HitboxFar = Fix.FromRatio(9, 2),
        HitboxBottom = Fix.Half, HitboxTop = Fix.FromRatio(3, 2),
        ImpulseFrame = 12, LungeSpeed = Fix.FromRatio(-15, 100), // the kick pushes the shooter back
    };

    public static readonly MoveData BallRoll = new()
    {
        Startup = 14, Active = 0, Recovery = 20,
        Damage = 14, BaseKnockback = Fix.FromRatio(18, 100), KnockbackGrowth = Fix.FromRatio(24, 10000),
        LaunchX = C(8660), LaunchY = C(5000), // 30 degrees
        HitstunBonus = 4,
    };

    public static readonly MoveData UziFire = new()
    {
        Startup = 3, Active = 0, Recovery = 8,
        Damage = 2, BaseKnockback = Fix.FromRatio(5, 100), KnockbackGrowth = Fix.FromRatio(5, 10000),
        LaunchX = C(9848), LaunchY = C(1736), // 10 degrees
        HitstunBonus = -6,
    };

    public static readonly MoveData BombLob = new()
    {
        Startup = 8, Active = 0, Recovery = 14,
        Damage = 12, BaseKnockback = Fix.FromRatio(18, 100), KnockbackGrowth = Fix.FromRatio(25, 10000),
        LaunchX = C(5000), LaunchY = C(8660), // 60 degrees, away from the blast
        HitstunBonus = 4,
    };

    public static readonly MoveData BowShot = new()
    {
        Startup = 10, Active = 0, Recovery = 14,
        Damage = 10, BaseKnockback = Fix.FromRatio(15, 100), KnockbackGrowth = Fix.FromRatio(24, 10000),
        LaunchX = C(9397), LaunchY = C(3420), // 20 degrees
        HitstunBonus = 2,
    };

    public static readonly MoveData WeaponThrow = new()
    {
        Startup = 6, Active = 0, Recovery = 12,
        Damage = 9, BaseKnockback = Fix.FromRatio(16, 100), KnockbackGrowth = Fix.FromRatio(26, 10000),
        LaunchX = C(8192), LaunchY = C(5736), // 35 degrees
        HitstunBonus = 4,
    };

    /// <summary>The bat's sweet spot (the tip) launches flat at 20 degrees: the ring-out "six".</summary>
    public static readonly Fix SixLaunchX = C(9397);
    public static readonly Fix SixLaunchY = C(3420);

    public static MoveData? MoveFor(MoveId id) => id switch
    {
        MoveId.BelanBonk => BelanBonk,
        MoveId.BatSwing => BatSwing,
        MoveId.ShotgunBlast => ShotgunBlast,
        MoveId.BallRoll => BallRoll,
        MoveId.UziFire => UziFire,
        MoveId.BombLob => BombLob,
        MoveId.BowShot => BowShot,
        MoveId.WeaponThrow => WeaponThrow,
        _ => null,
    };

    public static bool IsWeaponMove(MoveId id) => id >= MoveId.BelanBonk;
}
