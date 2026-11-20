using System;
using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim;

/// <summary>
/// Weapons in the step function (Weapons.cs has the data): Prop Boxes dropping, picking up and
/// swapping, the weapons' shots, projectiles flying, hitting and exploding, the bat batting them
/// back, and a dead fighter's weapon coming back in a box. Runs only when MatchRules.Weapons is on.
/// </summary>
public static partial class Simulation
{
    /// <summary>A prop's box: <see cref="Prop.Position"/> is its bottom centre.</summary>
    public static FixRect PropBox(in Prop p)
    {
        Fix h = p.Kind == PropKind.Shot ? Weapons.ShotHalf(p.Shot) : Weapons.BoxHalf;
        return new FixRect(p.Position.X - h, p.Position.Y, p.Position.X + h, p.Position.Y + h * 2);
    }

    /// <summary>Weapon moves release their shot on their startup frame; the shotgun spends a shell then.</summary>
    private static void FireShots(GameState state, ReadOnlySpan<bool> frozen, MatchRules rules)
    {
        Fighter[] fighters = state.Fighters;
        for (int i = 0; i < fighters.Length; i++)
        {
            ref Fighter f = ref fighters[i];
            if (frozen[i] || f.Action != FighterAction.Attack || !Weapons.IsWeaponMove(f.Move)) continue;
            if (f.ActionFrame != rules.Tuning.Moves[f.Move].Startup || f.Weapon == WeaponId.None) continue;

            var hand = new FixVec2(f.Position.X + Weapons.HandReach * f.Facing, f.Position.Y + Weapons.HandHeight);
            switch (f.Move)
            {
                case MoveId.WeaponThrow:
                    AddShot(state, ShotKind.Thrown, f.Weapon, i, hand, new FixVec2(Weapons.ThrowVx * f.Facing, Weapons.ThrowVy), false);
                    f.Weapon = WeaponId.None;
                    f.WeaponSupply = 0;
                    break;
                case MoveId.ShotgunBlast:
                    if (f.WeaponSupply > 0) f.WeaponSupply--;
                    break;
                case MoveId.BallRoll:
                    AddShot(state, ShotKind.Roll, f.Weapon, i, new FixVec2(hand.X, f.Position.Y),
                        new FixVec2(Weapons.RollSpeed * f.Facing, Fix.Zero), f.Grounded);
                    f.Weapon = WeaponId.None; // the ball has left the hand; it lies there when it stops
                    f.WeaponSupply = 0;
                    break;
                case MoveId.UziFire:
                    AddShot(state, ShotKind.Bullet, f.Weapon, i, hand, new FixVec2(Weapons.BulletSpeed * f.Facing, Weapons.UziSpreadAt(f.UziShots)), false);
                    f.UziShots++;
                    if (f.WeaponSupply > 0) f.WeaponSupply--;
                    break;
                case MoveId.BombLob:
                    AddShot(state, ShotKind.Bomb, f.Weapon, i, hand, new FixVec2(Weapons.BombVx * f.Facing, Weapons.BombVy), false);
                    if (f.WeaponSupply > 0) f.WeaponSupply--;
                    break;
                case MoveId.BowShot:
                    AddShot(state, ShotKind.Arrow, f.Weapon, i, hand, new FixVec2(Weapons.ArrowSpeed * f.Facing, Fix.Zero), false);
                    if (f.WeaponSupply > 0) f.WeaponSupply--;
                    break;
                case MoveId.BoomerangThrow:
                    // It leaves the hand carrying the throws it has left, and brings them back.
                    AddShot(state, ShotKind.Boomerang, f.Weapon, i, hand, new FixVec2(Weapons.BoomerangSpeed * f.Facing, Fix.Zero), false,
                        Math32.Max(0, f.WeaponSupply - 1));
                    f.Weapon = WeaponId.None;
                    f.WeaponSupply = 0;
                    break;
                case MoveId.PadPlace:
                    AddShot(state, ShotKind.Pad, f.Weapon, i, new FixVec2(hand.X, f.Position.Y), FixVec2.Zero, f.Grounded);
                    if (f.WeaponSupply > 0) f.WeaponSupply--;
                    break;
                case MoveId.MinePlant:
                    AddShot(state, ShotKind.Mine, f.Weapon, i, new FixVec2(hand.X, f.Position.Y), FixVec2.Zero, f.Grounded);
                    if (f.WeaponSupply > 0) f.WeaponSupply--;
                    break;
                case MoveId.MissileFire:
                    AddShot(state, ShotKind.Missile, f.Weapon, i, hand, new FixVec2(Weapons.MissileSpeed * f.Facing, Fix.Zero), false);
                    if (f.WeaponSupply > 0) f.WeaponSupply--;
                    break;
                case MoveId.HoleLob:
                    AddShot(state, ShotKind.Hole, f.Weapon, i, hand, new FixVec2(Weapons.BombVx * f.Facing, Weapons.BombVy), false);
                    if (f.WeaponSupply > 0) f.WeaponSupply--;
                    break;
            }
        }
    }

