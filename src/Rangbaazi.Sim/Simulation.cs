using System;
using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim;

/// <summary>Immutable per-match settings (not part of rollback state).</summary>
public sealed class MatchRules
{
    public required StageDefinition Stage { get; init; }
    public FighterTuning Tuning { get; init; } = FighterTuning.Default;
    public int Stocks { get; init; } = 3;               // GDD B2: stocks default to 3
    public int RespawnFrames { get; init; } = 60;
    public int MaxJosh { get; init; } = 300;

    /// <summary>
    /// Guts (GDD B2): once per stock, a fighter whom a hit leaves at <see cref="GutsJosh"/> or more,
    /// and who then keeps the stock for <see cref="GutsDelayFrames"/>, gets <see cref="GutsHeal"/>
    /// Josh back and <see cref="GutsArmorFrames"/> of armor. Off, for comparing in playtests.
    /// </summary>
    public bool Guts { get; init; } = true;
    public int GutsJosh { get; init; } = 150;
    public int GutsDelayFrames { get; init; } = 180;    // 3 s
    public int GutsHeal { get; init; } = 30;
    public int GutsArmorFrames { get; init; } = 60;

    /// <summary>Hit-stop range (GDD B2 feel targets: 4-10 frames, scaled by damage).</summary>
    public int MinHitStop { get; init; } = 4;
    public int MaxHitStop { get; init; } = 10;

    /// <summary>
    /// Timed free-for-all (online rooms) when above 0: nobody is ever eliminated, a fighter who
    /// leaves the blast zone always respawns, KOs score, and the match ends after this many frames.
    /// 0 is a stock match.
    /// </summary>
    public int TimeLimitFrames { get; init; }

    /// <summary>A ring-out within this many frames of a fighter's last hit credits that attacker with the KO.</summary>
    public int KoCreditFrames { get; init; } = 300; // 5 s

    /// <summary>
    /// Comeback "rage": a fighter's hits do up to this many percent more damage and knockback, scaling
    /// with its own Josh up to <see cref="RageJosh"/>. 0 is off.
    /// </summary>
    public int RagePercent { get; init; }
    public int RageJosh { get; init; } = 150;

    /// <summary>
    /// Hidden second wind: after a respawn, this much incoming damage is absorbed before Josh rises.
    /// Never shown to players. 0 is off.
    /// </summary>
    public int RespawnShield { get; init; }

    /// <summary>
    /// The fist flurry: empty-handed, Light and Heavy pressed on the same frame start one of four
    /// combos at random, with equal odds (a jab; a neutral heavy; a jab then a neutral heavy; two
    /// neutral heavies). Off, both together are the heavy, as in combat spec v0.1.
    /// </summary>
    public bool Flurry { get; init; }

    /// <summary>The stage's hazards run (StageDefinition.Hazards). Off, they are scenery.</summary>
    public bool Hazards { get; init; }

    /// <summary>Prop Boxes drop weapons (Weapons.cs). Off, the match is fists only.</summary>
    public bool Weapons { get; init; }

    /// <summary>
    /// Each fighter's own weapon, by fighter index (<see cref="Sim.Weapons.SignatureFor"/>): with it,
    /// their hits do <see cref="Sim.Weapons.SignaturePercent"/>. Null or shorter: no signature.
    /// </summary>
    public WeaponId[]? Signatures { get; init; }

    public WeaponId SignatureOf(int fighter) =>
        Signatures != null && fighter >= 0 && fighter < Signatures.Length ? Signatures[fighter] : WeaponId.None;

    /// <summary>A room match: the timed free-for-all with weapons, rage and the hidden second wind.</summary>
    public static MatchRules Room(StageDefinition stage, int minutes, WeaponId[]? signatures = null) => new()
    {
        Stage = stage,
        TimeLimitFrames = minutes * 60 * Simulation.TicksPerSecond,
        RagePercent = 50,
        RespawnShield = 20,
        Weapons = true,
        Hazards = true,
        Flurry = true,
        Signatures = signatures,
    };
}

/// <summary>
/// The deterministic step function: (state, inputs) -> next state, at a fixed 60 Hz.
/// Graybox scope today: run, jump + air jump, one-way platforms, jab, four heavies plus the
/// ground slam, dodge and parry, hit-stop, Josh % knockback, Guts, and ring-out
/// (docs/design/combat-spec-v0.1.md). Light strings, grabs, weapons and Taali come next.
/// </summary>
public static partial class Simulation
{
    public const int TicksPerSecond = 60;

    /// <summary>Most fighters in one match: online rooms hold up to 15 players (one spare slot).</summary>
    public const int MaxFighters = 16;

    private static readonly Fix Diagonal = Fix.FromRatio(7071, 10000); // ~sin(45deg)

    public static GameState CreateInitialState(MatchRules rules, int fighterCount, ulong seed)
    {
        if (fighterCount < 1 || fighterCount > MaxFighters)
            throw new ArgumentOutOfRangeException(nameof(fighterCount), $"1-{MaxFighters} fighters.");

        var state = new GameState(fighterCount, seed);
        for (int i = 0; i < fighterCount; i++)
        {
            ref Fighter f = ref state.Fighters[i];
            f.Stocks = rules.Stocks;
            Spawn(ref f, i, rules);
        }
        state.PropTimer = Weapons.FirstBoxFrames;
        return state;
    }

