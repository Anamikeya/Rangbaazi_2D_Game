using System;
using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim;

/// <summary>
/// Weapons in the step function (Weapons.cs has the data): Prop Boxes dropping, picking up and
/// swapping, the weapons' shots, projectiles flying, hitting and exploding, the bat batting them
/// back, and a dead fighter's weapon coming back in a box. Runs only when MatchRules.Weapons is on.
/// Every weapon's numbers come from its row of the table; this file branches on a row's use, its
/// shot's flight and its other columns, never on which weapon it is.
/// </summary>
public static partial class Simulation
{
    /// <summary>
    /// A prop's box: <see cref="Prop.Position"/> is its bottom centre. A zone or a wall is as wide
    /// and as tall as its row says; every other prop is a square.
    /// </summary>
    public static FixRect PropBox(in Prop p)
    {
        if (p.Kind != PropKind.Shot)
            return new FixRect(p.Position.X - Weapons.BoxHalf, p.Position.Y, p.Position.X + Weapons.BoxHalf, p.Position.Y + Weapons.BoxHalf * 2);
        if (p.Shot == ShotKind.Zone || p.Shot == ShotKind.Wall)
        {
            WeaponRow row = Weapons.ShotRow(in p);
            return new FixRect(p.Position.X - row.ZoneW / 2, p.Position.Y, p.Position.X + row.ZoneW / 2, p.Position.Y + row.ZoneH);
        }
        Fix h = Weapons.ShotHalf(in p);
        return new FixRect(p.Position.X - h, p.Position.Y, p.Position.X + h, p.Position.Y + h * 2);
    }

    /// <summary>
    /// Weapon moves act on their startup frame: a throw lets go of the weapon, a blast spends a
    /// shell, and a row that shoots releases its shot.
    /// </summary>
    private static void FireShots(GameState state, ReadOnlySpan<bool> frozen, MatchRules rules)
    {
        Fighter[] fighters = state.Fighters;
        for (int i = 0; i < fighters.Length; i++)
        {
            ref Fighter f = ref fighters[i];
            if (frozen[i] || f.Action != FighterAction.Attack) continue;
            bool thrown = f.Move == MoveId.WeaponThrow;
            WeaponRow? row = Weapons.RowOfMove(f.Move);
            if (!thrown && row == null) continue;
            if (f.ActionFrame != rules.Tuning.Moves[f.Move].Startup || f.Weapon == WeaponId.None) continue;

            var hand = new FixVec2(f.Position.X + Weapons.HandReach * f.Facing, f.Position.Y + Weapons.HandHeight);
            if (row == null)
            {
                // A weapon thrown as a hidden row (the Gas Cylinder, Charpai and Spear) fires that
                // row's shot instead of a plain thrown prop.
                int thrownAs = WeaponTable.Rows[(int)f.Weapon].ThrownAs;
                bool placed;
                if (thrownAs > 0)
                {
                    WeaponRow shot = WeaponTable.Rows[thrownAs];
                    placed = AddShot(state, shot.Flight, (WeaponId)thrownAs, i, new FixVec2(hand.X, hand.Y - shot.ShotHalf),
                        new FixVec2(shot.Vx * f.Facing, shot.Vy), false, shot.Fuse, 0);
                }
                else placed = AddShot(state, ShotKind.Thrown, f.Weapon, i, new FixVec2(hand.X, hand.Y - Weapons.ThrownHalf),
                        new FixVec2(Weapons.ThrowVx * f.Facing, Weapons.ThrowVy), false, 0, 0);
                if (!placed) continue; // no room for it: it stays in the hand
                f.Weapon = WeaponId.None;
                f.WeaponSupply = 0;
                continue;
            }
            if (row.Use == WeaponUse.Swing)
            {
                // A swing that is spent per use (the Thunder Hammer, the Gauntlet's punch), hit or miss.
                if (row.Spend == WeaponSpend.Use && f.WeaponSupply > 0) f.WeaponSupply--;
            }
            else if (row.Use == WeaponUse.Blast)
            {
                if (row.Spend == WeaponSpend.Use && f.WeaponSupply > 0) f.WeaponSupply--;
            }
            else if (row.Use == WeaponUse.Spray)
            {
                // Each pass through the startup frame is a tick of the stream: it spends one.
                if (row.Spend == WeaponSpend.Use && f.WeaponSupply > 0) f.WeaponSupply--;
                f.BurstShots++;
            }
            else if (row.Use == WeaponUse.Shoot) Shoot(state, ref f, i, row, hand, rules);
            else if (row.Use == WeaponUse.Detonate) Detonate(state, ref f, i, row, hand, rules);
            else if (row.Use == WeaponUse.Blink) Blink(ref f, row, rules);
            if (row.Fires > 0)
            {
                // A row that also fires another row's shot beside its own attack (the Thunder
                // Hammer's wave), with that row's own numbers.
                WeaponRow also = WeaponTable.Rows[row.Fires];
                var at = also.Feet ? new FixVec2(hand.X, f.Position.Y) : new FixVec2(hand.X, hand.Y - also.ShotHalf);
                AddShot(state, also.Flight, (WeaponId)row.Fires, i, at, new FixVec2(also.Vx * f.Facing, also.Vy), also.Feet && f.Grounded, also.Fuse, 0);
            }
        }
    }

    /// <summary>
    /// Releases a row's shot: from the hand, or at the feet. A use fires the row's pellets (one,
    /// or the Ninja Stars' three), each with the upward speed its spread adds. The use is spent
    /// once; a weapon that leaves the hand goes with the shot, carrying what it has left. A use
    /// there is no room for fizzles: nothing is spent, and a weapon that would have left the hand
    /// stays in it. A fizzled shot of a repeating weapon still counts as a shot of its burst, so
    /// the burst ends. A gate opens only on a safe spot (<see cref="SafeSpot"/>): anywhere else
    /// the use fizzles too. A speaker keeps its wielder's facing in its supply, and a decoy the
    /// weapon it pretends to hold.
    /// </summary>
    private static void Shoot(GameState state, ref Fighter f, int index, WeaponRow row, FixVec2 hand, MatchRules rules)
    {
        int shot = f.BurstShots; // which shot of this burst it is, from 0
        if (row.Interval > 0) f.BurstShots++;
        FixVec2 at = ShotStart(state, rules, index, row, hand);
        if (row.Flight == ShotKind.Gate && !SafeSpot(state, rules, at)) return; // no gate over the pit or by a blast line
        int carried = row.Leave && row.Spend == WeaponSpend.Use ? Math32.Max(0, f.WeaponSupply - 1) : 0;
        if (row.Flight == ShotKind.Speaker) carried = f.Facing;
        else if (row.Flight == ShotKind.Decoy) carried = (int)Weapons.DecoyLooks(state);
        // A hidden row's shot (the Gauntlet's bolt) carries the hidden row's id, so it finds its own
        // numbers; its hits are credited to the weapon (the row's credit).
        WeaponId weapon = row.Credit != row.Id ? row.Id : f.Weapon;
        bool placed = false;
        for (int pellet = 0; pellet < row.Pellets; pellet++)
        {
            Fix vy = row.Vy + Spread(row, shot, pellet, f.WeaponSupply);
            placed |= AddShot(state, row.Flight, weapon, index, at, new FixVec2(row.Vx * f.Facing, vy), row.Feet && f.Grounded, Weapons.StartTimer(row), carried);
        }
        if (!placed) return;
        if (row.Leave)
        {
            f.Weapon = WeaponId.None; // it has left the hand: it comes back, or lies there when it stops
            f.WeaponSupply = 0;
        }
        else if (row.Spend == WeaponSpend.Use && f.WeaponSupply > 0) f.WeaponSupply--;
    }

    /// <summary>
    /// Where a row's shot starts: from the hand, or at the feet for a row that rolls or is put
    /// down. A gate opens where the wielder stands. An anvil hangs over its mark (<see cref="DropSpot"/>).
    /// </summary>
    private static FixVec2 ShotStart(GameState state, MatchRules rules, int index, WeaponRow row, FixVec2 hand)
    {
        ref Fighter f = ref state.Fighters[index];
        if (row.Flight == ShotKind.Drop) return DropSpot(state, rules, index);
        if (row.Flight == ShotKind.Gate) return f.Position;
        return row.Feet ? new FixVec2(hand.X, f.Position.Y) : new FixVec2(hand.X, hand.Y - row.ShotHalf);
    }

