using System;
using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim;

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
    public int Supply;          // ammo or durability the weapon carries (a box flung by a swing carries it as a thrown shot); a speaker's facing; the weapon a decoy pretends to hold
    public int Owner;           // the fighter who fired or threw it, or -1
    public bool OwnerClear;     // the release grace is over: it can hit its owner now
    public FixVec2 Position;
    public FixVec2 Velocity;
    public bool Grounded;
    public int Timer;           // frames: age of a box or item, fuse of a bomb, arming of a mine or trap, life left of a pad, zone, wall, speaker, gate or decoy, an anvil's hang, wait of a pending box; a boomerang's leg (0 out, 1 back)
    public int Age;             // frames in flight
    public int HitMask;         // one bit per fighter already hit (a gate: per fighter it has sent through and who still touches it)
    public int Hits;            // fighters hit by this shot so far
    public int IgnoreFighter;   // a box or item: 1 + the fighter who just dropped it, or 0. A shot: swings a breakable prop has taken (its wear)
    public int IgnoreTimer;     // a box or item: frames left of that. A shot: screen-edge bounces done

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

/// <summary>
/// The props' rules and the numbers every weapon shares. A weapon's own numbers are a row of the
/// generated <see cref="WeaponTable"/> (tools/weapons.json; combat spec v0.4): the engine reads the
/// row's columns and never asks which weapon it is.
/// </summary>
public static class Weapons
{
    /// <summary>Rows in the table: the empty row 0, the weapons, and the hidden rows that are parts of weapons.</summary>
    public const int Count = WeaponTable.Count;

    /// <summary>
    /// The match record (<see cref="GameState.WeaponStats"/>): each fighter has this many slots of
    /// <see cref="RecordSlotInts"/> ints, [weapon + 1, damage, KOs, sweet spots], then one int, all
    /// the damage they have done to others. A slot whose first int is 0 is empty.
    /// </summary>
    public const int RecordSlots = 6;
    public const int RecordSlotInts = 4;
    public const int RecordInts = RecordSlots * RecordSlotInts + 1;

    /// <summary>The prop table of a match: 16 slots and 4 more per fighter, up to 64.</summary>
    public static int PropSlots(int fighters) => Math32.Min(64, 16 + 4 * fighters);

    /// <summary>
    /// The first slots of the table are kept for boxes, pending boxes and loose weapons: shots only
    /// use the slots after them, so crates keep dropping and a dead fighter's weapon always comes
    /// back, however many shots are out.
    /// </summary>
    public static int BoxSlots(int fighters) => Cap(fighters) + 2;

    /// <summary>
    /// The most placed props (pads, mines: the flights with the placed flag) one fighter can have
    /// out: 4, or 2 in a match of more than 8. Placing one more removes their oldest.
    /// </summary>
    public static int PlacedLimit(int fighters) => fighters > 8 ? 2 : 4;

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

    // Shared by every projectile.
    public static readonly Fix ShotGravity = Fix.FromRatio(1, 100);      // props lying or rolling on the stage
    public static readonly Fix MaxFall = Fix.FromRatio(30, 100);
    public static readonly Fix GroundBrake = Fix.FromRatio(2, 100);      // a landed bomb slides to a stop
    public static readonly Fix ThrowVx = Fix.FromRatio(32, 100);
    public static readonly Fix ThrowVy = Fix.FromRatio(6, 100);
    public static readonly Fix ThrownHalf = Fix.FromRatio(3, 10);        // a thrown weapon's box is 0.6 m
    public static readonly Fix HandHeight = Fix.One;
    public static readonly Fix HandReach = Fix.FromRatio(3, 5);
    public static readonly Fix BoomerangClimb = Fix.FromRatio(12, 100);  // how fast a boomerang rises or sinks to the hand
    public static readonly Fix MissileLevel = Fix.FromRatio(1, 5);       // within this height of its target a missile flies level
    public static readonly Fix BounceMin = Fix.FromRatio(8, 100);        // a bouncing shot leaves the floor at least this fast
    public static readonly Fix ScreenInset = Fix.FromRatio(6, 5);        // the screen's sides are this far inside the side blast lines
    public static readonly Fix PullStop = Fix.One;                       // a spray's pull stops this far in front of the wielder