    public static void Advance(GameState state, MatchRules rules, ReadOnlySpan<Buttons> inputs)
    {
        if (inputs.Length != state.Fighters.Length)
            throw new ArgumentException($"Expected {state.Fighters.Length} inputs, got {inputs.Length}.", nameof(inputs));

        if (!state.MatchOver)
        {
            Fighter[] fighters = state.Fighters;
            Span<bool> frozen = stackalloc bool[fighters.Length];
            for (int i = 0; i < fighters.Length; i++) frozen[i] = UpdateFighter(state, ref fighters[i], i, inputs[i], rules);
            if (rules.Weapons) FireShots(state, frozen, rules);
            ResolveHits(state, frozen, rules);
            if (rules.Weapons) UpdateProps(state, frozen, rules);
            if (rules.Hazards) ApplyHazards(state, rules);
            for (int i = 0; i < fighters.Length; i++)
            {
                EndPlungeOnLanding(ref fighters[i], rules.Tuning);
                if (rules.Weapons) ReturnOnFall(state, i, rules);
                CheckBlastZone(state, i, rules);
            }
            CheckMatchOver(state, rules);
        }

        state.Frame++;
    }

    /// <summary>Hit-stop for a hit of this much damage: 2 + damage / 2, clamped to the rules' range.</summary>
    public static int HitStopFrames(int damage, MatchRules rules) =>
        Math32.Clamp(2 + damage / 2, rules.MinHitStop, rules.MaxHitStop);

    /// <summary>Dodging and not yet in the dodge's last, hittable frames.</summary>
    public static bool IsIntangible(in Fighter f, FighterTuning t) =>
        f.Action == FighterAction.Dodge && f.ActionFrame < t.DodgeIntangibleFrames;

    /// <summary>In the first frames of a dodge, when a hit is parried instead of dodged.</summary>
    public static bool InParryWindow(in Fighter f, FighterTuning t) =>
        f.Action == FighterAction.Dodge && f.ActionFrame < t.ParryWindowFrames;

    /// <summary>The move's frame data while attacking, otherwise null.</summary>
    public static MoveData? CurrentMove(in Fighter f, FighterTuning t) =>
        f.Action == FighterAction.Attack ? t.Moves[f.Move] : null;

    /// <summary>The fighter's hitbox, if its move's hitbox is out this frame.</summary>
    public static bool TryGetActiveHitbox(in Fighter f, FighterTuning t, out FixRect hitbox)
    {
        MoveData? m = CurrentMove(in f, t);
        if (m == null || !m.IsActiveOn(f.ActionFrame))
        {
            hitbox = default;
            return false;
        }
        hitbox = MoveHitbox(in f, m);
        return true;
    }

    /// <summary>Where a move's hitbox would be for a fighter at its current position and facing.</summary>
    public static FixRect MoveHitbox(in Fighter f, MoveData m)
    {
        Fix x = f.Position.X, y = f.Position.Y;
        if (m.HitsBothSides) return new FixRect(x - m.HitboxFar, y + m.HitboxBottom, x + m.HitboxFar, y + m.HitboxTop);
        return new FixRect(x + m.HitboxNear * f.Facing, y + m.HitboxBottom, x + m.HitboxFar * f.Facing, y + m.HitboxTop);
    }

    public static FixRect Hurtbox(in Fighter f, FighterTuning t) =>
        new(f.Position.X - t.HalfWidth, f.Position.Y, f.Position.X + t.HalfWidth, f.Position.Y + t.Height);

    /// <summary>Steps one fighter. Returns true if it spent this frame frozen in hit-stop.</summary>
    private static bool UpdateFighter(GameState state, ref Fighter f, int index, Buttons input, MatchRules rules)
    {
        FighterTuning t = rules.Tuning;

        if (f.Action == FighterAction.Eliminated || f.Action == FighterAction.Respawning)
        {
            f.PreviousButtons = input;
            if (f.Action == FighterAction.Respawning && ++f.ActionFrame >= rules.RespawnFrames)
            {
                Spawn(ref f, index, rules);
                f.Shield = rules.RespawnShield; // the hidden second wind, only after a respawn
            }
            return false;
        }

        // Frozen in hit-stop: nothing moves or counts down. PreviousButtons is left alone too, so
        // a button pressed during the freeze and still held afterwards counts as a fresh press.
        if (f.HitStop > 0)
        {
            if (--f.HitStop == 0) f.FrozenByHit = false;
            return true;
        }

        Buttons pressed = input & ~f.PreviousButtons;
        f.PreviousButtons = input;
        TickTimers(ref f, rules);

        bool gravity = true;
        switch (f.Action)
        {
            case FighterAction.Hitstun:
            case FighterAction.Stunned:
                f.HitstunFrames--;
                if (f.HitstunFrames <= 0) f.Action = FighterAction.Idle;
                if (f.Grounded) f.Velocity.X = Fix.MoveToward(f.Velocity.X, Fix.Zero, t.GroundAccel);
                break;

            case FighterAction.Attack:
                gravity = UpdateAttack(ref f, input, t);
                break;

            case FighterAction.Dodge:
                gravity = UpdateDodge(ref f, t);
                break;

            case FighterAction.Idle:
                gravity = HandleControl(state, ref f, input, pressed, rules);
                break;
        }

        ApplyPhysics(ref f, rules, gravity);
        return false;
    }

