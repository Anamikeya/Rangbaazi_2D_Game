using System;
using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim;

/// <summary>Which move a fighter is performing while its action is <see cref="FighterAction.Attack"/>.</summary>
public enum MoveId : byte
{
    None,

    /// <summary>Light. The only light attack so far: strings and directional lights come later.</summary>
    Jab,

    /// <summary>Heavy with no direction.</summary>
    NeutralHeavy,

    /// <summary>Left or right + Heavy: lunges forward. The main way to knock someone off the side.</summary>
    SideHeavy,

    /// <summary>Up + Heavy: a rising strike, and the recovery move. Once per trip into the air.</summary>
    UpHeavy,

    /// <summary>Down + Heavy on the ground: a low sweep that hits both sides.</summary>
    DownHeavy,

    /// <summary>Down + Heavy in the air (GDD B2 "Ground slam"): hovers, then plunges until it lands.</summary>
    GroundSlam,
}

/// <summary>
/// Frame data for one move (GDD B4 move-set template). Immutable and outside the rollback state:
/// a fighter's state stores only the <see cref="MoveId"/> and how many frames into it they are.
///
/// Frame numbers count from the frame the button was pressed (frame 0). The hitbox is out on
/// frames [Startup, Startup + Active), and the fighter can act again on the frame after
/// <see cref="TotalFrames"/>. Distances are metres from the fighter's feet, with X measured in the
/// direction they face; speeds are metres per frame.
/// </summary>
public sealed class MoveData
{
    public int Startup { get; init; }
    public int Active { get; init; }
    public int Recovery { get; init; }

    /// <summary>Josh % added to the target.</summary>
    public int Damage { get; init; }

    /// <summary>Knockback speed is BaseKnockback + KnockbackGrowth × the target's Josh after the hit.</summary>
    public Fix BaseKnockback { get; init; }
    public Fix KnockbackGrowth { get; init; }

    /// <summary>Launch direction as a unit vector: X forward (away from the attacker), Y up.</summary>
    public Fix LaunchX { get; init; }
    public Fix LaunchY { get; init; }

    /// <summary>Extra hitstun frames on top of the shared formula (8 + Josh / 8).</summary>
    public int HitstunBonus { get; init; }

    public Fix HitboxNear { get; init; }
    public Fix HitboxFar { get; init; }
    public Fix HitboxBottom { get; init; }
    public Fix HitboxTop { get; init; }

    /// <summary>
    /// The hitbox covers both sides, from -HitboxFar to +HitboxFar, and launches targets away from the
    /// attacker whichever side they are on.
    /// </summary>
    public bool HitsBothSides { get; init; }

    /// <summary>Frame on which <see cref="LungeSpeed"/> and <see cref="RiseSpeed"/> are applied.</summary>
    public int ImpulseFrame { get; init; }

    /// <summary>When nonzero, horizontal speed is set to this, forward, on <see cref="ImpulseFrame"/>.</summary>
    public Fix LungeSpeed { get; init; }

    /// <summary>When nonzero, vertical speed is set to this, upward, on <see cref="ImpulseFrame"/>.</summary>
    public Fix RiseSpeed { get; init; }

    /// <summary>The fighter can steer left and right in the air during the move.</summary>
    public bool AirControl { get; init; }

    /// <summary>
    /// When nonzero, the fighter hangs still through startup and then falls at this speed, with the
    /// hitbox out, until it lands or the active frames run out. Landing skips to recovery.
    /// </summary>
    public Fix PlungeSpeed { get; init; }

    /// <summary>Using the move spends the fighter's recovery until they land or are hit.</summary>
    public bool UsesRecovery { get; init; }

    public int TotalFrames => Startup + Active + Recovery;

    public bool IsActiveOn(int actionFrame) => actionFrame >= Startup && actionFrame < Startup + Active;
}

/// <summary>One hero's moves (GDD B4: lights, and four heavies or signatures).</summary>
public sealed class MoveSet
{
    public required MoveData Jab { get; init; }
    public required MoveData NeutralHeavy { get; init; }
    public required MoveData SideHeavy { get; init; }
    public required MoveData UpHeavy { get; init; }
    public required MoveData DownHeavy { get; init; }
    public required MoveData GroundSlam { get; init; }