    /// <summary>
    /// Where an anvil appears. Its mark is over the nearest opponent in front of its wielder (within
    /// 8 m ahead and 9 m up or down, not a teammate, not hidden in a cloud), where they will be
    /// <see cref="Weapons.DropLead"/> frames on at their speed, but 2 to 6 m ahead; with nobody to
    /// aim at, 4 m ahead. It is kept at least 2 m inside the side blast lines, and 9 m above the
    /// wielder's feet or 2 m under the top blast line, whichever is lower (the rule a Prop Box
    /// follows), so it is never lost as it appears.
    /// </summary>
    public static FixVec2 DropSpot(GameState state, MatchRules rules, int index)
    {
        Fighter[] fighters = state.Fighters;
        ref Fighter f = ref fighters[index];
        Fix ahead = Weapons.DropAhead;
        int target = -1;
        Fix nearest = Fix.Zero;
        for (int d = 0; d < fighters.Length; d++)
        {
            ref Fighter t = ref fighters[d];
            if (Allies(rules, index, d) || !t.IsActive) continue;
            Fix dx = (t.Position.X - f.Position.X) * f.Facing;
            if (dx <= Fix.Zero || dx > Weapons.DropSeek || Fix.Abs(t.Position.Y - f.Position.Y) > Weapons.DropHeight || IsHidden(state, rules, d)) continue;
            if (target < 0 || dx < nearest)
            {
                target = d;
                nearest = dx;
            }
        }
        if (target >= 0)
        {
            ref Fighter t = ref fighters[target];
            ahead = Fix.Clamp((t.Position.X + t.Velocity.X * Weapons.DropLead - f.Position.X) * f.Facing, Weapons.DropMin, Weapons.DropMax);
        }
        FixRect zone = rules.Stage.BlastZone;
        Fix x = Fix.Clamp(f.Position.X + ahead * f.Facing, zone.MinX + Weapons.BlastMargin, zone.MaxX - Weapons.BlastMargin);
        Fix y = Fix.Min(f.Position.Y + Weapons.DropHeight, zone.MaxY - Weapons.BlastMargin);
        return new FixVec2(x, y);
    }

    /// <summary>
    /// The Crate Detonator (use <c>detonate</c>), on its startup frame. If the wielder owns any
    /// decoy, rigged crate or Decoy Crate, every one of them bursts with the Detonator's numbers
    /// (its owner too, if they stand in it). Otherwise it puts down a rigged crate: a decoy that
    /// doesn't spring. Either way the press spends one; a crate with no slot for it fizzles.
    /// </summary>
    private static void Detonate(GameState state, ref Fighter f, int index, WeaponRow row, FixVec2 hand, MatchRules rules)
    {
        Prop[] props = state.Props;
        bool burst = false;
        for (int k = 0; k < props.Length; k++)
        {
            if (props[k].Kind != PropKind.Shot || props[k].Shot != ShotKind.Decoy || props[k].Owner != index) continue;
            props[k].Weapon = row.Id; // it bursts with the Detonator's numbers, credited to the Detonator
            Explode(state, k, -1, rules);
            burst = true;
        }
        if (!burst && !AddShot(state, ShotKind.Decoy, row.Id, index, new FixVec2(hand.X, f.Position.Y), FixVec2.Zero, f.Grounded,
                row.Life, (int)Weapons.DecoyLooks(state)))
            return;
        if (f.WeaponSupply > 0) f.WeaponSupply--;
    }

    /// <summary>
    /// The Teleporter (use <c>blink</c>), on its startup frame: the wielder is put on the landing
    /// spot <see cref="BlinkSpot"/> finds, standing still, with the air jump, air dodge and recovery
    /// back as any landing gives, and a root ends. It spends one. With no landing spot it fizzles.
    /// Up held on this frame looks above first (Up + Light; Up + Heavy is still the recovery).
    /// </summary>
    private static void Blink(ref Fighter f, WeaponRow row, MatchRules rules)
    {
        bool up = (f.PreviousButtons & Buttons.Up) != 0; // this frame's buttons: a fighter not frozen has just stored them
        if (!BlinkSpot(rules.Stage, in f, row.Reach, up, out FixVec2 spot)) return;
        f.Position = spot;
        f.Velocity = FixVec2.Zero;
        f.Grounded = true;
        f.AirJumpsLeft = rules.Tuning.AirJumps;
        f.AirDodgeUsed = false;
        f.RecoveryUsed = false;
        f.RootFrames = 0; // a blink ends a root
        f.StunWeapon = 0;
        if (f.WeaponSupply > 0) f.WeaponSupply--;
    }

    /// <summary>
    /// The Teleporter's landing spot: the point <paramref name="reach"/> metres ahead, then 1 m
    /// nearer each time down to 2 m, on a platform top within 6 m of the feet (the nearest in
    /// height, the higher on a tie). <paramref name="up"/> looks straight above first, and takes
    /// only tops more than 0.5 m above the feet. False when there is nowhere to land.
    /// </summary>
    public static bool BlinkSpot(StageDefinition stage, in Fighter f, Fix reach, bool up, out FixVec2 spot)
    {
        if (up && LandingAt(stage, f.Position.X, f.Position.Y, above: true, out spot)) return true;
        for (Fix d = reach; d >= Weapons.BlinkMin; d -= Fix.One)
            if (LandingAt(stage, f.Position.X + d * f.Facing, f.Position.Y, up, out spot)) return true;
        spot = default;
        return false;
    }

    /// <summary>The platform top at <paramref name="x"/> nearest the feet in height (within 6 m; above them, for an upward blink).</summary>
    private static bool LandingAt(StageDefinition stage, Fix x, Fix feet, bool above, out FixVec2 spot)
    {
        bool found = false;
        Fix best = Fix.Zero, top = Fix.Zero;
        var platforms = stage.Platforms;
        for (int i = 0; i < platforms.Count; i++)
        {
            FixRect r = platforms[i];
            if (x < r.MinX || x > r.MaxX) continue;
            Fix rise = r.MaxY - feet;
            if (Fix.Abs(rise) > Weapons.BlinkHeight || (above && rise <= Weapons.BlinkAbove)) continue;
            Fix gap = Fix.Abs(rise);
            if (!found || gap < best || (gap == best && r.MaxY > top))
            {
                found = true;
                best = gap;
                top = r.MaxY;
            }
        }
        spot = new FixVec2(x, top);
        return found;
    }

    /// <summary>
    /// A safe spot (combat spec v0.4, section 1.6), for the Portal Disc and the Swap Ray: at least
    /// 2 m inside the blast zone on every side, a platform top straight below within 4 m, and, with
    /// hazards on, out of every hazard that is warning or active (a fighter standing there would
    /// not touch it).
    /// </summary>
    public static bool SafeSpot(GameState state, MatchRules rules, FixVec2 point)
    {
        FixRect zone = rules.Stage.BlastZone;
        Fix m = Weapons.SafeMargin;
        if (point.X < zone.MinX + m || point.X > zone.MaxX - m || point.Y < zone.MinY + m || point.Y > zone.MaxY - m) return false;
        if (!TopBelow(rules.Stage, point, out Fix top) || point.Y - top > Weapons.SafeDrop) return false;
        if (!rules.Hazards) return true;
        FighterTuning t = rules.Tuning;
        var body = new FixRect(point.X - t.HalfWidth, point.Y, point.X + t.HalfWidth, point.Y + t.Height);
        var hazards = rules.Stage.Hazards;
        for (int k = 0; k < hazards.Count; k++)
        {
            int at = Hazards.CycleAt(state.Frame, k);
            if (at >= Hazards.WarnAt && at < Hazards.CooldownAt && hazards[k].Zone.Overlaps(body)) return false;
        }
        return true;
    }

    /// <summary>The highest platform top straight below a point (or at it), if there is one.</summary>
    public static bool TopBelow(StageDefinition stage, FixVec2 point, out Fix top)
    {
        bool found = false;
        top = Fix.Zero;
        var platforms = stage.Platforms;
        for (int i = 0; i < platforms.Count; i++)
        {
            FixRect r = platforms[i];
            if (point.X < r.MinX || point.X > r.MaxX || r.MaxY > point.Y) continue;
            if (!found || r.MaxY > top)
            {
                found = true;
                top = r.MaxY;
            }
        }
        return found;
    }