    /// <summary>
    /// A ground slam that has reached the ground stops plunging. Runs after
    /// <see cref="ResolveHits"/>, so the frame it lands on still has its hitbox out: that is the
    /// impact, and a slam started less than one frame's plunge above the floor has no other. The
    /// landing frame becomes the slam's last active frame, so its full recovery follows.
    /// </summary>
    private static void EndPlungeOnLanding(ref Fighter f, FighterTuning t)
    {
        MoveData? move = CurrentMove(in f, t);
        if (f.Grounded && move != null && move.PlungeSpeed != Fix.Zero && move.IsActiveOn(f.ActionFrame))
            f.ActionFrame = move.Startup + move.Active - 1;
    }

    private static void TickTimers(ref Fighter f, MatchRules rules)
    {
        if (f.DodgeCooldown > 0) f.DodgeCooldown--;
        if (f.ArmorFrames > 0) f.ArmorFrames--;
        if (f.LastHitTimer > 0) f.LastHitTimer--;
        if (f.GutsTimer > 0 && --f.GutsTimer == 0)
        {
            f.Josh = Math32.Max(0, f.Josh - rules.GutsHeal);
            f.ArmorFrames = rules.GutsArmorFrames;
        }
    }

    /// <summary>
    /// Movement, then at most one of: a dodge, a heavy, a jab or a jump, in that order of priority.
    /// A jump pressed on the same frame as an attack or dodge is ignored (and not spent), so the
    /// action is the one for where the fighter stands: Jump + Down + Heavy on the ground is the
    /// down heavy, not a ground slam that would cancel the jump. Returns whether gravity applies.
    /// </summary>
    private static bool HandleControl(GameState state, ref Fighter f, Buttons input, Buttons pressed, MatchRules rules)
    {
        FighterTuning t = rules.Tuning;
        int dir = Horizontal(input);
        if (dir != 0) f.Facing = dir;

        Fix targetVx = dir * (f.Grounded ? t.RunSpeed : t.AirSpeed);
        if (f.Weapon == WeaponId.BowlingBall) targetVx = targetVx * 4 / 5; // the ball is heavy
        f.Velocity.X = Fix.MoveToward(f.Velocity.X, targetVx, f.Grounded ? t.GroundAccel : t.AirAccel);

        if ((pressed & Buttons.Dodge) != 0 && f.DodgeCooldown == 0 && (f.Grounded || !f.AirDodgeUsed))
        {
            StartDodge(ref f, input, t);
            return false;
        }

        MoveId move = MoveId.None;
        f.FlurryNext = MoveId.None;
        const Buttons both = Buttons.Light | Buttons.Heavy;
        if (f.Weapon != WeaponId.None) move = ArmedMove(in f, input, pressed);
        else if (rules.Flurry && (pressed & both) == both && (input & Buttons.Up) == 0)
        {
            // The fist flurry: the match's own random stream picks it, so every device agrees
            // and nobody can choose the result.
            int roll = state.Random.Range(0, 4);
            move = roll == 0 || roll == 2 ? MoveId.Jab : MoveId.NeutralHeavy;
            if (roll >= 2) f.FlurryNext = MoveId.NeutralHeavy;
        }
        else
        {
            if ((pressed & Buttons.Heavy) != 0) move = HeavyFor(in f, input, dir);
            if (move == MoveId.None && (pressed & Buttons.Light) != 0) move = MoveId.Jab;
        }
        if (move == MoveId.None)
        {
            if ((pressed & Buttons.Jump) != 0) Jump(ref f, t);
            return true;
        }

        f.Action = FighterAction.Attack;
        f.Move = move;
        f.ActionFrame = 0;
        f.AttackConnected = false;
        f.UziShots = 0;
        return AttackMotion(ref f, input, t, steer: false);
    }

    /// <summary>
    /// Armed, the attack buttons are one button: Light, Heavy or both fire the weapon's attack once,
    /// or throw it when it's empty. Drop weapon throws it. Up + Heavy stays the recovery.
    /// </summary>
    private static MoveId ArmedMove(in Fighter f, Buttons input, Buttons pressed)
    {
        if ((pressed & Buttons.Heavy) != 0 && (input & Buttons.Up) != 0) return f.RecoveryUsed ? MoveId.None : MoveId.UpHeavy;
        if ((pressed & Buttons.DropWeapon) != 0) return MoveId.WeaponThrow;
        if ((pressed & (Buttons.Light | Buttons.Heavy)) == 0) return MoveId.None;
        return f.WeaponSupply > 0 ? Weapons.Get(f.Weapon).Move : MoveId.WeaponThrow;
    }

    private static void Jump(ref Fighter f, FighterTuning t)
    {
        if (f.Grounded)
        {
            f.Velocity.Y = t.JumpVelocity;
            f.Grounded = false;
        }
        else if (f.AirJumpsLeft > 0)
        {
            f.Velocity.Y = t.AirJumpVelocity;
            f.AirJumpsLeft--;
        }
    }