    private static void AddShot(GameState state, ShotKind kind, WeaponId weapon, int owner, FixVec2 at, FixVec2 velocity, bool grounded, int supply = 0)
    {
        int slot = FreeSlot(state, forShot: true);
        if (slot < 0) return;
        Fix h = Weapons.ShotHalf(kind);
        state.Props[slot] = new Prop
        {
            Kind = PropKind.Shot,
            Weapon = weapon,
            Shot = kind,
            Owner = owner,
            Supply = supply,
            // The ball, a trampoline and a mine start at the feet; the rest leave the hand.
            Position = new FixVec2(at.X, kind == ShotKind.Roll || kind == ShotKind.Pad || kind == ShotKind.Mine ? at.Y : at.Y - h),
            Velocity = velocity,
            Grounded = grounded,
            Timer = kind switch
            {
                ShotKind.Bomb => Weapons.BombFuse,
                ShotKind.Pad => Weapons.PadFrames,
                ShotKind.Mine => Weapons.MineArmFrames,
                ShotKind.Hole => Weapons.HoleFuse,
                _ => 0,
            },
        };
    }

    /// <summary>
    /// The first empty prop slot. A shot that finds none replaces the oldest bullet; boxes wait.
    /// </summary>
    private static int FreeSlot(GameState state, bool forShot)
    {
        Prop[] props = state.Props;
        for (int i = 0; i < props.Length; i++)
            if (props[i].Kind == PropKind.None) return i;
        if (!forShot) return -1;
        int oldest = -1;
        for (int i = 0; i < props.Length; i++)
        {
            if (props[i].Kind != PropKind.Shot || props[i].Shot != ShotKind.Bullet) continue;
            if (oldest < 0 || props[i].Age > props[oldest].Age) oldest = i;
        }
        return oldest;
    }

    private static void UpdateProps(GameState state, ReadOnlySpan<bool> frozen, MatchRules rules)
    {
        Prop[] props = state.Props;
        for (int i = 0; i < props.Length; i++)
        {
            ref Prop p = ref props[i];
            switch (p.Kind)
            {
                case PropKind.Box:
                case PropKind.Item:
                    if (p.IgnoreTimer > 0 && --p.IgnoreTimer == 0) p.IgnoreFighter = 0;
                    Fall(ref p, rules.Stage, Weapons.ShotGravity, platforms: true);
                    if (++p.Timer >= Weapons.ItemLifeFrames || !rules.Stage.BlastZone.Contains(p.Position)) p = default;
                    break;
                case PropKind.PendingBox:
                    if (--p.Timer <= 0) PlaceBox(state, rules, i, p.Weapon, p.Supply);
                    break;
                case PropKind.Shot:
                    MoveShot(state, i, rules);
                    break;
            }
        }
        ShotHits(state, frozen, rules);
        Pickups(state, rules);
        DropBoxes(state, rules);
    }