    /// <summary>
    /// Sprays and beams (combat spec v0.4, section 2.5), after <see cref="ResolveHits"/>. Every
    /// stream is read from the state before any of them acts, so two wielders spraying each other
    /// don't depend on their indices. Each frame a stream's box is out it touches every other active
    /// fighter in it who isn't dodging or a teammate (only the nearest for a row with <c>first</c>;
    /// only armed ones for <c>armedOnly</c>): <c>drift</c> moves them sideways (a pull stops 1 m in
    /// front; on someone in the air it is capped at 60% of air speed, so a gust at a ledge can slow
    /// a recovery but never hold it out), and <c>lift</c> floats them up. On a tick frame (each pass
    /// through the startup frame) the row's hit lands too, within <c>tickNear</c> when the row has
    /// it, without freezing the wielder. <c>propDrift</c> moves boxes and loose weapons, and
    /// <c>blow</c> turns round every flying shot of someone else's side that comes at the wielder.
    /// Sprays never break props.
    /// </summary>
    private static void Sprays(GameState state, ReadOnlySpan<bool> frozen, MatchRules rules)
    {
        Fighter[] fighters = state.Fighters;
        FighterTuning t = rules.Tuning;
        int n = fighters.Length;
        Span<int> who = stackalloc int[n];
        Span<FixRect> boxes = stackalloc FixRect[n];
        Span<Fix> xs = stackalloc Fix[n];
        Span<int> facings = stackalloc int[n];
        Span<bool> ticks = stackalloc bool[n];
        Span<byte> moves = stackalloc byte[n];
        int count = 0;
        for (int a = 0; a < n; a++)
        {
            ref Fighter f = ref fighters[a];
            if (frozen[a]) continue;
            WeaponRow? row = Weapons.RowOfMove(f.Move);
            if (row == null || row.Use != WeaponUse.Spray || !TryGetActiveHitbox(in f, t, out FixRect box)) continue;
            who[count] = a;
            boxes[count] = box;
            xs[count] = f.Position.X;
            facings[count] = f.Facing;
            ticks[count] = f.ActionFrame == row.Startup;
            moves[count] = (byte)f.Move;
            count++;
        }

        for (int k = 0; k < count; k++)
        {
            int a = who[k];
            WeaponRow row = Weapons.RowOfMove((MoveId)moves[k])!;
            FixRect box = boxes[k];
            Fix ax = xs[k];
            int facing = facings[k];

            int only = -1;
            if (row.First)
            {
                Fix best = Fix.Zero;
                for (int d = 0; d < n; d++)
                {
                    if (!SprayTouches(state, rules, a, d, row, box, ax)) continue;
                    Fix dist = Fix.Abs(fighters[d].Position.X - ax);
                    if (only < 0 || dist < best)
                    {
                        only = d;
                        best = dist;
                    }
                }
            }

            for (int d = 0; d < n; d++)
            {
                if (row.First ? d != only : !SprayTouches(state, rules, a, d, row, box, ax)) continue;
                ref Fighter def = ref fighters[d];
                if (row.Drift != Fix.Zero)
                {
                    Fix step = row.Drift;
                    if (!def.Grounded)
                    {
                        Fix cap = t.AirSpeed * Weapons.AirDriftPercent / 100;
                        step = Fix.Clamp(step, -cap, cap);
                    }
                    def.Position.X += DriftBy((def.Position.X - ax) * facing, step) * facing;
                }
                if (row.Lift != Fix.Zero)
                {
                    if (def.Velocity.Y < row.Lift) def.Velocity.Y = row.Lift;
                    def.Grounded = false;
                }
                if (!ticks[k] || (row.Damage == 0 && row.Effect == WeaponEffect.None)) continue;
                if (row.TickNear != Fix.Zero && Fix.Abs(def.Position.X - ax) > row.TickNear) continue;
                var hit = new Hit(row.MoveData);
                hit.Flags(row);
                WeaponId weapon = fighters[a].Weapon;
                if (weapon != WeaponId.None && rules.SignatureOf(a) == weapon) hit.Percent = hit.Percent * Weapons.SignaturePercent / 100;
                hit.Weapon = weapon;
                LandHit(state, a, d, in hit, facing, freezeAttacker: false, rules);
            }

            if (row.PropDrift == Fix.Zero && !row.Blow) continue;
            Prop[] props = state.Props;
            for (int i = 0; i < props.Length; i++)
            {
                ref Prop p = ref props[i];
                if (p.Kind == PropKind.Box || p.Kind == PropKind.Item)
                {
                    if (row.PropDrift == Fix.Zero || !box.Overlaps(PropBox(in p))) continue;
                    p.Position.X += DriftBy((p.Position.X - ax) * facing, row.PropDrift) * facing;
                }
                else if (p.Kind == PropKind.Shot && row.Blow && WeaponTable.Flies(p.Shot))
                {
                    if (p.Owner >= 0 && Allies(rules, a, p.Owner)) continue;
                    if (Fix.Sign(p.Velocity.X) != -facing || !box.Overlaps(PropBox(in p))) continue;
                    p.Velocity.X = -p.Velocity.X; // blown round: it flies away from the wielder, still its owner's
                }
            }
        }
    }

    /// <summary>
    /// How far a spray's drift moves something <paramref name="ahead"/> metres in front of the
    /// wielder, along the way they face: a push all of it, a pull no nearer than 1 m.
    /// </summary>
    private static Fix DriftBy(Fix ahead, Fix step)
    {
        if (step >= Fix.Zero) return step;
        return ahead > Weapons.PullStop ? Fix.Max(Weapons.PullStop, ahead + step) - ahead : Fix.Zero;
    }

    /// <summary>
    /// A fighter a spray's stream reaches: active, not dodging, not on the wielder's side, in the
    /// box (armed, for armedOnly), with no wall in between and not holding the Parat's guard
    /// towards the wielder (a plain guard doesn't stop a stream).
    /// </summary>
    private static bool SprayTouches(GameState state, MatchRules rules, int a, int d, WeaponRow row, FixRect box, Fix ax)
    {
        ref Fighter def = ref state.Fighters[d];
        if (Allies(rules, a, d) || !def.IsActive || IsIntangible(in def, rules.Tuning)) return false;
        if (row.ArmedOnly && def.Weapon == WeaponId.None) return false;
        if (!box.Overlaps(Hurtbox(in def, rules.Tuning))) return false;
        if (Guarding(in def) && Weapons.RowOfMove(def.Move)!.Defence == WeaponDefence.Hold && FacesToward(in def, ax)) return false;
        return !WallBetween(state, ax, def.Position.X, box);
    }

    /// <summary>
    /// The upward speed a row's spread adds to one shot: an entry of its list, picked by the row's
    /// <c>spreadBy</c>. By the shots already fired in this burst: <c>burst</c> goes round the list
    /// (the Uzi), <c>hold</c> stays on its last entry (the AK's climb), <c>widen</c> goes through
    /// it once and then takes its last two in turn (the Tommy Gun stays wide). <c>pellet</c>: by
    /// the pellet's number. <c>supply</c>: by the supply left, going round.
    /// </summary>
    private static Fix Spread(WeaponRow row, int shot, int pellet, int supply)
    {
        int n = row.SpreadLen, k;
        if (row.SpreadBy == WeaponSpreadBy.Burst) k = shot % n;
        else if (row.SpreadBy == WeaponSpreadBy.Hold) k = Math32.Min(shot, n - 1);
        else if (row.SpreadBy == WeaponSpreadBy.Widen) k = shot < n ? shot : Math32.Max(0, n - 2 + (shot - n) % 2);
        else if (row.SpreadBy == WeaponSpreadBy.Pellet) k = pellet % n;
        else if (row.SpreadBy == WeaponSpreadBy.Supply) k = supply % n;
        else return Fix.Zero;
        return WeaponTable.Spreads[row.SpreadAt + k];
    }

    /// <summary>
    /// Puts a shot on the stage. False when there is no slot for it (it fizzles). A placed prop
    /// first makes room under its owner's limits: a second speaker removes the first, a third gate
    /// closes the oldest, and at the placed limit their oldest placed prop vanishes without bursting.
    /// </summary>
    private static bool AddShot(GameState state, ShotKind kind, WeaponId weapon, int owner, FixVec2 at, FixVec2 velocity, bool grounded, int timer, int supply)
    {
        if (WeaponTable.Placed(kind))
        {
            int own = Weapons.FlightLimit(kind);
            if (own > 0)
                for (int oldest = OldestPlaced(state, owner, out int count, kind); count >= own; oldest = OldestPlaced(state, owner, out count, kind))
                    state.Props[oldest] = default;
            MakePlacedRoom(state, owner);
        }
        int slot = FreeSlot(state, forShot: true, owner);
        if (slot < 0) return false;
        state.Props[slot] = new Prop
        {
            Kind = PropKind.Shot,
            Weapon = weapon,
            Shot = kind,
            Owner = owner,
            Supply = supply,
            Position = at,
            Velocity = velocity,
            Grounded = grounded,
            Timer = timer, // a fuse, a mine's arming, a pad's life
        };
        return true;
    }

    /// <summary>Under a fighter's placed-prop limit, room for one more: at the limit, their oldest vanishes without bursting.</summary>
    private static void MakePlacedRoom(GameState state, int owner)
    {
        int limit = Weapons.PlacedLimit(state.Fighters.Length);
        for (int oldest = OldestPlaced(state, owner, out int count); count >= limit; oldest = OldestPlaced(state, owner, out count))
            state.Props[oldest] = default;
    }

    /// <summary>
    /// The oldest placed prop of a fighter (the lower slot on a tie), or -1, and how many they have
    /// out; of one flight only, when <paramref name="only"/> is set.
    /// </summary>
    private static int OldestPlaced(GameState state, int owner, out int count, ShotKind only = ShotKind.None)
    {
        Prop[] props = state.Props;
        int oldest = -1;
        count = 0;
        for (int i = 0; i < props.Length; i++)
        {
            if (props[i].Kind != PropKind.Shot || props[i].Owner != owner || !WeaponTable.Placed(props[i].Shot)) continue;
            if (only != ShotKind.None && props[i].Shot != only) continue;
            count++;
            if (oldest < 0 || props[i].Age > props[oldest].Age) oldest = i;
        }
        return oldest;
    }

    /// <summary>
    /// An empty prop slot, or -1. A box, a pending box or a loose weapon takes the first empty slot
    /// anywhere. A shot only uses the slots after the box slots; with none empty it replaces the
    /// oldest bullet, or else its firer's own oldest placed prop, and never another fighter's.
    /// </summary>
    private static int FreeSlot(GameState state, bool forShot, int owner = -1)
    {
        Prop[] props = state.Props;
        for (int i = forShot ? Weapons.BoxSlots(state.Fighters.Length) : 0; i < props.Length; i++)
            if (props[i].Kind == PropKind.None) return i;
        if (!forShot) return -1;
        int oldest = -1;
        for (int i = 0; i < props.Length; i++)
        {
            if (props[i].Kind != PropKind.Shot || props[i].Shot != ShotKind.Bullet) continue;
            if (oldest < 0 || props[i].Age > props[oldest].Age) oldest = i;
        }
        return oldest >= 0 ? oldest : OldestPlaced(state, owner, out _);
    }