    /// <summary>
    /// Up: up heavy (nothing if the recovery is spent). Down: down heavy, or the ground slam in the
    /// air. Left or right: side heavy. Otherwise neutral heavy.
    /// </summary>
    private static MoveId HeavyFor(in Fighter f, Buttons input, int dir)
    {
        if ((input & Buttons.Up) != 0) return f.RecoveryUsed ? MoveId.None : MoveId.UpHeavy;
        if ((input & Buttons.Down) != 0) return f.Grounded ? MoveId.DownHeavy : MoveId.GroundSlam;
        return dir != 0 ? MoveId.SideHeavy : MoveId.NeutralHeavy;
    }

    private static bool UpdateAttack(ref Fighter f, Buttons input, FighterTuning t)
    {
        MoveData m = t.Moves[f.Move];
        f.ActionFrame++;
        // The Uzi keeps firing while an attack button is held (and it has rounds).
        if (f.Move == MoveId.UziFire && f.ActionFrame == m.Startup + Weapons.UziInterval
            && (input & (Buttons.Light | Buttons.Heavy)) != 0 && f.WeaponSupply > 0)
            f.ActionFrame = m.Startup;
        if (f.ActionFrame >= m.TotalFrames && f.FlurryNext != MoveId.None)
        {
            // The flurry's second hit starts the frame after the first one ends: no gap to dodge in.
            f.Move = f.FlurryNext;
            f.FlurryNext = MoveId.None;
            f.ActionFrame = 0;
            f.AttackConnected = false;
            return AttackMotion(ref f, input, t, steer: false);
        }
        if (f.ActionFrame >= m.TotalFrames)
        {
            f.Action = FighterAction.Idle;
            f.Move = MoveId.None;
            if (f.Grounded) f.Velocity.X = Fix.MoveToward(f.Velocity.X, Fix.Zero, t.GroundAccel);
            return true;
        }
        return AttackMotion(ref f, input, t, steer: true);
    }