    /// <summary>Gravity and one-way platforms for a prop, the way fighters land.</summary>
    private static void Fall(ref Prop p, StageDefinition stage, Fix gravity, bool platforms)
    {
        if (!p.Grounded) p.Velocity.Y = Fix.Max(p.Velocity.Y - gravity, -Weapons.MaxFall);
        Fix prevY = p.Position.Y;
        FixVec2 next = p.Position + p.Velocity;
        bool landed = false;
        Fix landY = Fix.Zero;
        if (platforms && p.Velocity.Y <= Fix.Zero)
        {
            var list = stage.Platforms;
            for (int i = 0; i < list.Count; i++)
            {
                FixRect r = list[i];
                if (next.X < r.MinX || next.X > r.MaxX) continue;
                if (prevY < r.MaxY || next.Y > r.MaxY) continue;
                if (!landed || r.MaxY > landY)
                {
                    landed = true;
                    landY = r.MaxY;
                }
            }
        }
        if (landed)
        {
            next.Y = landY;
            p.Velocity.Y = Fix.Zero;
        }
        p.Grounded = landed;
        p.Position = next;
    }

    private static void MoveShot(GameState state, int index, MatchRules rules)
    {
        ref Prop p = ref state.Props[index];
        p.Age++;
        switch (p.Shot)
        {
            case ShotKind.Roll:
                Fall(ref p, rules.Stage, Weapons.ShotGravity, platforms: true);
                if (p.Age >= Weapons.RollFrames) Rest(ref p, 1); // it stops and lies there: anyone can roll it again
                break;
            case ShotKind.Bullet:
                p.Position = p.Position + p.Velocity;
                if (p.Age >= Weapons.BulletFrames)
                {
                    MissBullet(state, in p);
                    p = default;
                    return;
                }
                break;
            case ShotKind.Arrow:
                p.Velocity.Y = p.Velocity.Y - Weapons.ArrowGravity;
                p.Position = p.Position + p.Velocity;
                if (p.Age >= Weapons.ArrowFrames)
                {
                    p = default;
                    return;
                }
                break;
            case ShotKind.Bomb:
                p.Timer--;
                Fall(ref p, rules.Stage, Weapons.ShotGravity, platforms: true);
                if (p.Grounded) p.Velocity.X = Fix.MoveToward(p.Velocity.X, Fix.Zero, Fix.FromRatio(2, 100));
                break;
            case ShotKind.Boomerang:
                if (!MoveBoomerang(state, ref p)) return;
                break;
            case ShotKind.Pad:
                Fall(ref p, rules.Stage, Weapons.ShotGravity, platforms: true);
                if (--p.Timer <= 0)
                {
                    p = default;
                    return;
                }
                break;
            case ShotKind.Mine:
                Fall(ref p, rules.Stage, Weapons.ShotGravity, platforms: true);
                if (p.Timer > 0) p.Timer--;
                break;
            case ShotKind.Missile:
                SteerMissile(state, ref p, rules);
                p.Position = p.Position + p.Velocity;
                break;
            case ShotKind.Hole:
                p.Timer--;
                Fall(ref p, rules.Stage, Weapons.ShotGravity, platforms: true);
                if (p.Grounded) p.Velocity.X = Fix.MoveToward(p.Velocity.X, Fix.Zero, Fix.FromRatio(2, 100));
                break;
            case ShotKind.Thrown:
                Fall(ref p, rules.Stage, Weapons.ShotGravity, platforms: true);
                if (p.Grounded)
                {
                    p = default; // a thrown weapon is gone once it lands
                    return;
                }
                break;
        }
        if (p.Kind != PropKind.None && !rules.Stage.BlastZone.Contains(p.Position))
        {
            if (p.Shot == ShotKind.Bullet) MissBullet(state, in p);
            p = default;
        }
    }

    /// <summary>
    /// A shot comes to rest as a loose weapon anyone can pick up. It keeps its speed downwards
    /// and whether it was on the ground, as the bowling ball always has.
    /// </summary>
    private static void Rest(ref Prop p, int supply)
    {
        p.Kind = PropKind.Item;
        p.Shot = ShotKind.None;
        p.Supply = supply;
        p.Owner = -1;
        p.OwnerClear = false;
        p.Velocity = FixVec2.Zero;
        p.Timer = 0;
        p.Age = 0;
        p.HitMask = 0;
        p.Hits = 0;
    }