    private static void UpdateProps(GameState state, ReadOnlySpan<bool> frozen, MatchRules rules)
    {
        Prop[] props = state.Props;
        SwingProps(state, frozen, rules);
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

    /// <summary>
    /// Props in a swing (<c>scoop</c>, combat spec v0.4, section 2.7). On its active frames a swing
    /// with <c>fling</c> (the Hockey Stick) turns every box and loose weapon in its box into a plain
    /// thrown shot of the swinger's, flying the way they face at the throw speed and carrying the
    /// weapon's supply; with <c>pull</c> (the Rake) each is moved to 1.2 m in front of them.
    /// </summary>
    private static void SwingProps(GameState state, ReadOnlySpan<bool> frozen, MatchRules rules)
    {
        Fighter[] fighters = state.Fighters;
        Prop[] props = state.Props;
        for (int a = 0; a < fighters.Length; a++)
        {
            ref Fighter f = ref fighters[a];
            if (frozen[a]) continue;
            WeaponRow? row = Weapons.RowOfMove(f.Move);
            if (row == null || row.Scoop == WeaponScoop.None || !TryGetActiveHitbox(in f, rules.Tuning, out FixRect box)) continue;
            for (int i = 0; i < props.Length; i++)
            {
                ref Prop p = ref props[i];
                if ((p.Kind != PropKind.Box && p.Kind != PropKind.Item) || !box.Overlaps(PropBox(in p))) continue;
                if (row.Scoop == WeaponScoop.Pull)
                {
                    p.Position.X = f.Position.X + Weapons.ScoopPullTo * f.Facing;
                    continue;
                }
                int supply = p.Supply;
                Become(ref p, ShotKind.Thrown, Weapons.Get(p.Weapon));
                p.Owner = a;
                p.Supply = supply;
                p.Velocity = new FixVec2(Weapons.ThrowVx * f.Facing, Weapons.ThrowVy);
                p.Grounded = false;
            }
        }
    }

    /// <summary>
    /// The one way a prop changes what it is (combat spec v0.4, section 3.2): a burst leaving a
    /// zone, wall or pad; a swing flinging a box; a shot coming to rest. Every field is written, so
    /// nothing stale from the old role carries over (a wall starting with a bomb's bounce count; a
    /// rested weapon that a fighter can't pick up because IgnoreFighter held a wall's wear). The
    /// prop keeps what it carries, where it is and whether it is on the ground, and a shot keeps
    /// its owner; everything else starts afresh: a zone, wall or pad lives for its row's
    /// <c>zoneLife</c>, and <paramref name="flight"/> None makes it a loose weapon.
    /// </summary>
    internal static void Become(ref Prop p, ShotKind flight, WeaponRow row)
    {
        p.Kind = flight == ShotKind.None ? PropKind.Item : PropKind.Shot;
        // Weapon, Position and Grounded are kept.
        p.Shot = flight;
        p.Supply = 0;
        if (flight == ShotKind.None) p.Owner = -1;
        p.OwnerClear = false;
        p.Velocity = FixVec2.Zero;
        p.Timer = flight == ShotKind.Zone || flight == ShotKind.Wall || flight == ShotKind.Pad ? row.ZoneLife : 0;
        p.Age = 0;
        p.HitMask = 0;
        p.Hits = 0;
        p.IgnoreFighter = 0;
        p.IgnoreTimer = 0;
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
        WeaponRow row = Weapons.ShotRow(in p);
        switch (p.Shot)
        {
            case ShotKind.Roll:
                Fall(ref p, rules.Stage, Weapons.ShotGravity, platforms: true);
                if (row.Rehit > 0 && p.Age % row.Rehit == 0) p.HitMask = 0; // every so often it forgets who it has hit (the Chicken)
                if (p.Age >= row.Life)
                {
                    if (!row.Rest)
                    {
                        p = default;
                        return;
                    }
                    Rest(ref p, row.Supply); // it stops and lies there: anyone can use it again
                }
                break;
            case ShotKind.Bullet:
                p.Position = p.Position + p.Velocity;
                if (p.Age >= row.Life)
                {
                    MissBullet(state, in p, row);
                    p = default;
                    return;
                }
                break;
            case ShotKind.Arrow:
                p.Velocity.Y = p.Velocity.Y - row.Gravity;
                p.Position = p.Position + p.Velocity;
                if (p.Age >= row.Life)
                {
                    p = default;
                    return;
                }
                break;
            case ShotKind.Bomb:
            case ShotKind.Hole:
            {
                p.Timer--;
                bool flying = !p.Grounded;
                Fix down = Fix.Max(p.Velocity.Y - row.Gravity, -Weapons.MaxFall); // the speed it comes down with
                Fall(ref p, rules.Stage, row.Gravity, platforms: true);
                // A bomb that bounces (the Grenade Launcher) counts its floor bounces in Hits; after
                // the last it lies and slides like any bomb.
                if (p.Grounded && flying && p.Shot == ShotKind.Bomb && p.Hits < row.Bounces)
                {
                    p.Hits++;
                    BounceOffFloor(ref p, row, down);
                }
                else if (p.Grounded) p.Velocity.X = Fix.MoveToward(p.Velocity.X, Fix.Zero, Weapons.GroundBrake);
                if (p.Grounded && row.Land) p.Timer = 0; // it bursts where it comes down (the Chakla)
                break;
            }
            case ShotKind.Lob:
            {
                // Arcs, lands on platforms and hits whoever it touches. A floor bounce, counted in
                // Timer, if it has one left; with none left it is gone when it lands.
                Fix down = Fix.Max(p.Velocity.Y - row.Gravity, -Weapons.MaxFall);
                Fall(ref p, rules.Stage, row.Gravity, platforms: true);
                if (p.Grounded)
                {
                    if (p.Timer >= row.Bounces)
                    {
                        p = default;
                        return;
                    }
                    p.Timer++;
                    BounceOffFloor(ref p, row, down);
                }
                if (row.Life > 0 && p.Age >= row.Life)
                {
                    p = default;
                    return;
                }
                break;
            }
            case ShotKind.Boomerang:
                if (!MoveBoomerang(state, ref p, row)) return;
                break;
            case ShotKind.Pad:
            case ShotKind.Zone:
            case ShotKind.Wall:
            case ShotKind.Speaker:
            case ShotKind.Decoy:
                // It lies where it was put or left, falling to the platform below, for its life.
                Fall(ref p, rules.Stage, Weapons.ShotGravity, platforms: true);
                if (--p.Timer <= 0)
                {
                    p = default;
                    return;
                }
                break;
            case ShotKind.Gate:
                // It floats where it was opened, for its life.
                if (--p.Timer <= 0)
                {
                    p = default;
                    return;
                }
                break;
            case ShotKind.Drop:
                // It hangs for its fuse, then falls under its own gravity through everyone, and is
                // gone when it lands.
                if (p.Timer > 0)
                {
                    p.Timer--;
                    break;
                }
                Fall(ref p, rules.Stage, row.Gravity, platforms: true);
                if (p.Grounded)
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
                SteerMissile(state, ref p, row, rules);
                p.Position = p.Position + p.Velocity;
                break;
            case ShotKind.Thrown:
                Fall(ref p, rules.Stage, Weapons.ShotGravity, platforms: true);
                if (p.Grounded)
                {
                    EndShot(ref p); // a thrown weapon is gone once it lands; a flung one lies there
                    return;
                }
                break;
        }
        if (p.Kind == PropKind.None) return;
        if (row.Edge > 0 && p.IgnoreTimer < row.Edge) EdgeBounce(ref p, rules.Stage);
        if (!rules.Stage.BlastZone.Contains(p.Position))
        {
            if (p.Shot == ShotKind.Bullet) MissBullet(state, in p, row);
            if (p.Shot == ShotKind.Thrown && p.Supply > 0)
                p = new Prop { Kind = PropKind.PendingBox, Weapon = p.Weapon, Supply = p.Supply, Owner = -1, Timer = Weapons.ReturnFrames }; // a flung weapon comes back in a box
            else p = default;
        }
    }

    /// <summary>
    /// A shot that stops (it hits, lands, or is destroyed) is gone, unless it is a box or loose
    /// weapon flung by a swing (a thrown shot that carries supply): that comes to rest where it is,
    /// holding what it held, so flinging never takes a weapon out of the match.
    /// </summary>
    private static void EndShot(ref Prop p)
    {
        if (p.Shot == ShotKind.Thrown && p.Supply > 0) Rest(ref p, p.Supply);
        else p = default;
    }

    /// <summary>Off the floor again at the row's <c>keep</c> percent of the speed it came down with, and at least 0.08.</summary>
    private static void BounceOffFloor(ref Prop p, WeaponRow row, Fix down)
    {
        p.Velocity.Y = Fix.Max(-down * row.Keep / 100, Weapons.BounceMin);
        p.Grounded = false;
    }

    /// <summary>
    /// A screen-edge bounce (<c>edge</c>): a shot that crosses a side of the screen, 1.2 m inside
    /// the side blast line, is put back on it with its sideways speed reversed, and can hit
    /// everyone again. The bounces done are kept in <see cref="Prop.IgnoreTimer"/>.
    /// </summary>
    private static void EdgeBounce(ref Prop p, StageDefinition stage)
    {
        Fix left = stage.BlastZone.MinX + Weapons.ScreenInset;
        Fix right = stage.BlastZone.MaxX - Weapons.ScreenInset;
        if (p.Position.X < left && p.Velocity.X < Fix.Zero) p.Position.X = left;
        else if (p.Position.X > right && p.Velocity.X > Fix.Zero) p.Position.X = right;
        else return;
        p.Velocity.X = -p.Velocity.X;
        p.IgnoreTimer++;
        p.HitMask = 0;
    }

    /// <summary>A shot that has bounced off the floor or the side of the screen at least once (the <c>bounced</c> sweet spot).</summary>
    private static bool Bounced(in Prop p) =>
        p.IgnoreTimer > 0 || (p.Shot == ShotKind.Lob && p.Timer > 0) || (p.Shot == ShotKind.Bomb && p.Hits > 0);

    /// <summary>
    /// A shot comes to rest as a loose weapon anyone can pick up. It keeps its speed downwards
    /// and whether it was on the ground, as the bowling ball always has.
    /// </summary>
    private static void Rest(ref Prop p, int supply)
    {
        Become(ref p, ShotKind.None, Weapons.Get(p.Weapon));
        p.Supply = supply;
    }

    /// <summary>
    /// The boomerang's flight: out, slowing to a stop (leg 0), then back to its thrower's hand
    /// (leg 1), able to hit everyone again. Not caught in time, or its thrower gone, it drops and
    /// lies there with the throws it has left (an empty one is gone). False when it is gone.
    /// </summary>
    private static bool MoveBoomerang(GameState state, ref Prop p, WeaponRow row)
    {
        bool owned = p.Owner >= 0 && p.Owner < state.Fighters.Length && state.Fighters[p.Owner].IsActive;
        if (p.Age >= row.Life || (p.Timer != 0 && !owned))
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
            p.Velocity.X = Fix.MoveToward(p.Velocity.X, Fix.Zero, row.Drag);
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
            Fix dy = o.Position.Y + Weapons.HandHeight - row.ShotHalf - p.Position.Y;
            p.Velocity.X = Fix.MoveToward(p.Velocity.X, row.Vx * Fix.Sign(dx), row.Drag * 2);
            p.Velocity.Y = Fix.Clamp(dy, -Weapons.BoomerangClimb, Weapons.BoomerangClimb);
        }
        p.Position = p.Position + p.Velocity;
        return true;
    }