    /// <summary>
    /// The current move's motion on its current frame. <paramref name="steer"/> is false on the
    /// press frame, which has already had normal movement. Returns whether gravity applies.
    /// </summary>
    private static bool AttackMotion(ref Fighter f, Buttons input, FighterTuning t, bool steer)
    {
        MoveData m = t.Moves[f.Move];
        if (steer)
        {
            // Attacks lock steering: friction on the ground, drift kept in the air. Exceptions: a
            // move with air control steers, and a lunge brakes in the air too (more slowly than on
            // the ground), or an airborne side heavy would carry the fighter several metres.
            if (f.Grounded && f.Move == MoveId.UziFire) // walk at half speed while firing, without turning
                f.Velocity.X = Fix.MoveToward(f.Velocity.X, Horizontal(input) * t.RunSpeed / 2, t.GroundAccel);
            else if (f.Grounded) f.Velocity.X = Fix.MoveToward(f.Velocity.X, Fix.Zero, t.GroundAccel);
            else if (m.AirControl) f.Velocity.X = Fix.MoveToward(f.Velocity.X, Horizontal(input) * t.AirSpeed, t.AirAccel);
            else if (m.LungeSpeed != Fix.Zero && f.ActionFrame > m.ImpulseFrame)
                f.Velocity.X = Fix.MoveToward(f.Velocity.X, Fix.Zero, t.AirAccel);
        }

        if (f.ActionFrame == m.ImpulseFrame)
        {
            if (m.LungeSpeed != Fix.Zero) f.Velocity.X = m.LungeSpeed * f.Facing;
            if (m.RiseSpeed != Fix.Zero)
            {
                f.Velocity.Y = m.RiseSpeed;
                f.Grounded = false;
                if (m.UsesRecovery) f.RecoveryUsed = true; // here, not at the press: landing would refund it
            }
        }

        if (m.PlungeSpeed != Fix.Zero)
        {
            if (f.ActionFrame < m.Startup)
            {
                f.Velocity = FixVec2.Zero; // hang still while winding up
                return false;
            }
            if (m.IsActiveOn(f.ActionFrame))
            {
                f.Velocity = new FixVec2(Fix.Zero, -m.PlungeSpeed);
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Starts a dodge in the held direction. On the ground, left or right dashes and anything else
    /// is a spot dodge; in the air it dashes in any of 8 directions, or hangs still with none.
    /// </summary>
    private static void StartDodge(ref Fighter f, Buttons input, FighterTuning t)
    {
        f.Action = FighterAction.Dodge;
        f.Move = MoveId.None;
        f.ActionFrame = 0;
        f.AttackConnected = false;

        int dx = Horizontal(input);
        if (f.Grounded)
        {
            f.Velocity = new FixVec2(t.GroundDodgeSpeed * dx, Fix.Zero);
            return;
        }

        f.AirDodgeUsed = true;
        int dy = ((input & Buttons.Up) != 0 ? 1 : 0) - ((input & Buttons.Down) != 0 ? 1 : 0);
        Fix speed = dx != 0 && dy != 0 ? t.AirDodgeSpeed * Diagonal : t.AirDodgeSpeed;
        f.Velocity = new FixVec2(speed * dx, speed * dy);
    }

    /// <summary>
    /// A dodge moves in a straight line, without gravity, while it can't be hit. Then an airborne
    /// fighter keeps half that speed and falls; a grounded one slows to a stop.
    /// </summary>
    private static bool UpdateDodge(ref Fighter f, FighterTuning t)
    {
        f.ActionFrame++;
        if (f.ActionFrame >= t.DodgeTotalFrames)
        {
            f.Action = FighterAction.Idle;
            f.DodgeCooldown = t.DodgeCooldownFrames;
        }
        else if (f.ActionFrame < t.DodgeIntangibleFrames)
        {
            return false;
        }
        else if (f.ActionFrame == t.DodgeIntangibleFrames && !f.Grounded)
        {
            f.Velocity = new FixVec2(f.Velocity.X / 2, f.Velocity.Y / 2);
        }

        if (f.Grounded) f.Velocity.X = Fix.MoveToward(f.Velocity.X, Fix.Zero, t.GroundAccel);
        return true;
    }

    private static int Horizontal(Buttons input) =>
        ((input & Buttons.Right) != 0 ? 1 : 0) - ((input & Buttons.Left) != 0 ? 1 : 0);

    private static void ApplyPhysics(ref Fighter f, MatchRules rules, bool gravity)
    {
        FighterTuning t = rules.Tuning;
        if (gravity && !f.Grounded) f.Velocity.Y = Fix.Max(f.Velocity.Y - t.Gravity, -t.MaxFallSpeed);

        Fix prevY = f.Position.Y;
        FixVec2 next = f.Position + f.Velocity;

        // One-way platforms: land when moving down through a top surface. Highest surface wins.
        bool landed = false;
        Fix landY = Fix.Zero;
        if (f.Velocity.Y <= Fix.Zero)
        {
            var platforms = rules.Stage.Platforms;
            for (int i = 0; i < platforms.Count; i++)
            {
                FixRect p = platforms[i];
                if (next.X < p.MinX || next.X > p.MaxX) continue;
                if (prevY < p.MaxY || next.Y > p.MaxY) continue;
                if (!landed || p.MaxY > landY)
                {
                    landed = true;
                    landY = p.MaxY;
                }
            }
        }

        if (landed)
        {
            next.Y = landY;
            f.Velocity.Y = Fix.Zero;
            f.Grounded = true;
            f.AirJumpsLeft = t.AirJumps;
            f.AirDodgeUsed = false;
            f.RecoveryUsed = false;
        }
        else
        {
            f.Grounded = false;
        }

        f.Position = next;
    }

    /// <summary>
    /// Finds every attacker whose active hitbox touches another fighter, then applies parries and
    /// hits. Fighters that spent this frame frozen in hit-stop don't attack: a frozen hitbox waits
    /// until its owner moves again (this matters for an armored fighter hit mid-attack).
    /// </summary>
    private static void ResolveHits(GameState state, ReadOnlySpan<bool> frozen, MatchRules rules)
    {
        Fighter[] fighters = state.Fighters;
        FighterTuning t = rules.Tuning;
        int n = fighters.Length;

        // Collect first, apply after, so simultaneous hits trade fairly regardless of player index.
        // Each contact records the move, because applying a parry clears the attacker's move.
        Span<int> contactAttacker = stackalloc int[n * n];
        Span<int> contactTarget = stackalloc int[n * n];
        Span<byte> contactMove = stackalloc byte[n * n];
        Span<bool> contactParried = stackalloc bool[n * n];
        int contacts = 0;

        for (int a = 0; a < n; a++)
        {
            ref Fighter atk = ref fighters[a];
            if (atk.AttackConnected || frozen[a]) continue;
            if (!TryGetActiveHitbox(in atk, t, out FixRect hitbox)) continue;

            for (int d = 0; d < n; d++)
            {
                ref Fighter def = ref fighters[d];
                if (d == a || !def.IsActive) continue;
                if (!hitbox.Overlaps(Hurtbox(in def, t))) continue;

                bool parried = InParryWindow(in def, t);
                if (!parried && IsIntangible(in def, t)) continue; // dodged: the attack can still hit later

                contactAttacker[contacts] = a;
                contactTarget[contacts] = d;
                contactMove[contacts] = (byte)atk.Move;
                contactParried[contacts] = parried;
                contacts++;
            }
        }

        // Parries before hits: an attacker who is parried by one fighter and hit by another on the
        // same frame ends up knocked back, not just stunned.
        for (int i = 0; i < contacts; i++)
            if (contactParried[i]) ApplyParry(ref fighters[contactAttacker[i]], ref fighters[contactTarget[i]], rules);
        for (int i = 0; i < contacts; i++)
            if (!contactParried[i]) ApplyHit(state, contactAttacker[i], contactTarget[i], (MoveId)contactMove[i], rules);
    }

    /// <summary>
    /// The rage multiplier, in percent, for hits by a fighter at <paramref name="josh"/>:
    /// 100 + RagePercent × min(josh, RageJosh) / RageJosh.
    /// </summary>
    public static int RageMultiplier(int josh, MatchRules rules) =>
        rules.RagePercent <= 0 ? 100 : 100 + Math32.Min(josh, rules.RageJosh) * rules.RagePercent / rules.RageJosh;

    /// <summary>
    /// The attacker is stunned in place: it stops moving sideways and stops rising, so a parried
    /// up heavy drops instead of carrying on up out of reach. The defender's dodge ends at once,
    /// with no cooldown and with its air dodge given back, so it can punish and, if it parried in
    /// the air, still recover; it keeps moving the way a dodge does after its invincible frames
    /// (half speed in the air). Both freeze for the longest hit-stop.
    /// </summary>
    private static void ApplyParry(ref Fighter atk, ref Fighter def, MatchRules rules)
    {
        atk.AttackConnected = true;
        atk.Action = FighterAction.Stunned;
        atk.Move = MoveId.None;
        atk.FlurryNext = MoveId.None;
        atk.ActionFrame = 0;
        atk.HitstunFrames = rules.Tuning.ParryStunFrames;
        atk.Velocity = new FixVec2(Fix.Zero, Fix.Min(atk.Velocity.Y, Fix.Zero));
        atk.HitStop = Math32.Max(atk.HitStop, rules.MaxHitStop);
        ParryReward(ref def, rules);
    }

    /// <summary>The parrying defender's reward, for a fighter's attack or a projectile.</summary>
    private static void ParryReward(ref Fighter def, MatchRules rules)
    {
        if (def.Action == FighterAction.Dodge) // only once, if it parries two attackers on one frame
        {
            def.Action = FighterAction.Idle;
            def.ActionFrame = 0;
            def.AirDodgeUsed = false;
            if (!def.Grounded) def.Velocity = new FixVec2(def.Velocity.X / 2, def.Velocity.Y / 2);
        }
        def.HitStop = Math32.Max(def.HitStop, rules.MaxHitStop);
    }

    /// <summary>A hit about to land: a move's numbers after weapon bonuses.</summary>
    private struct Hit
    {
        public int Damage;
        public int Percent;          // damage multiplier: the signature bonus and sweet spots
        public Fix BaseKnockback;
        public Fix KnockbackGrowth;
        public Fix LaunchX;
        public Fix LaunchY;
        public int HitstunBonus;
        public bool NoHitStop;       // Uzi bullets: the target doesn't freeze
        public bool MaxHitStop;      // sweet spots always freeze the longest
        public WeaponId Weapon;      // what it came from (None: fists or a hazard)
        public bool Sweet;           // a sweet-spot hit

        public Hit(MoveData m)
        {
            Damage = m.Damage;
            Percent = 100;
            BaseKnockback = m.BaseKnockback;
            KnockbackGrowth = m.KnockbackGrowth;
            LaunchX = m.LaunchX;
            LaunchY = m.LaunchY;
            HitstunBonus = m.HitstunBonus;
            NoHitStop = false;
            MaxHitStop = false;
            Weapon = WeaponId.None;
            Sweet = false;
        }

        /// <summary>A sweet-spot hit: 1.25x damage and base knockback, the longest hit-stop.</summary>
        public void Sweeten()
        {
            Percent = Percent * Weapons.SweetPercent / 100;
            BaseKnockback = BaseKnockback * Weapons.SweetPercent / 100;
            MaxHitStop = true;
            Sweet = true;
        }
    }

    /// <summary>A fighter's move connects: fists, or a melee weapon (belan, bat, shotgun).</summary>
    private static void ApplyHit(GameState state, int attacker, int target, MoveId move, MatchRules rules)
    {
        Fighter[] fighters = state.Fighters;
        ref Fighter atk = ref fighters[attacker];
        ref Fighter def = ref fighters[target];
        atk.AttackConnected = true;
        MoveData m = rules.Tuning.Moves[move];
        var hit = new Hit(m);

        // Launch away from the attacker. For moves whose hitbox reaches behind the attacker (the
        // up heavy, the slam, the sweep) that depends on which side the target is on.
        int side = atk.Facing;
        bool reachesBehind = m.HitsBothSides || m.HitboxNear < Fix.Zero;
        if (reachesBehind && def.Position.X != atk.Position.X) side = def.Position.X > atk.Position.X ? 1 : -1;

        WeaponId weapon = WeaponId.None;
        if (move == MoveId.BelanBonk || move == MoveId.BatSwing || move == MoveId.ShotgunBlast)
        {
            weapon = atk.Weapon;
            MeleeWeaponHit(ref atk, in def, move, ref hit, rules);
        }
        if (weapon != WeaponId.None && rules.SignatureOf(attacker) == weapon) hit.Percent = hit.Percent * Weapons.SignaturePercent / 100;
        hit.Weapon = weapon;
        LandHit(state, attacker, target, in hit, side, freezeAttacker: true, rules);
    }

    /// <summary>
    /// A melee weapon's sweet spot and quirks. Belan: bonking someone in the middle of their own
    /// attack dizzies them longer. Bat: the very tip (1.3 m or more away) launches flat, the "six".
    /// Shotgun: point blank (within 1.2 m) is the sweet spot; beyond 3 m it does only 10. Belan and
    /// bat lose a hit of durability.
    /// </summary>
    private static void MeleeWeaponHit(ref Fighter atk, in Fighter def, MoveId move, ref Hit hit, MatchRules rules)
    {
        FixRect hurt = Hurtbox(in def, rules.Tuning);
        Fix near = atk.Facing > 0 ? hurt.MinX - atk.Position.X : atk.Position.X - hurt.MaxX;
        if (move == MoveId.BelanBonk)
        {
            if (atk.WeaponSupply > 0) atk.WeaponSupply--;
            if (def.Action == FighterAction.Attack)
            {
                hit.Sweeten();
                hit.HitstunBonus += 6;
            }
        }
        else if (move == MoveId.BatSwing)
        {
            if (atk.WeaponSupply > 0) atk.WeaponSupply--;
            if (near >= Weapons.BatTip)
            {
                hit.Sweeten();
                hit.LaunchX = Weapons.SixLaunchX;
                hit.LaunchY = Weapons.SixLaunchY;
            }
        }
        else
        {
            if (near > Weapons.ShotgunFar) hit.Damage = Weapons.ShotgunFarDamage;
            else if (near <= Weapons.PointBlank) hit.Sweeten();
        }
    }

    /// <summary>
    /// Applies a hit: rage, the hidden second wind, Josh, hit-stop, Guts, then knockback in
    /// direction <paramref name="side"/>. Projectiles don't freeze their owner.
    /// </summary>
    private static void LandHit(GameState state, int attacker, int target, in Hit hit, int side, bool freezeAttacker, MatchRules rules)
    {
        Fighter[] fighters = state.Fighters;
        ref Fighter atk = ref fighters[attacker];
        ref Fighter def = ref fighters[target];

        // Rage: the attacker's own Josh makes its hits stronger (a comeback when close to death).
        int rage = RageMultiplier(atk.Josh, rules);
        int damage = hit.Damage * rage / 100;
        if (hit.Percent != 100) damage = damage * hit.Percent / 100;

        // The hidden second wind soaks damage first.
        int absorbed = Math32.Min(def.Shield, damage);
        def.Shield -= absorbed;
        def.Josh = Math32.Min(rules.MaxJosh, def.Josh + damage - absorbed);
        def.LastHitBy = attacker + 1;
        def.LastHitTimer = rules.KoCreditFrames;
        def.LastHitWeapon = hit.Weapon;
        def.LastHitSweet = hit.Sweet;
        def.HitCount++;
        if (attacker != target)
        {
            // The attacker's record with this weapon, for the winner's nickname.
            int stats = (attacker * Weapons.Count + (int)hit.Weapon) * 3;
            state.WeaponStats[stats] += damage;
            if (hit.Sweet) state.WeaponStats[stats + 2]++;
        }
        if (!hit.NoHitStop)
        {
            int stop = hit.MaxHitStop ? rules.MaxHitStop : HitStopFrames(damage, rules);
            if (freezeAttacker) atk.HitStop = Math32.Max(atk.HitStop, stop);
            def.HitStop = Math32.Max(def.HitStop, stop);
            def.FrozenByHit = true;
        }

        if (rules.Guts && !def.GutsUsed && def.Josh >= rules.GutsJosh)
        {
            def.GutsUsed = true;
            def.GutsTimer = rules.GutsDelayFrames;
        }

        if (def.ArmorFrames > 0) return; // Guts armor: takes the Josh, keeps its footing

        // A dodge cut short by a hit still costs its cooldown (the dodge would have set it on ending).
        if (def.Action == FighterAction.Dodge) def.DodgeCooldown = rules.Tuning.DodgeCooldownFrames;

        Fix kb = hit.BaseKnockback + hit.KnockbackGrowth * def.Josh;
        if (rage != 100) kb = kb * rage / 100;
        def.Velocity = new FixVec2(kb * hit.LaunchX * side, kb * hit.LaunchY);
        def.Grounded = false;
        def.Action = FighterAction.Hitstun;
        def.Move = MoveId.None;
        def.FlurryNext = MoveId.None;
        def.ActionFrame = 0;
        def.HitstunFrames = Math32.Max(1, 8 + def.Josh / 8 + hit.HitstunBonus);
        def.AirDodgeUsed = false; // getting hit gives back the air dodge and the recovery
        def.RecoveryUsed = false;
    }

    private static void CheckBlastZone(GameState state, int index, MatchRules rules)
    {
        Fighter[] fighters = state.Fighters;
        ref Fighter f = ref fighters[index];
        if (!f.IsActive || rules.Stage.BlastZone.Contains(f.Position)) return;

        // The KO goes to whoever hit this fighter last, if that was recent; a lone fall scores none.
        f.Falls++;
        if (f.LastHitTimer > 0 && f.LastHitBy > 0 && f.LastHitBy - 1 != index)
        {
            fighters[f.LastHitBy - 1].Kos++;
            state.WeaponStats[((f.LastHitBy - 1) * Weapons.Count + (int)f.LastHitWeapon) * 3 + 1]++; // the weapon's KO
        }
        f.LastHitBy = 0;
        f.LastHitTimer = 0;

        bool timed = rules.TimeLimitFrames > 0;
        if (!timed) f.Stocks--;
        f.Velocity = FixVec2.Zero;
        f.Josh = 0;
        f.ActionFrame = 0;
        f.Move = MoveId.None;
        f.HitStop = 0;
        f.FrozenByHit = false;
        f.GutsTimer = 0; // losing the stock cancels a pending Guts
        f.ArmorFrames = 0;
        f.Shield = 0;
        f.Action = timed || f.Stocks > 0 ? FighterAction.Respawning : FighterAction.Eliminated;
    }

    private static void CheckMatchOver(GameState state, MatchRules rules)
    {
        if (rules.TimeLimitFrames > 0)
        {
            if (state.Frame + 1 < rules.TimeLimitFrames) return;
            state.MatchOver = true;
            state.WinnerIndex = TimedWinner(state.Fighters);
            return;
        }

        if (state.Fighters.Length < 2) return;

        int alive = 0, last = -1;
        for (int i = 0; i < state.Fighters.Length; i++)
        {
            if (state.Fighters[i].Action == FighterAction.Eliminated) continue;
            alive++;
            last = i;
        }

        if (alive <= 1)
        {
            state.MatchOver = true;
            state.WinnerIndex = alive == 1 ? last : -1;
        }
    }

    /// <summary>
    /// A fighter's main weapon in this match, for their nickname: the weapon (0 is fists) that did
    /// the most of their damage, if that is at least 40% of it; otherwise -1, an all-rounder.
    /// <paramref name="master"/>: they also landed 3 or more sweet spots and a KO with it.
    /// </summary>
    public static int MainWeapon(GameState state, int fighter, out bool master)
    {
        master = false;
        int total = 0, best = -1, bestDamage = 0;
        int from = fighter * Weapons.Count * 3;
        for (int w = 0; w < Weapons.Count; w++)
        {
            int damage = state.WeaponStats[from + w * 3];
            total += damage;
            if (damage > bestDamage)
            {
                best = w;
                bestDamage = damage;
            }
        }
        if (best < 0 || bestDamage * 100 < total * 40) return -1;
        master = state.WeaponStats[from + best * 3 + 2] >= 3 && state.WeaponStats[from + best * 3 + 1] >= 1;
        return best;
    }

    /// <summary>
    /// The stage's hazards, while active: a launch pad flings up a fighter standing in it, a
    /// pusher carries everyone in it sideways, and a striker hits everyone in it every
    /// <see cref="Hazards.HitEvery"/> frames. A dodging fighter is untouched. Hazard hits credit
    /// nobody with a KO.
    /// </summary>
    private static void ApplyHazards(GameState state, MatchRules rules)
    {
        var hazards = rules.Stage.Hazards;
        Fighter[] fighters = state.Fighters;
        for (int k = 0; k < hazards.Count; k++)
        {
            int at = Hazards.CycleAt(state.Frame, k);
            if (at < Hazards.ActiveAt || at >= Hazards.CooldownAt) continue;
            Hazard h = hazards[k];
            for (int i = 0; i < fighters.Length; i++)
            {
                ref Fighter f = ref fighters[i];
                if (!f.IsActive || IsIntangible(in f, rules.Tuning)) continue;
                if (!h.Zone.Overlaps(Hurtbox(in f, rules.Tuning))) continue;
                switch (h.Kind)
                {
                    case HazardKind.Launch:
                        if (f.Grounded && f.Action != FighterAction.Hitstun)
                        {
                            f.Velocity.Y = Hazards.LaunchSpeed;
                            f.Grounded = false;
                        }
                        break;
                    case HazardKind.Push:
                        f.Position.X += Hazards.PushSpeed * h.Direction;
                        break;
                    case HazardKind.Hit:
                        if ((at - Hazards.ActiveAt) % Hazards.HitEvery != 0) break;
                        var hit = new Hit(Hazards.Strike);
                        int side = f.Position.X * 2 >= h.Zone.MinX + h.Zone.MaxX ? 1 : -1;
                        LandHit(state, i, i, in hit, side, freezeAttacker: false, rules);
                        break;
                }
            }
        }
    }

    /// <summary>
    /// The timed match's winner: most KOs, then fewest falls. -1 if the best two are level on both.
    /// </summary>
    public static int TimedWinner(Fighter[] fighters)
    {
        int best = -1;
        bool tied = false;
        for (int i = 0; i < fighters.Length; i++)
        {
            if (best < 0) { best = i; continue; }
            ref Fighter f = ref fighters[i];
            ref Fighter b = ref fighters[best];
            if (f.Kos > b.Kos || (f.Kos == b.Kos && f.Falls < b.Falls))
            {
                best = i;
                tied = false;
            }
            else if (f.Kos == b.Kos && f.Falls == b.Falls)
            {
                tied = true;
            }
        }
        return tied ? -1 : best;
    }

    private static void Spawn(ref Fighter f, int index, MatchRules rules)
    {
        var spawns = rules.Stage.SpawnPoints;
        f.Position = spawns[index % spawns.Count];
        f.Velocity = FixVec2.Zero;
        f.Facing = f.Position.X <= Fix.Zero ? 1 : -1;
        f.Grounded = false;
        f.AirJumpsLeft = rules.Tuning.AirJumps;
        f.Josh = 0;
        f.Action = FighterAction.Idle;
        f.Move = MoveId.None;
        f.ActionFrame = 0;
        f.HitstunFrames = 0;
        f.AttackConnected = false;
        f.HitStop = 0;
        f.FrozenByHit = false;
        f.DodgeCooldown = 0;
        f.AirDodgeUsed = false;
        f.RecoveryUsed = false;
        f.GutsUsed = false; // a new stock gets a new Guts
        f.GutsTimer = 0;
        f.ArmorFrames = 0;
        f.Shield = 0;
        f.LastHitBy = 0;
        f.LastHitTimer = 0;
        f.Weapon = WeaponId.None;
        f.WeaponSupply = 0;
        f.UziShots = 0;
        f.UziStreak = 0;
        f.UziTarget = 0;
        f.FlurryNext = MoveId.None;
    }
}

/// <summary>Integer helpers (System.Math is banned in the sim to keep floats out).</summary>
public static class Math32
{
    public static int Min(int a, int b) => a < b ? a : b;
    public static int Max(int a, int b) => a > b ? a : b;
    public static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);
    public static int Abs(int v) => v < 0 ? -v : v;
}