    /// <summary>
    /// The boomerang's flight: out, slowing to a stop (leg 0), then back to its thrower's hand
    /// (leg 1), able to hit everyone again. Not caught in time, or its thrower gone, it drops and
    /// lies there with the throws it has left (an empty one is gone). False when it is gone.
    /// </summary>
    private static bool MoveBoomerang(GameState state, ref Prop p)
    {
        bool owned = p.Owner >= 0 && p.Owner < state.Fighters.Length && state.Fighters[p.Owner].IsActive;
        if (p.Age >= Weapons.BoomerangFrames || (p.Timer != 0 && !owned))
        {
            if (p.Supply > 0)
            {
                Rest(ref p, p.Supply);
                p.Grounded = false;
            }
            else p = default;
            return false;
        }
        if (p.Timer == 0)
        {
            p.Velocity.X = Fix.MoveToward(p.Velocity.X, Fix.Zero, Weapons.BoomerangDrag);
            if (p.Velocity.X == Fix.Zero)
            {
                p.Timer = 1;
                p.HitMask = 0;
            }
        }
        else
        {
            ref Fighter o = ref state.Fighters[p.Owner];
            Fix dx = o.Position.X - p.Position.X;
            Fix dy = o.Position.Y + Weapons.HandHeight - Weapons.ShotHalf(ShotKind.Boomerang) - p.Position.Y;
            p.Velocity.X = Fix.MoveToward(p.Velocity.X, Weapons.BoomerangSpeed * Fix.Sign(dx), Weapons.BoomerangDrag * 2);
            p.Velocity.Y = Fix.Clamp(dy, -Weapons.BoomerangClimb, Weapons.BoomerangClimb);
        }
        p.Position = p.Position + p.Velocity;
        return true;
    }

    /// <summary>
    /// The missile turns a little each frame towards the nearest fighter who isn't its owner (on a
    /// tie, the lower index); with nobody to chase it flies on.
    /// </summary>
    private static void SteerMissile(GameState state, ref Prop p, MatchRules rules)
    {
        Fighter[] fighters = state.Fighters;
        Fix cy = p.Position.Y + Weapons.ShotHalf(ShotKind.Missile);
        int target = -1;
        Fix best = Fix.Zero;
        for (int d = 0; d < fighters.Length; d++)
        {
            if (d == p.Owner || !fighters[d].IsActive) continue;
            Fix dist = Fix.Abs(fighters[d].Position.X - p.Position.X) + Fix.Abs(fighters[d].Position.Y + rules.Tuning.Height / 2 - cy);
            if (target < 0 || dist < best)
            {
                target = d;
                best = dist;
            }
        }
        if (target < 0) return;
        Fix dx = fighters[target].Position.X - p.Position.X;
        Fix dy = fighters[target].Position.Y + rules.Tuning.Height / 2 - cy;
        int up = Fix.Abs(dy) <= Weapons.MissileLevel ? 0 : Fix.Sign(dy);
        p.Velocity.X = Fix.MoveToward(p.Velocity.X, Weapons.MissileSpeed * Fix.Sign(dx), Weapons.MissileTurn);
        p.Velocity.Y = Fix.MoveToward(p.Velocity.Y, Weapons.MissileSpeed * up, Weapons.MissileTurn);
    }

    /// <summary>An Uzi bullet that hit nobody breaks its shooter's streak.</summary>
    private static void MissBullet(GameState state, in Prop p)
    {
        if (p.Hits == 0 && p.Owner >= 0 && p.Owner < state.Fighters.Length) state.Fighters[p.Owner].UziStreak = 0;
    }