    public MoveData this[MoveId id] => id switch
    {
        MoveId.Jab => Jab,
        MoveId.NeutralHeavy => NeutralHeavy,
        MoveId.SideHeavy => SideHeavy,
        MoveId.UpHeavy => UpHeavy,
        MoveId.DownHeavy => DownHeavy,
        MoveId.GroundSlam => GroundSlam,
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "No move data for this id."),
    };

    /// <summary>
    /// The first hero's moves: the medium-weight all-rounder (GDD B4 roster, hero 1), written to
    /// combat spec v0.1 (docs/design/combat-spec-v0.1.md). Placeholder numbers for the graybox
    /// playtest. Named for the archetype until the hero's final name is settled.
    /// </summary>
    public static readonly MoveSet AllRounder = new()
    {
        Jab = new MoveData
        {
            Startup = 4, Active = 3, Recovery = 8,
            Damage = 6,
            BaseKnockback = Fix.FromRatio(12, 100), KnockbackGrowth = Fix.FromRatio(3, 1000),
            LaunchX = Fix.FromRatio(7071, 10000), LaunchY = Fix.FromRatio(7071, 10000), // 45 degrees
            HitboxNear = Fix.FromRatio(1, 5), HitboxFar = Fix.FromRatio(11, 10),
            HitboxBottom = Fix.FromRatio(3, 5), HitboxTop = Fix.FromRatio(7, 5),
        },
        NeutralHeavy = new MoveData
        {
            Startup = 14, Active = 4, Recovery = 22,
            Damage = 14,
            BaseKnockback = Fix.FromRatio(16, 100), KnockbackGrowth = Fix.FromRatio(225, 100000),
            LaunchX = Fix.FromRatio(7660, 10000), LaunchY = Fix.FromRatio(6428, 10000), // 40 degrees
            HitstunBonus = 4,
            HitboxNear = Fix.FromRatio(1, 5), HitboxFar = Fix.FromRatio(7, 5),
            HitboxBottom = Fix.FromRatio(2, 5), HitboxTop = Fix.FromRatio(8, 5),
        },
        SideHeavy = new MoveData
        {
            Startup = 16, Active = 5, Recovery = 24,
            Damage = 16,
            BaseKnockback = Fix.FromRatio(156, 1000), KnockbackGrowth = Fix.FromRatio(23, 10000),
            LaunchX = Fix.FromRatio(8660, 10000), LaunchY = Fix.Half, // 30 degrees: flatter, for ring-outs
            HitstunBonus = 4,
            HitboxNear = Fix.FromRatio(3, 10), HitboxFar = Fix.FromRatio(3, 2),
            HitboxBottom = Fix.Half, HitboxTop = Fix.FromRatio(7, 5),
            ImpulseFrame = 10, LungeSpeed = Fix.FromRatio(16, 100),
        },
        UpHeavy = new MoveData
        {
            Startup = 6, Active = 8, Recovery = 26,
            Damage = 10,
            BaseKnockback = Fix.FromRatio(17, 100), KnockbackGrowth = Fix.FromRatio(22, 10000),
            LaunchX = Fix.FromRatio(1736, 10000), LaunchY = Fix.FromRatio(9848, 10000), // 80 degrees
            HitstunBonus = 2,
            HitboxNear = Fix.FromRatio(-3, 10), HitboxFar = Fix.FromRatio(9, 10),
            HitboxBottom = Fix.FromRatio(4, 5), HitboxTop = Fix.FromRatio(12, 5),
            ImpulseFrame = 6, RiseSpeed = Fix.FromRatio(27, 100),
            AirControl = true, UsesRecovery = true,
        },
        DownHeavy = new MoveData
        {
            Startup = 12, Active = 4, Recovery = 22,
            Damage = 12,
            BaseKnockback = Fix.FromRatio(15, 100), KnockbackGrowth = Fix.FromRatio(18, 10000),
            LaunchX = Fix.FromRatio(9063, 10000), LaunchY = Fix.FromRatio(4226, 10000), // 25 degrees
            HitstunBonus = 2,
            HitboxNear = Fix.Zero, HitboxFar = Fix.FromRatio(13, 10),
            HitboxBottom = Fix.Zero, HitboxTop = Fix.FromRatio(7, 10),
            HitsBothSides = true,
        },
        GroundSlam = new MoveData
        {
            Startup = 10, Active = 40, Recovery = 14,
            Damage = 13,
            BaseKnockback = Fix.FromRatio(17, 100), KnockbackGrowth = Fix.FromRatio(21, 10000),
            LaunchX = Fix.FromRatio(3420, 10000), LaunchY = Fix.FromRatio(9397, 10000), // 70 degrees
            HitstunBonus = 4,
            HitboxNear = Fix.FromRatio(-1, 2), HitboxFar = Fix.Half,
            HitboxBottom = Fix.FromRatio(-2, 5), HitboxTop = Fix.FromRatio(3, 5),
            PlungeSpeed = Fix.FromRatio(2, 5),
        },
    };
}