    // Statuses (combat spec v0.4, section 2.4).
    public const int SlowCapFrames = 150;       // slow (and chill) adds up to 2.5 s
    public const int FreezeAt = 90;             // chill of this much on someone standing freezes them
    public const int FreezeFrames = 60;         // frozen solid for 1 s
    public const int FreezeImmuneFrames = 210;  // and then can't be held for 2.5 s more
    public const int HoldImmuneFrames = 90;     // after a root or stun ends, 1.5 s before another can take
    public const int RootPressFrames = 12;      // each fresh attack press cuts a root this much
    public const int MarkPercent = 125;         // a marked fighter takes 25% more damage
    public const int DotDamage = 1;             // a poison or burn tick
    public const int AirDriftPercent = 60;      // a spray's drift on someone in the air is capped at this much of air speed

    // Guards, traps, zones and walls (combat spec v0.4, sections 2.7 and 2.8).
    public const int GuardFrom = 2;             // a guard covers the front from this frame of its move to the end of its startup
    public const int HoldMaxFrames = 90;        // the Parat's guard waits on its last startup frame for at most this long
    public static readonly Fix BlockPush = Fix.FromRatio(12, 100);       // a blocked attacker is pushed back this fast
    public static readonly Fix SlipSpeed = Fix.FromRatio(30, 100);       // a slip slides the way the fighter faces, then friction stops it
    public static readonly Fix SlipRunSpeed = Fix.FromRatio(5, 100);     // a puddle slips whoever crosses it faster than this
    public static readonly Fix WallPush = Fix.FromRatio(1, 5);           // a wall moves a fighter out by at most this much a frame
    public static readonly Fix ScoopPullTo = Fix.FromRatio(6, 5);        // the rake drags boxes and loose weapons to this far in front
    public static readonly Fix HiddenSeen = Fix.FromInt(2);              // the CPU still sees a fighter hidden in a cloud this close

    // Machines and position tricks (combat spec v0.4, section 2.9).
    public static readonly Fix DropAhead = Fix.FromInt(4);               // the anvil hangs this far in front of its wielder (with nobody to aim at) ...
    public static readonly Fix DropHeight = Fix.FromInt(9);              // ... this high above their feet ...
    public static readonly Fix BlastMargin = Fix.FromInt(2);             // ... and at least this far inside the blast lines, as a box is
    public static readonly Fix DropMin = Fix.FromInt(2);                 // its mark is over the nearest opponent in front, where they will be, but no nearer than this ...
    public static readonly Fix DropMax = Fix.FromInt(6);                 // ... and no further than this ahead
    public static readonly Fix DropSeek = Fix.FromInt(8);                // it aims at an opponent at most this far in front
    public const int DropLead = 40;                                      // frames ahead it guesses where they will be (their speed times this)
    public static readonly Fix PulseHeight = Fix.FromInt(2);             // a speaker's pulse is its reach long and this high
    public static readonly Fix SafeMargin = Fix.FromInt(2);              // a safe spot is this far inside the blast zone on every side ...
    public static readonly Fix SafeDrop = Fix.FromInt(4);                // ... with a platform top at most this far below it
    public static readonly Fix BlinkMin = Fix.FromInt(2);                // the Teleporter tries its reach, then 1 m less each time, down to this
    public static readonly Fix BlinkHeight = Fix.FromInt(6);             // onto a platform top at most this far above or below the feet
    public static readonly Fix BlinkAbove = Fix.Half;                    // the upward blink only takes tops more than this above the feet

    /// <summary>
    /// Frames a shot starts with in <see cref="Prop.Timer"/>: a speaker, gate or decoy counts its
    /// life down there; every other flight starts at its row's fuse (a mine's arming, a pad's life,
    /// an anvil's hang).
    /// </summary>
    public static int StartTimer(WeaponRow row) =>
        row.Flight == ShotKind.Speaker || row.Flight == ShotKind.Gate || row.Flight == ShotKind.Decoy ? row.Life : row.Fuse;

    /// <summary>The most props of this flight one fighter can have out (one speaker, a pair of gates), or 0 for no limit of its own.</summary>
    public static int FlightLimit(ShotKind flight) => flight == ShotKind.Speaker ? 1 : flight == ShotKind.Gate ? 2 : 0;

    /// <summary>The weapon a decoy pretends to hold: the last real box's, or the Belan if none has dropped yet.</summary>
    public static WeaponId DecoyLooks(GameState state) => state.LastBoxWeapon != WeaponId.None ? state.LastBoxWeapon : WeaponId.Belan;

    /// <summary>A row by weapon id (row 0 is the empty hand).</summary>
    public static WeaponRow Get(WeaponId id) => WeaponTable.Rows[(int)id];

    /// <summary>The built weapons a crate can hold, in id order.</summary>
    public static readonly WeaponId[] CrateList = BuildCrateList();