    private static void ShotHits(GameState state, ReadOnlySpan<bool> frozen, MatchRules rules)
    {
        Fighter[] fighters = state.Fighters;
        Prop[] props = state.Props;
        FighterTuning t = rules.Tuning;
        for (int i = 0; i < props.Length; i++)
        {
            ref Prop p = ref props[i];
            if (p.Kind != PropKind.Shot) continue;
            FixRect box = PropBox(in p);

            // The release grace: it can't hit its owner until it has stopped touching them once.
            if (!p.OwnerClear && (p.Owner < 0 || !fighters[p.Owner].IsActive || !box.Overlaps(Hurtbox(in fighters[p.Owner], t))))
                p.OwnerClear = true;

            if (p.Shot == ShotKind.Pad)
            {
                Bounce(state, in p, box, rules);
                continue;
            }
            if (p.Shot == ShotKind.Mine)
            {
                TripMine(state, i, box, rules);
                continue;
            }

            // A bat swing bats it back: it becomes the batter's, reversed and 1.25x faster.
            for (int b = 0; b < fighters.Length; b++)
            {
                ref Fighter batter = ref fighters[b];
                if (frozen[b] || batter.Move != MoveId.BatSwing || !TryGetActiveHitbox(in batter, t, out FixRect bat)) continue;
                if (Fix.Sign(p.Velocity.X) == batter.Facing || !bat.Overlaps(box)) continue;
                p.Owner = b;
                p.OwnerClear = false;
                p.HitMask = 0;
                p.Hits = 0;
                p.Velocity = new FixVec2(Fix.Abs(p.Velocity.X) * Weapons.SweetPercent / 100 * batter.Facing, p.Velocity.Y);
                if (p.Shot == ShotKind.Bullet || p.Shot == ShotKind.Arrow) p.Age = 0;
                if (p.Shot == ShotKind.Boomerang) p.Timer = 0; // it flies out again, and comes back to the batter
                break;
            }

            if (((p.Shot == ShotKind.Bomb || p.Shot == ShotKind.Hole) && p.Timer <= 0)
                || (p.Shot == ShotKind.Missile && p.Age >= Weapons.MissileFrames))
            {
                Explode(state, i, -1, rules);
                continue;
            }
            if (p.Shot == ShotKind.Hole)
            {
                // It doesn't burst on touch: for the end of its fuse it pulls everyone near it in.
                if (p.Timer <= Weapons.HolePullFrames) Pull(state, in p, rules);
                continue;
            }

            for (int d = 0; d < fighters.Length; d++)
            {
                ref Fighter def = ref fighters[d];
                if (!def.IsActive || (p.HitMask & (1 << d)) != 0) continue;
                if (d == p.Owner && !p.OwnerClear) continue;
                if (!box.Overlaps(Hurtbox(in def, t))) continue;

                if (p.Shot == ShotKind.Boomerang && d == p.Owner)
                {
                    // Back to the hand: caught on the way back, empty-handed. Otherwise it flies past.
                    if (p.Timer == 0 || def.Weapon != WeaponId.None) continue;
                    def.Weapon = p.Weapon;
                    def.WeaponSupply = p.Supply;
                    def.UziShots = 0;
                    def.UziStreak = 0;
                    def.UziTarget = 0;
                    p = default;
                    break;
                }
                if (p.Shot == ShotKind.Bomb || p.Shot == ShotKind.Missile)
                {
                    if (d == p.Owner) continue; // it bursts on touching anyone but its thrower
                    Explode(state, i, d, rules);
                    break;
                }
                if (InParryWindow(in def, t))
                {
                    ParryReward(ref def, rules); // parrying a projectile destroys it
                    p = default;
                    break;
                }
                if (IsIntangible(in def, t)) continue; // dodged: it flies on

                p.HitMask |= 1 << d;
                ShotHit(state, i, d, rules);
                p.Hits++;
                if (p.Shot == ShotKind.Bullet || p.Shot == ShotKind.Thrown)
                {
                    p = default;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// A trampoline bounces up everyone touching it who isn't already rising, its owner too once
    /// they have stepped off it. It does no damage and credits nobody.
    /// </summary>
    private static void Bounce(GameState state, in Prop p, FixRect box, MatchRules rules)
    {
        Fighter[] fighters = state.Fighters;
        for (int d = 0; d < fighters.Length; d++)
        {
            ref Fighter f = ref fighters[d];
            if (!f.IsActive || IsIntangible(in f, rules.Tuning) || f.Action == FighterAction.Hitstun) continue;
            if (d == p.Owner && !p.OwnerClear) continue;
            if (f.Velocity.Y > Fix.Zero || !box.Overlaps(Hurtbox(in f, rules.Tuning))) continue;
            f.Velocity.Y = Weapons.PadLaunch;
            f.Grounded = false;
        }
    }

    /// <summary>
    /// An armed mine bursts on the first fighter touching it, its planter included. One that
    /// nobody found bursts by itself after 15 s.
    /// </summary>
    private static void TripMine(GameState state, int index, FixRect box, MatchRules rules)
    {
        if (state.Props[index].Age >= Weapons.MineFrames)
        {
            Explode(state, index, -1, rules);
            return;
        }
        if (state.Props[index].Timer > 0) return;
        int owner = state.Props[index].Owner;
        bool ownerClear = state.Props[index].OwnerClear;
        Fighter[] fighters = state.Fighters;
        for (int d = 0; d < fighters.Length; d++)
        {
            ref Fighter f = ref fighters[d];
            if (!f.IsActive || IsIntangible(in f, rules.Tuning)) continue;
            if (d == owner && !ownerClear) continue;
            if (!box.Overlaps(Hurtbox(in f, rules.Tuning))) continue;
            Explode(state, index, d, rules);
            return;
        }
    }

    /// <summary>The black hole drags everyone within reach sideways towards it.</summary>
    private static void Pull(GameState state, in Prop p, MatchRules rules)
    {
        Fighter[] fighters = state.Fighters;
        Fix cy = p.Position.Y + Weapons.ShotHalf(ShotKind.Hole);
        for (int d = 0; d < fighters.Length; d++)
        {
            ref Fighter f = ref fighters[d];
            if (!f.IsActive || IsIntangible(in f, rules.Tuning)) continue;
            Fix dx = p.Position.X - f.Position.X;
            if (Fix.Abs(dx) > Weapons.HoleReach || Fix.Abs(f.Position.Y + rules.Tuning.Height / 2 - cy) > Weapons.HoleReach) continue;
            f.Position.X += Fix.Clamp(dx, -Weapons.HolePull, Weapons.HolePull);
        }
    }

    /// <summary>
    /// A projectile hits a fighter, its owner included. Sweet spots (never on yourself): the ball's
    /// second and later fighters ("Strike!"), the Uzi from the 6th bullet in a row on one target,
    /// an arrow that flew far (a long shot), a thrown weapon on an airborne target.
    /// </summary>
    private static void ShotHit(GameState state, int index, int target, MatchRules rules)
    {
        ref Prop p = ref state.Props[index];
        Fighter[] fighters = state.Fighters;
        int owner = p.Owner >= 0 ? p.Owner : target;
        bool self = owner == target;
        MoveId move = p.Shot switch
        {
            ShotKind.Roll => MoveId.BallRoll,
            ShotKind.Bullet => MoveId.UziFire,
            ShotKind.Arrow => MoveId.BowShot,
            ShotKind.Boomerang => MoveId.BoomerangThrow,
            _ => MoveId.WeaponThrow,
        };
        var hit = new Hit(rules.Tuning.Moves[move]);
        switch (p.Shot)
        {
            case ShotKind.Roll:
                if (!self && p.Hits >= 1) hit.Sweeten();
                break;
            case ShotKind.Bullet:
                hit.NoHitStop = true;
                if (!self)
                {
                    ref Fighter shooter = ref fighters[owner];
                    if (shooter.UziTarget == target + 1) shooter.UziStreak++;
                    else
                    {
                        shooter.UziTarget = target + 1;
                        shooter.UziStreak = 1;
                    }
                    if (shooter.UziStreak >= Weapons.UziStreakSweet)
                    {
                        hit.Damage = 3; // keeping on target: the stream holds them
                        hit.HitstunBonus = 0;
                        hit.Sweet = true;
                    }
                }
                break;
            case ShotKind.Arrow:
                if (!self && p.Age >= Weapons.LongShotFrames) hit.Sweeten();
                break;
            case ShotKind.Boomerang:
                if (!self && p.Timer != 0) hit.Sweeten(); // caught by it on its way back
                break;
            case ShotKind.Thrown:
                hit.Damage = Weapons.Get(p.Weapon).ThrowDamage;
                if (!self && !fighters[target].Grounded) hit.Sweeten();
                break;
        }
        if (rules.SignatureOf(owner) == p.Weapon) hit.Percent = hit.Percent * Weapons.SignaturePercent / 100;
        hit.Weapon = p.Weapon;
        int side = p.Velocity.X < Fix.Zero ? -1 : 1;
        LandHit(state, owner, target, in hit, side, freezeAttacker: false, rules);
    }

    /// <summary>
    /// A cherry bomb, mine, missile or black hole bursts: a 2.4 m box (the hole's is 3.6 m) that
    /// hits everyone in it, its thrower too, launching them away from its centre. A fighter it
    /// burst on is a direct hit (a sweet spot).
    /// </summary>
    private static void Explode(GameState state, int index, int direct, MatchRules rules)
    {
        Prop p = state.Props[index];
        state.Props[index] = default;
        Fighter[] fighters = state.Fighters;
        Fix cy = p.Position.Y + Weapons.ShotHalf(p.Shot);
        Fix half = p.Shot == ShotKind.Hole ? Weapons.HoleBlastHalf : Weapons.BlastHalf;
        MoveData burst = p.Shot switch
        {
            ShotKind.Mine => Weapons.MinePlant,
            ShotKind.Missile => Weapons.MissileFire,
            ShotKind.Hole => Weapons.HoleLob,
            _ => Weapons.BombLob,
        };
        var blast = new FixRect(p.Position.X - half, cy - half, p.Position.X + half, cy + half);
        int owner = p.Owner;
        for (int d = 0; d < fighters.Length; d++)
        {
            ref Fighter def = ref fighters[d];
            if (!def.IsActive || IsIntangible(in def, rules.Tuning)) continue;
            if (!blast.Overlaps(Hurtbox(in def, rules.Tuning))) continue;
            int from = owner >= 0 ? owner : d;
            var hit = new Hit(burst);
            if (d == direct && from != d) hit.Sweeten();
            if (rules.SignatureOf(from) == p.Weapon) hit.Percent = hit.Percent * Weapons.SignaturePercent / 100;
            hit.Weapon = p.Weapon;
            int side = def.Position.X >= p.Position.X ? 1 : -1;
            LandHit(state, from, d, in hit, side, freezeAttacker: false, rules);
        }
    }

    /// <summary>
    /// Touch to take: a box or loose weapon goes to the fighter touching it (nearest its centre;
    /// on a tie, the lower index). Their old weapon drops where they stand and ignores them for 1 s.
    /// Fighters mid-attack don't pick up.
    /// </summary>
    private static void Pickups(GameState state, MatchRules rules)
    {
        Fighter[] fighters = state.Fighters;
        Prop[] props = state.Props;
        for (int i = 0; i < props.Length; i++)
        {
            ref Prop p = ref props[i];
            if (p.Kind != PropKind.Box && p.Kind != PropKind.Item) continue;
            FixRect box = PropBox(in p);
            Fix cy = p.Position.Y + Weapons.BoxHalf;
            int best = -1;
            Fix bestDist = Fix.Zero;
            for (int d = 0; d < fighters.Length; d++)
            {
                ref Fighter f = ref fighters[d];
                if (!f.IsActive || f.Action == FighterAction.Attack || p.IgnoreFighter == d + 1) continue;
                if (!box.Overlaps(Hurtbox(in f, rules.Tuning))) continue;
                Fix dist = Fix.Abs(f.Position.X - p.Position.X) + Fix.Abs(f.Position.Y + rules.Tuning.Height / 2 - cy);
                if (best < 0 || dist < bestDist)
                {
                    best = d;
                    bestDist = dist;
                }
            }
            if (best < 0) continue;

            ref Fighter taker = ref fighters[best];
            WeaponId weapon = p.Weapon;
            int supply = p.Supply;
            p = default;
            if (taker.Weapon != WeaponId.None && (taker.WeaponSupply > 0 || taker.Weapon == WeaponId.BowlingBall))
            {
                p.Kind = PropKind.Item;
                p.Weapon = taker.Weapon;
                p.Supply = taker.WeaponSupply;
                p.Owner = -1;
                p.Position = taker.Position;
                p.Grounded = taker.Grounded;
                p.IgnoreFighter = best + 1;
                p.IgnoreTimer = Weapons.NoSwapBackFrames;
            }
            taker.Weapon = weapon;
            taker.WeaponSupply = supply;
            taker.UziShots = 0;
            taker.UziStreak = 0;
            taker.UziTarget = 0;
        }
    }

    /// <summary>Drops a new Prop Box when one is due and there's room under the cap.</summary>
    private static void DropBoxes(GameState state, MatchRules rules)
    {
        int onStage = 0;
        Prop[] props = state.Props;
        for (int i = 0; i < props.Length; i++)
            if (props[i].Kind == PropKind.Box || props[i].Kind == PropKind.Item || props[i].Kind == PropKind.PendingBox) onStage++;
        if (onStage >= Weapons.Cap(state.Fighters.Length)) return; // the timer waits while the cap is full

        if (state.PropTimer > 0) state.PropTimer--;
        if (state.PropTimer > 0) return;
        int slot = FreeSlot(state, forShot: false);
        if (slot < 0) return;

        // Any weapon but the last box's, with equal odds.
        WeaponId weapon;
        if (state.LastBoxWeapon == WeaponId.None) weapon = (WeaponId)state.Random.Range(1, Weapons.Count);
        else
        {
            int r = state.Random.Range(1, Weapons.Count - 1);
            if (r >= (int)state.LastBoxWeapon) r++;
            weapon = (WeaponId)r;
        }
        PlaceBox(state, rules, slot, weapon, Weapons.Get(weapon).Supply);
        state.LastBoxWeapon = weapon;
        state.PropTimer = state.Random.Range(Weapons.BoxIntervalMin, Weapons.BoxIntervalMax);
    }

    /// <summary>
    /// A box above a random point on a platform (chosen by width, at least 0.5 m from its ends),
    /// 6 m up, so it lands quickly.
    /// </summary>
    private static void PlaceBox(GameState state, MatchRules rules, int slot, WeaponId weapon, int supply)
    {
        var platforms = rules.Stage.Platforms;
        Fix minWidth = Weapons.EdgeMargin * 2 + Fix.One;
        int total = 0;
        for (int i = 0; i < platforms.Count; i++)
        {
            Fix w = platforms[i].MaxX - platforms[i].MinX;
            if (w >= minWidth) total += (int)(w.Raw >> 12);
        }
        int chosen = 0;
        if (total > 0)
        {
            int roll = state.Random.Range(0, total);
            for (int i = 0; i < platforms.Count; i++)
            {
                Fix w = platforms[i].MaxX - platforms[i].MinX;
                if (w < minWidth) continue;
                roll -= (int)(w.Raw >> 12);
                if (roll < 0)
                {
                    chosen = i;
                    break;
                }
            }
        }
        FixRect p = platforms[chosen];
        Fix span = p.MaxX - p.MinX - Weapons.EdgeMargin * 2;
        Fix x = span > Fix.Zero ? p.MinX + Weapons.EdgeMargin + Fix.FromRaw(state.Random.Range(0, (int)span.Raw)) : (p.MinX + p.MaxX) / 2;
        Fix y = Fix.Min(p.MaxY + Weapons.BoxDropHeight, rules.Stage.BlastZone.MaxY - Fix.FromInt(2));
        state.Props[slot] = new Prop
        {
            Kind = PropKind.Box,
            Weapon = weapon,
            Supply = supply,
            Owner = -1,
            Position = new FixVec2(x, y),
        };
    }

    /// <summary>
    /// A fighter leaving the blast zone with a weapon: it comes back in a box 1 s later, with the
    /// ammo or durability it had left (an empty one doesn't come back).
    /// </summary>
    private static void ReturnOnFall(GameState state, int index, MatchRules rules)
    {
        ref Fighter f = ref state.Fighters[index];
        if (!f.IsActive || f.Weapon == WeaponId.None || rules.Stage.BlastZone.Contains(f.Position)) return;
        if (f.WeaponSupply > 0 || f.Weapon == WeaponId.BowlingBall)
        {
            int slot = FreeSlot(state, forShot: false);
            if (slot >= 0)
                state.Props[slot] = new Prop { Kind = PropKind.PendingBox, Weapon = f.Weapon, Supply = f.WeaponSupply, Owner = -1, Timer = Weapons.ReturnFrames };
        }
        f.Weapon = WeaponId.None;
        f.WeaponSupply = 0;
    }
}