    /// <summary>
    /// The missile turns a little each frame towards the nearest fighter who isn't its owner (on a
    /// tie, the lower index); with nobody to chase it flies on. A row with no turn (the RPG) is a
    /// rocket: it flies straight.
    /// </summary>
    private static void SteerMissile(GameState state, ref Prop p, WeaponRow row, MatchRules rules)
    {
        if (row.Turn == Fix.Zero) return;
        Fighter[] fighters = state.Fighters;
        Fix cy = p.Position.Y + row.ShotHalf;
        int target = -1;
        Fix best = Fix.Zero;
        for (int d = 0; d < fighters.Length; d++)
        {
            if ((p.Owner >= 0 && Allies(rules, p.Owner, d)) || !fighters[d].IsActive) continue; // never its owner or their team
            if (IsHidden(state, rules, d)) continue; // nor anyone it can't see in a cloud
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
        p.Velocity.X = Fix.MoveToward(p.Velocity.X, row.Vx * Fix.Sign(dx), row.Turn);
        p.Velocity.Y = Fix.MoveToward(p.Velocity.Y, row.Vx * up, row.Turn);
    }

    /// <summary>A bullet of a streak weapon (the Uzi) that hit nobody breaks its shooter's streak.</summary>
    private static void MissBullet(GameState state, in Prop p, WeaponRow row)
    {
        if (row.Sweet == WeaponSweet.Streak && p.Hits == 0 && p.Owner >= 0 && p.Owner < state.Fighters.Length)
            state.Fighters[p.Owner].Streak = 0;
    }

    private static void ShotHits(GameState state, ReadOnlySpan<bool> frozen, MatchRules rules)
    {
        Fighter[] fighters = state.Fighters;
        Prop[] props = state.Props;
        FighterTuning t = rules.Tuning;
        bool walls = false;
        for (int i = 0; i < props.Length && !walls; i++) walls = props[i].Kind == PropKind.Shot && props[i].Shot == ShotKind.Wall;
        int pulsed = 0; // fighters a speaker has pulsed this frame: one pulse a frame each
        for (int i = 0; i < props.Length; i++)
        {
            ref Prop p = ref props[i];
            if (p.Kind != PropKind.Shot) continue;
            FixRect box = PropBox(in p);
            WeaponRow row = Weapons.ShotRow(in p);

            // The release grace: it can't hit its owner until it has stopped touching them once.
            if (!p.OwnerClear && (p.Owner < 0 || !fighters[p.Owner].IsActive || !box.Overlaps(Hurtbox(in fighters[p.Owner], t))))
                p.OwnerClear = true;

            // A sweep (the Jhaadu) takes every trap, peel, mine and patch in its reach away, whoever
            // owns it, without bursting. Not a cloud.
            if ((p.Shot == ShotKind.Mine || (p.Shot == ShotKind.Zone && !row.Hides)) && Swept(state, frozen, box, t))
            {
                p = default;
                continue;
            }
            if (p.Shot == ShotKind.Pad)
            {
                Bounce(state, in p, row, box, rules);
                continue;
            }
            if (p.Shot == ShotKind.Mine)
            {
                TripMine(state, i, row, box, rules);
                continue;
            }
            if (p.Shot == ShotKind.Zone)
            {
                ZoneTouch(state, in p, row, box, rules);
                continue;
            }
            if (p.Shot == ShotKind.Wall)
            {
                WallPush(state, box, rules);
                continue;
            }
            if (p.Shot == ShotKind.Speaker)
            {
                if (row.Rehit > 0 && state.Frame % row.Rehit == 0) Pulse(state, in p, row, rules, ref pulsed);
                continue;
            }
            if (p.Shot == ShotKind.Gate)
            {
                PassGate(state, i, box, rules);
                continue;
            }
            if (p.Shot == ShotKind.Decoy)
            {
                if (row.Touch) Spring(state, i, row, box, rules);
                continue;
            }
            if (p.Shot == ShotKind.Drop)
            {
                if (p.Timer == 0) DropOn(state, i, row, box, rules);
                continue;
            }
            if (!WeaponTable.Flies(p.Shot)) continue; // nothing else acts on a shot that doesn't fly

            // A breaking swing (the Lathi) knocks a shot of someone else's side out of the air.
            if (Broken(state, frozen, in p, box, rules))
            {
                EndShot(ref p);
                continue;
            }

            // A bat swing bats it back: it becomes the batter's, reversed and 1.25x faster. A light
            // bat (the racket's batMax) only returns light shots, nothing that bursts and nothing
            // that does more than its limit, and each return costs it supply (batCost).
            for (int b = 0; b < fighters.Length; b++)
            {
                ref Fighter batter = ref fighters[b];
                WeaponRow? swung = Weapons.RowOfMove(batter.Move);
                if (frozen[b] || swung == null || swung.Defence != WeaponDefence.Bat || !TryGetActiveHitbox(in batter, t, out FixRect bat)) continue;
                if (p.Owner >= 0 && b != p.Owner && Allies(rules, b, p.Owner)) continue; // never a teammate's shot
                if (Fix.Sign(p.Velocity.X) == batter.Facing || !bat.Overlaps(box)) continue;
                if (swung.BatMax > 0)
                {
                    bool thrown = p.Shot == ShotKind.Thrown;
                    int damage = thrown ? WeaponTable.Rows[(int)p.Weapon].Throw : row.Damage;
                    if (damage > swung.BatMax || (!thrown && row.BlastHalf != Fix.Zero)) continue;
                }
                if (batter.WeaponSupply < swung.BatCost) continue;
                batter.WeaponSupply -= swung.BatCost;
                p.Owner = b;
                p.OwnerClear = false;
                p.HitMask = 0;
                p.Hits = 0;
                p.Velocity = new FixVec2(Fix.Abs(p.Velocity.X) * Weapons.SweetPercent / 100 * batter.Facing, p.Velocity.Y);
                if (p.Shot == ShotKind.Bullet || p.Shot == ShotKind.Arrow) p.Age = 0;
                if (p.Shot == ShotKind.Boomerang) p.Timer = 0; // it flies out again, and comes back to the batter
                break;
            }

            if (walls && StopAtWall(state, i, box, rules)) continue;

            if (((p.Shot == ShotKind.Bomb || p.Shot == ShotKind.Hole) && p.Timer <= 0)
                || (p.Shot == ShotKind.Missile && p.Age >= row.Life))
            {
                Explode(state, i, -1, rules);
                continue;
            }
            if (p.Shot == ShotKind.Hole)
            {
                // It doesn't burst on touch: for the end of its fuse it pulls everyone near it in.
                if (p.Timer <= row.PullFrames) Pull(state, in p, row, rules);
                continue;
            }

            for (int d = 0; d < fighters.Length; d++)
            {
                ref Fighter def = ref fighters[d];
                if (!def.IsActive || (p.HitMask & (1 << d)) != 0) continue;
                if (d == p.Owner && !p.OwnerClear) continue;
                if (d != p.Owner && p.Owner >= 0 && Allies(rules, p.Owner, d)) continue; // it flies through a teammate
                if (row.NoSelf && p.Owner >= 0 && Allies(rules, p.Owner, d)) continue; // it never hits its caster (Dark Energy)
                if (!box.Overlaps(Hurtbox(in def, t))) continue;

                // A guard destroys a shot that comes at it from the front (by the way it moves), as
                // a parry does. A bomb or missile is destroyed only where it would have burst on touch.
                bool bursts = p.Shot == ShotKind.Bomb || p.Shot == ShotKind.Missile;
                if (Guarding(in def) && ShotFromFront(in def, in p) && (!bursts || (row.Touch && d != p.Owner)))
                {
                    GuardCost(ref def);
                    EndShot(ref p);
                    break;
                }

                if (p.Shot == ShotKind.Boomerang && d == p.Owner)
                {
                    // Back to the hand: caught on the way back, empty-handed. Otherwise it flies past.
                    if (p.Timer == 0 || def.Weapon != WeaponId.None) continue;
                    def.Weapon = p.Weapon;
                    def.WeaponSupply = p.Supply;
                    def.BurstShots = 0;
                    def.Streak = 0;
                    def.StreakTarget = 0;
                    p = default;
                    break;
                }
                if (p.Shot == ShotKind.Bomb || p.Shot == ShotKind.Missile)
                {
                    if (!row.Touch || d == p.Owner) continue; // it bursts on touching anyone but its thrower
                    Explode(state, i, d, rules);
                    break;
                }
                if (InParryWindow(in def, t))
                {
                    ParryReward(ref def, rules); // parrying a projectile destroys it
                    EndShot(ref p);
                    break;
                }
                if (IsIntangible(in def, t)) continue; // dodged: it flies on

                p.HitMask |= 1 << d;
                ShotHit(state, i, d, row, rules);
                p.Hits++;
                if (p.Shot == ShotKind.Bullet || p.Shot == ShotKind.Thrown || ((p.Shot == ShotKind.Arrow || p.Shot == ShotKind.Lob) && !row.Pierce))
                {
                    EndShot(ref p); // it stops in the first fighter it hits; only an arrow or a lob that pierces flies on
                    break;
                }
                if (row.MaxHits > 0 && p.Hits >= row.MaxHits)
                {
                    p = default; // it fades after its last fighter (the Dark Energy orb's fourth)
                    break;
                }
            }
        }
    }

    /// <summary>
    /// A trampoline bounces up everyone touching it who isn't already rising, its owner too once
    /// they have stepped off it. It does no damage and credits nobody.
    /// </summary>
    private static void Bounce(GameState state, in Prop p, WeaponRow row, FixRect box, MatchRules rules)
    {
        Fighter[] fighters = state.Fighters;
        for (int d = 0; d < fighters.Length; d++)
        {
            ref Fighter f = ref fighters[d];
            if (!f.IsActive || IsIntangible(in f, rules.Tuning) || f.Action == FighterAction.Hitstun) continue;
            if (d == p.Owner && !p.OwnerClear) continue;
            if (f.Velocity.Y > Fix.Zero || !box.Overlaps(Hurtbox(in f, rules.Tuning))) continue;
            f.Velocity.Y = row.PadLaunch;
            f.Grounded = false;
        }
    }

    /// <summary>
    /// A speaker's pulse. Every speaker pulses together, on the match clock (each frame that is a
    /// multiple of its row's <c>rehit</c>): its row's hit on every undodging fighter but its owner
    /// and their team whose hurtbox touches the box its reach long and 2 m high in front of it,
    /// launched the way it faces (its supply). A fighter takes one pulse a frame at most, from the
    /// speaker in the lowest slot, so two speakers never take turns on anyone; a wall in between
    /// stops it; and it never pulses at someone in the air with no platform anywhere below them,
    /// so it can't hold a recovering fighter out.
    /// </summary>
    private static void Pulse(GameState state, in Prop p, WeaponRow row, MatchRules rules, ref int pulsed)
    {
        Fighter[] fighters = state.Fighters;
        FighterTuning t = rules.Tuning;
        int facing = p.Supply < 0 ? -1 : 1;
        var box = new FixRect(p.Position.X, p.Position.Y, p.Position.X + row.Reach * facing, p.Position.Y + Weapons.PulseHeight);
        WeaponId credit = WeaponTable.Rows[(int)p.Weapon].Credit;
        for (int d = 0; d < fighters.Length; d++)
        {
            ref Fighter f = ref fighters[d];
            if ((pulsed & (1 << d)) != 0 || !f.IsActive || IsIntangible(in f, t)) continue;
            if (p.Owner >= 0 && Allies(rules, p.Owner, d)) continue; // never its owner or their team
            if (!box.Overlaps(Hurtbox(in f, t))) continue;
            if (!f.Grounded && !TopBelow(rules.Stage, f.Position, out _)) continue; // nothing under them: it lets them come back
            if (WallBetween(state, p.Position.X, f.Position.X, box)) continue;
            pulsed |= 1 << d;
            int from = p.Owner >= 0 ? p.Owner : d;
            var hit = new Hit(row.MoveData);
            hit.Flags(row);
            if (rules.SignatureOf(from) == credit) hit.Percent = hit.Percent * Weapons.SignaturePercent / 100;
            hit.Weapon = credit;
            LandHit(state, from, d, in hit, facing, freezeAttacker: false, rules);
        }
    }

    /// <summary>
    /// A gate (the Portal Disc). Two gates with one owner are a pair; one with no partner does
    /// nothing. An undodging fighter touching a gate that hasn't marked it comes out of the
    /// partner: put on the partner's spot, its speed zero, its air jump, air dodge and recovery
    /// given back. Both gates then mark it (a bit in <see cref="Prop.HitMask"/>), and each clears
    /// its mark once the fighter stops touching it, so nobody bounces between them. Anyone may use
    /// them; the opener only once they have stepped off the gate they opened. Shots don't.
    /// </summary>
    private static void PassGate(GameState state, int index, FixRect box, MatchRules rules)
    {
        Prop[] props = state.Props;
        Fighter[] fighters = state.Fighters;
        FighterTuning t = rules.Tuning;
        ref Prop p = ref props[index];
        int partner = -1;
        for (int i = 0; i < props.Length && partner < 0; i++)
            if (i != index && props[i].Kind == PropKind.Shot && props[i].Shot == ShotKind.Gate && props[i].Owner == p.Owner) partner = i;
        for (int d = 0; d < fighters.Length; d++)
        {
            ref Fighter f = ref fighters[d];
            int bit = 1 << d;
            if (!f.IsActive || !box.Overlaps(Hurtbox(in f, t)))
            {
                p.HitMask &= ~bit; // it has stepped off: it can go through again
                continue;
            }
            if ((p.HitMask & bit) != 0 || partner < 0 || IsIntangible(in f, t)) continue;
            if (d == p.Owner && !p.OwnerClear) continue; // the opener is standing in it
            f.Position = props[partner].Position;
            f.Velocity = FixVec2.Zero;
            f.Grounded = TopBelow(rules.Stage, f.Position, out Fix top) && top == f.Position.Y;
            f.AirJumpsLeft = t.AirJumps;
            f.AirDodgeUsed = false;
            f.RecoveryUsed = false;
            p.HitMask |= bit;
            props[partner].HitMask |= bit;
        }
    }

    /// <summary>
    /// A Decoy Crate springs: the first fighter, other than its owner and their team, who touches
    /// it while able to take a box (active, not attacking, not dodging) takes its row's hit,
    /// launched up and away from it, and the decoy is gone. A rigged crate (a row without
    /// <c>touch</c>) never springs; only the Detonator bursts it.
    /// </summary>
    private static void Spring(GameState state, int index, WeaponRow row, FixRect box, MatchRules rules)
    {
        Fighter[] fighters = state.Fighters;
        int owner = state.Props[index].Owner;
        for (int d = 0; d < fighters.Length; d++)
        {
            ref Fighter f = ref fighters[d];
            if (!f.IsActive || f.Action == FighterAction.Attack || f.Action == FighterAction.Dodge) continue;
            if (owner >= 0 && Allies(rules, owner, d)) continue;
            if (!box.Overlaps(Hurtbox(in f, rules.Tuning))) continue;
            Prop p = state.Props[index];
            state.Props[index] = default;
            int from = owner >= 0 ? owner : d;
            WeaponId credit = WeaponTable.Rows[(int)p.Weapon].Credit;
            var hit = new Hit(row.MoveData);
            hit.Flags(row);
            if (rules.SignatureOf(from) == credit) hit.Percent = hit.Percent * Weapons.SignaturePercent / 100;
            hit.Weapon = credit;
            LandHit(state, from, d, in hit, f.Position.X >= p.Position.X ? 1 : -1, freezeAttacker: false, rules);
            return;
        }
    }

    /// <summary>
    /// A falling anvil (flight <c>drop</c>) hits each fighter it passes through once, its owner
    /// too once the release grace is over, never a teammate of its owner's. Nothing stops it: a
    /// dodge, or a parry, only saves the fighter.
    /// </summary>
    private static void DropOn(GameState state, int index, WeaponRow row, FixRect box, MatchRules rules)
    {
        Fighter[] fighters = state.Fighters;
        FighterTuning t = rules.Tuning;
        for (int d = 0; d < fighters.Length; d++)
        {
            ref Prop p = ref state.Props[index];
            ref Fighter f = ref fighters[d];
            if (!f.IsActive || (p.HitMask & (1 << d)) != 0) continue;
            if (d == p.Owner && !p.OwnerClear) continue;
            if (d != p.Owner && p.Owner >= 0 && Allies(rules, p.Owner, d)) continue;
            if (!box.Overlaps(Hurtbox(in f, t)) || IsIntangible(in f, t)) continue;
            p.HitMask |= 1 << d;
            ShotHit(state, index, d, row, rules);
            p.Hits++;
        }
    }

    /// <summary>
    /// An armed mine bursts on the first fighter touching it, its planter included. One that
    /// nobody found bursts by itself after 15 s. A trap (a mine with no blast: the Mouse Trap,
    /// Venom Trap, Banana Peel) instead gives that fighter its row's hit and effect alone, and is
    /// gone; one nobody found is gone after its life without bursting.
    /// </summary>
    private static void TripMine(GameState state, int index, WeaponRow row, FixRect box, MatchRules rules)
    {
        bool trap = row.BlastHalf == Fix.Zero;
        if (state.Props[index].Age >= row.Life)
        {
            if (trap) state.Props[index] = default;
            else Explode(state, index, -1, rules);
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
            if (d != owner && owner >= 0 && Allies(rules, owner, d)) continue; // a teammate doesn't set it off
            if (!box.Overlaps(Hurtbox(in f, rules.Tuning))) continue;
            if (!trap)
            {
                Explode(state, index, d, rules);
                return;
            }
            Prop p = state.Props[index];
            state.Props[index] = default;
            int from = owner >= 0 ? owner : d;
            WeaponId credit = WeaponTable.Rows[(int)p.Weapon].Credit;
            var hit = new Hit(row.MoveData);
            hit.Flags(row);
            if (rules.SignatureOf(from) == credit) hit.Percent = hit.Percent * Weapons.SignaturePercent / 100;
            hit.Weapon = credit;
            LandHit(state, from, d, in hit, f.Position.X >= p.Position.X ? 1 : -1, freezeAttacker: false, rules);
            return;
        }
    }

    /// <summary>The black hole drags everyone within reach sideways towards it.</summary>
    private static void Pull(GameState state, in Prop p, WeaponRow row, MatchRules rules)
    {
        Fighter[] fighters = state.Fighters;
        Fix cy = p.Position.Y + row.ShotHalf;
        for (int d = 0; d < fighters.Length; d++)
        {
            ref Fighter f = ref fighters[d];
            if (!f.IsActive || IsIntangible(in f, rules.Tuning)) continue;
            Fix dx = p.Position.X - f.Position.X;
            if (Fix.Abs(dx) > row.Reach || Fix.Abs(f.Position.Y + rules.Tuning.Height / 2 - cy) > row.Reach) continue;
            f.Position.X += Fix.Clamp(dx, -row.Pull, row.Pull);
        }
    }

    /// <summary>
    /// A projectile hits a fighter, its owner included, with its row's numbers (a thrown weapon:
    /// the throw's, and its row's throw damage). Sweet spots, never on yourself, by the row's rule:
    /// a second and later fighter ("Strike!"), a shot that flew long enough, a boomerang on its way
    /// back, a thrown weapon on an airborne target. A streak weapon (the Uzi) instead does 1 more
    /// damage, with no hitstun penalty, from so many hits in a row on one target.
    /// </summary>
    private static void ShotHit(GameState state, int index, int target, WeaponRow row, MatchRules rules)
    {
        ref Prop p = ref state.Props[index];
        Fighter[] fighters = state.Fighters;
        int owner = p.Owner >= 0 ? p.Owner : target;
        bool self = owner == target;
        Hit hit;
        if (p.Shot == ShotKind.Thrown)
        {
            hit = new Hit(rules.Tuning.Moves[MoveId.WeaponThrow]);
            hit.Damage = WeaponTable.Rows[(int)p.Weapon].Throw;
            if (!self && !fighters[target].Grounded) hit.Sweeten();
        }
        else
        {
            hit = new Hit(row.MoveData);
            hit.Flags(row);
            if (!self && row.Sweet == WeaponSweet.Streak)
            {
                ref Fighter shooter = ref fighters[owner];
                if (shooter.StreakTarget == target + 1) shooter.Streak++;
                else
                {
                    shooter.StreakTarget = target + 1;
                    shooter.Streak = 1;
                }
                if (shooter.Streak >= row.SweetArg)
                {
                    hit.Damage++; // keeping on target: the stream holds them
                    hit.HitstunBonus = 0;
                    hit.Sweet = true;
                }
            }
            else if (!self && ((row.Sweet == WeaponSweet.Strike && p.Hits >= 1)
                || (row.Sweet == WeaponSweet.Longshot && p.Age >= row.SweetArg)
                || (row.Sweet == WeaponSweet.Return && p.Timer != 0)
                || (row.Sweet == WeaponSweet.Bounced && Bounced(in p))))
                hit.Sweeten();
            if (row.SweetOnly && !hit.Sweet) hit.Effect = WeaponEffect.None;
        }
        WeaponId credit = WeaponTable.Rows[(int)p.Weapon].Credit;
        if (rules.SignatureOf(owner) == credit) hit.Percent = hit.Percent * Weapons.SignaturePercent / 100;
        hit.Weapon = credit;
        int side = p.Velocity.X < Fix.Zero ? -1 : 1;
        if (p.Shot == ShotKind.Drop) side = fighters[target].Position.X >= p.Position.X ? 1 : -1; // it falls straight: away from its middle
        LandHit(state, owner, target, in hit, side, freezeAttacker: false, rules);
    }

    /// <summary>
    /// A bomb, mine, missile or black hole bursts: a box as wide as its row's blast that hits
    /// everyone in it, its thrower too, launching them away from its centre. A fighter it burst on
    /// is a direct hit (a sweet spot, for a row that has it). A guard whose front faces the centre
    /// takes nothing. A row with no damage (the cloud capsule, the thrown charpai) only opens.
    /// The burst breaks every breakable prop it covers that isn't its side's (walls ignore bursts,
    /// and break). Then a row that <c>leaves</c> something does not vanish: it becomes that zone,
    /// wall or pad where it burst (<see cref="Become"/>), a placed prop of its owner's from then on.
    /// </summary>
    private static void Explode(GameState state, int index, int direct, MatchRules rules)
    {
        Prop p = state.Props[index];
        state.Props[index] = default;
        Fighter[] fighters = state.Fighters;
        WeaponRow row = Weapons.ShotRow(in p);
        WeaponId credit = WeaponTable.Rows[(int)p.Weapon].Credit;
        Fix cy = p.Position.Y + row.ShotHalf;
        Fix half = row.BlastHalf;
        var blast = new FixRect(p.Position.X - half, cy - half, p.Position.X + half, cy + half);
        int owner = p.Owner;
        for (int d = 0; d < fighters.Length && row.Damage > 0; d++)
        {
            ref Fighter def = ref fighters[d];
            if (!def.IsActive || IsIntangible(in def, rules.Tuning)) continue;
            if (d != owner && owner >= 0 && Allies(rules, owner, d)) continue; // it hurts its thrower, never their team
            if (!blast.Overlaps(Hurtbox(in def, rules.Tuning))) continue;
            if (Guarding(in def) && FacesToward(in def, p.Position.X))
            {
                GuardCost(ref def); // a burst in front of a guard doesn't hit it
                continue;
            }
            int from = owner >= 0 ? owner : d;
            var hit = new Hit(row.MoveData);
            hit.Flags(row);
            if (row.Sweet == WeaponSweet.Direct && d == direct && from != d) hit.Sweeten();
            if (row.SweetOnly && !hit.Sweet) hit.Effect = WeaponEffect.None;
            if (rules.SignatureOf(from) == credit) hit.Percent = hit.Percent * Weapons.SignaturePercent / 100;
            hit.Weapon = credit;
            int side = def.Position.X >= p.Position.X ? 1 : -1;
            LandHit(state, from, d, in hit, side, freezeAttacker: false, rules);
        }

        if (half > Fix.Zero)
        {
            Prop[] props = state.Props;
            for (int i = 0; i < props.Length; i++)
            {
                if (!Weapons.Breakable(in props[i]) || (owner >= 0 && props[i].Owner >= 0 && Allies(rules, owner, props[i].Owner))) continue;
                if (blast.Overlaps(PropBox(in props[i]))) props[i] = default;
            }
        }

        ShotKind left = Weapons.LeftFlight(row);
        if (left == ShotKind.None) return;
        if (WeaponTable.Placed(left) && owner >= 0) MakePlacedRoom(state, owner);
        state.Props[index] = p;
        Become(ref state.Props[index], left, row);
    }

    /// <summary>A sweeping swing (defence <c>sweep</c>, the Jhaadu) on an active frame covers this box.</summary>
    private static bool Swept(GameState state, ReadOnlySpan<bool> frozen, FixRect box, FighterTuning t)
    {
        Fighter[] fighters = state.Fighters;
        for (int b = 0; b < fighters.Length; b++)
        {
            WeaponRow? swung = Weapons.RowOfMove(fighters[b].Move);
            if (frozen[b] || swung == null || swung.Defence != WeaponDefence.Sweep) continue;
            if (TryGetActiveHitbox(in fighters[b], t, out FixRect sweep) && sweep.Overlaps(box)) return true;
        }
        return false;
    }

    /// <summary>A breaking swing (defence <c>break</c>, the Lathi) on an active frame covers a flying shot of someone else's side.</summary>
    private static bool Broken(GameState state, ReadOnlySpan<bool> frozen, in Prop p, FixRect box, MatchRules rules)
    {
        Fighter[] fighters = state.Fighters;
        for (int b = 0; b < fighters.Length; b++)
        {
            WeaponRow? swung = Weapons.RowOfMove(fighters[b].Move);
            if (frozen[b] || swung == null || swung.Defence != WeaponDefence.Break) continue;
            if (p.Owner >= 0 && Allies(rules, b, p.Owner)) continue;
            if (TryGetActiveHitbox(in fighters[b], rules.Tuning, out FixRect spin) && spin.Overlaps(box)) return true;
        }
        return false;
    }

    /// <summary>
    /// A flying shot that touches a wall stops: a bullet, arrow, lob or thrown weapon is gone (a
    /// flung one lies there), a bomb, missile or black hole bursts, a rolling shot turns round,
    /// and a boomerang starts its way back (one already coming back drops). A shot passes its own
    /// side's walls, so a wall is cover to shoot from, not a trap for its thrower. True when the
    /// shot is gone or has burst.
    /// </summary>
    private static bool StopAtWall(GameState state, int index, FixRect box, MatchRules rules)
    {
        ref Prop p = ref state.Props[index];
        Prop[] props = state.Props;
        int wall = -1;
        for (int i = 0; i < props.Length && wall < 0; i++)
        {
            if (props[i].Kind != PropKind.Shot || props[i].Shot != ShotKind.Wall || !box.Overlaps(PropBox(in props[i]))) continue;
            if (p.Owner >= 0 && props[i].Owner >= 0 && Allies(rules, p.Owner, props[i].Owner)) continue;
            wall = i;
        }
        if (wall < 0) return false;
        Fix toward = props[wall].Position.X - p.Position.X;
        switch (p.Shot)
        {
            case ShotKind.Bomb:
            case ShotKind.Missile:
            case ShotKind.Hole:
                Explode(state, index, -1, rules);
                return true;
            case ShotKind.Roll:
                if (Fix.Sign(p.Velocity.X) == Fix.Sign(toward)) p.Velocity.X = -p.Velocity.X;
                return false;
            case ShotKind.Boomerang:
                if (p.Timer == 0)
                {
                    p.Timer = 1;
                    p.HitMask = 0;
                    p.Velocity.X = Fix.Zero;
                    return false;
                }
                if (Fix.Sign(p.Velocity.X) != Fix.Sign(toward)) return false; // going away from it
                if (p.Supply > 0)
                {
                    Rest(ref p, p.Supply);
                    p.Grounded = false;
                }
                else p = default;
                return true;
            case ShotKind.Bullet:
                MissBullet(state, in p, Weapons.ShotRow(in p));
                p = default;
                return true;
            default:
                EndShot(ref p);
                return true;
        }
    }

    /// <summary>
    /// A zone works on every undodging fighter touching it, its owner and their team included (a
    /// puddle is a puddle), with its row's effect in its standing form: slow keeps them slowed,
    /// poison keeps them poisoned (credited to the zone's owner; the ticks keep their own rhythm),
    /// and slip takes a grounded fighter who crosses it faster than 0.05 m a frame off their feet.
    /// </summary>
    private static void ZoneTouch(GameState state, in Prop p, WeaponRow row, FixRect box, MatchRules rules)
    {
        Fighter[] fighters = state.Fighters;
        WeaponId credit = WeaponTable.Rows[(int)p.Weapon].Credit;
        int n = row.EffectN;
        for (int d = 0; d < fighters.Length; d++)
        {
            ref Fighter f = ref fighters[d];
            if (!f.IsActive || IsIntangible(in f, rules.Tuning) || !box.Overlaps(Hurtbox(in f, rules.Tuning))) continue;
            switch (row.Effect)
            {
                case WeaponEffect.Slow:
                    f.SlowFrames = Math32.Max(f.SlowFrames, n);
                    break;
                case WeaponEffect.Poison:
                case WeaponEffect.Burn:
                    f.DotFrames = Math32.Max(f.DotFrames, n);
                    f.DotBy = p.Owner >= 0 ? p.Owner + 1 : 0;
                    f.DotWeapon = (int)credit;
                    break;
                case WeaponEffect.Slip:
                    if (!f.Grounded || Fix.Abs(f.Velocity.X) <= Weapons.SlipRunSpeed || f.StunImmune > 0 || f.ArmorFrames > 0) break;
                    if (f.Action == FighterAction.Hitstun || f.Action == FighterAction.Stunned) break;
                    Slip(ref f, n, credit, rules);
                    f.StunImmune = n + Weapons.HoldImmuneFrames;
                    break;
            }
        }
    }

    /// <summary>
    /// A wall moves every undodging fighter overlapping it out sideways, towards its nearer side, by
    /// at most 0.2 m a frame, and stops them moving into it. It is 1.6 m high: a jump clears it.
    /// </summary>
    private static void WallPush(GameState state, FixRect wall, MatchRules rules)
    {
        Fighter[] fighters = state.Fighters;
        FighterTuning t = rules.Tuning;
        Fix centre = (wall.MinX + wall.MaxX) / 2;
        for (int d = 0; d < fighters.Length; d++)
        {
            ref Fighter f = ref fighters[d];
            if (!f.IsActive || IsIntangible(in f, t) || !wall.Overlaps(Hurtbox(in f, t))) continue;
            if (f.Position.X < centre)
            {
                f.Position.X += Fix.Max(wall.MinX - t.HalfWidth - f.Position.X, -Weapons.WallPush);
                if (f.Velocity.X > Fix.Zero) f.Velocity.X = Fix.Zero;
            }
            else
            {
                f.Position.X += Fix.Min(wall.MaxX + t.HalfWidth - f.Position.X, Weapons.WallPush);
                if (f.Velocity.X < Fix.Zero) f.Velocity.X = Fix.Zero;
            }
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
            if (taker.Weapon != WeaponId.None && (taker.WeaponSupply > 0 || WeaponTable.Rows[(int)taker.Weapon].Spend == WeaponSpend.Never))
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
            taker.BurstShots = 0;
            taker.Streak = 0;
            taker.StreakTarget = 0;
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

        WeaponId weapon = PickCrate(state);
        PlaceBox(state, rules, slot, weapon, Weapons.Get(weapon).Supply);
        state.LastBoxWeapon = weapon;
        state.PropTimer = state.Random.Range(Weapons.BoxIntervalMin, Weapons.BoxIntervalMax);
    }

    /// <summary>
    /// Any weapon a crate can hold but the last box's, by weight: a row's share of crates is its
    /// <c>weight</c> (2, or 1 for the utility rows, which so come half as often). One draw a box.
    /// </summary>
    private static WeaponId PickCrate(GameState state) => PickCrate(ref state.Random, Weapons.CrateList, state.LastBoxWeapon);

    /// <summary>The pick over any list of rows in id order (the game's is <see cref="Weapons.CrateList"/>).</summary>
    internal static WeaponId PickCrate(ref SimRandom random, ReadOnlySpan<WeaponId> list, WeaponId last)
    {
        int total = 0;
        for (int i = 0; i < list.Length; i++)
            if (list[i] != last) total += WeaponTable.Rows[(int)list[i]].Weight;
        if (total <= 0) return list[0]; // a list of one: there is nothing else to pick
        int r = random.Range(0, total);
        for (int i = 0; i < list.Length; i++)
        {
            if (list[i] == last) continue;
            r -= WeaponTable.Rows[(int)list[i]].Weight;
            if (r < 0) return list[i];
        }
        return list[list.Length - 1];
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
        if (f.WeaponSupply > 0 || WeaponTable.Rows[(int)f.Weapon].Spend == WeaponSpend.Never)
        {
            int slot = FreeSlot(state, forShot: false);
            if (slot >= 0)
                state.Props[slot] = new Prop { Kind = PropKind.PendingBox, Weapon = f.Weapon, Supply = f.WeaponSupply, Owner = -1, Timer = Weapons.ReturnFrames };
        }
        f.Weapon = WeaponId.None;
        f.WeaponSupply = 0;
    }
}