    private static WeaponId[] BuildCrateList()
    {
        int n = 0;
        foreach (WeaponRow r in WeaponTable.Rows)
            if (r.Built && r.Crate) n++;
        var list = new WeaponId[n];
        n = 0;
        foreach (WeaponRow r in WeaponTable.Rows)
            if (r.Built && r.Crate) list[n++] = r.Id;
        return list;
    }

    private static readonly WeaponRow?[] MoveRows = BuildMoveRows();

    private static WeaponRow?[] BuildMoveRows()
    {
        var rows = new WeaponRow?[256];
        for (int i = 1; i < WeaponTable.Rows.Length; i++) rows[(int)WeaponTable.Rows[i].Move] = WeaponTable.Rows[i];
        return rows;
    }

    /// <summary>The row whose attack this move is; null for the fist moves and for <see cref="MoveId.WeaponThrow"/>.</summary>
    public static WeaponRow? RowOfMove(MoveId move) => MoveRows[(int)move];

    private static readonly WeaponRow?[] FlightRows = BuildFlightRows();

    private static WeaponRow?[] BuildFlightRows()
    {
        var rows = new WeaponRow?[256];
        for (int i = WeaponTable.Rows.Length - 1; i >= 1; i--)
            if (WeaponTable.Rows[i].Flight != ShotKind.None) rows[(int)WeaponTable.Rows[i].Flight] = WeaponTable.Rows[i];
        return rows;
    }

    /// <summary>The flight a burst of this row leaves behind, or None.</summary>
    public static ShotKind LeftFlight(WeaponRow row) =>
        row.Leaves == WeaponLeaves.Zone ? ShotKind.Zone
        : row.Leaves == WeaponLeaves.Wall ? ShotKind.Wall
        : row.Leaves == WeaponLeaves.Pad ? ShotKind.Pad : ShotKind.None;

    /// <summary>
    /// The row a shot prop takes its numbers from: the row of the weapon it carries. A thrown
    /// weapon (thrown, or flung by a swing) is a plain <c>thrown</c> prop and takes none of its
    /// weapon's shot columns: it gets the empty row 0, so it can't be broken, bounce off the screen
    /// edge or pass its thrower because of what it carries; its damage is its row's <c>throw</c>
    /// and its size <see cref="ThrownHalf"/>. A shot whose weapon doesn't fly that way (only a
    /// weapon changed in the middle of its move makes one) takes them from the first row in the
    /// table that does.
    /// </summary>
    public static WeaponRow ShotRow(in Prop p)
    {
        if (p.Shot == ShotKind.Thrown) return WeaponTable.Rows[0];
        WeaponRow row = WeaponTable.Rows[(int)p.Weapon];
        if (row.Flight == p.Shot || (row.Leaves != WeaponLeaves.None && LeftFlight(row) == p.Shot)) return row;
        return FlightRows[(int)p.Shot] ?? row;
    }

    /// <summary>
    /// A shot prop that swings and bursts break (its row's <c>brk</c>): the trap, the orb, or what a
    /// burst left behind (the Matka's wall). A bomb that will leave a breakable wall is not itself breakable.
    /// </summary>
    public static bool Breakable(in Prop p)
    {
        if (p.Kind != PropKind.Shot) return false;
        WeaponRow row = ShotRow(in p);
        return row.Break > 0 && (row.Leaves == WeaponLeaves.None || LeftFlight(row) == p.Shot);
    }

    /// <summary>The weapon a prop's hits, KOs and record are credited to: a hidden row's weapon, else its own.</summary>
    public static WeaponId CreditOf(in Prop p) => WeaponTable.Rows[(int)p.Weapon].Credit;

    /// <summary>Half the width (and half the height) of a shot's box.</summary>
    public static Fix ShotHalf(in Prop p) => p.Shot == ShotKind.Thrown ? ThrownHalf : ShotRow(in p).ShotHalf;

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

    /// <summary>Throwing the held weapon: the one weapon move that isn't a row. Its damage is the row's <c>throw</c>.</summary>
    public static readonly MoveData WeaponThrow = new()
    {
        Startup = 6, Active = 0, Recovery = 12,
        Damage = 9, BaseKnockback = Fix.FromRatio(16, 100), KnockbackGrowth = Fix.FromRatio(26, 10000),
        LaunchX = Fix.FromRatio(8192, 10000), LaunchY = Fix.FromRatio(5736, 10000), // 35 degrees
        HitstunBonus = 4,
    };

    /// <summary>A weapon move's frame data: the throw's, or its row's. Null for a fist move.</summary>
    public static MoveData? MoveFor(MoveId id) => id == MoveId.WeaponThrow ? WeaponThrow : RowOfMove(id)?.MoveData;
}
