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
}

/// <summary>
/// The deterministic step function: (state, inputs) -> next state, at a fixed 60 Hz.
/// Graybox scope today: run, jump + air jump, one-way platforms, jab, four heavies plus the
/// ground slam, dodge and parry, hit-stop, Josh % knockback, Guts, and ring-out
/// (docs/design/combat-spec-v0.1.md). Light strings, grabs, weapons and Taali come next.
/// </summary>
public static class Simulation
{
    public const int TicksPerSecond = 60;

    private static readonly Fix Diagonal = Fix.FromRatio(7071, 10000); // ~sin(45deg)

    public static GameState CreateInitialState(MatchRules rules, int fighterCount, ulong seed)
    {
        if (fighterCount < 1 || fighterCount > 8)
            throw new ArgumentOutOfRangeException(nameof(fighterCount), "1-8 fighters (GDD B3).");

        var state = new GameState(fighterCount, seed);
        for (int i = 0; i < fighterCount; i++)
        {
            ref Fighter f = ref state.Fighters[i];
            f.Stocks = rules.Stocks;
            Spawn(ref f, i, rules);
        }
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
            for (int i = 0; i < fighters.Length; i++) frozen[i] = UpdateFighter(ref fighters[i], i, inputs[i], rules);
            ResolveHits(fighters, frozen, rules);
            for (int i = 0; i < fighters.Length; i++)
            {
                EndPlungeOnLanding(ref fighters[i], rules.Tuning);
                CheckBlastZone(ref fighters[i], rules);
            }
            CheckMatchOver(state);
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
    private static bool UpdateFighter(ref Fighter f, int index, Buttons input, MatchRules rules)
    {
        FighterTuning t = rules.Tuning;

        if (f.Action == FighterAction.Eliminated || f.Action == FighterAction.Respawning)
        {
            f.PreviousButtons = input;
            if (f.Action == FighterAction.Respawning && ++f.ActionFrame >= rules.RespawnFrames) Spawn(ref f, index, rules);
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
                gravity = HandleControl(ref f, input, pressed, t);
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
    private static bool HandleControl(ref Fighter f, Buttons input, Buttons pressed, FighterTuning t)
    {
        int dir = Horizontal(input);
        if (dir != 0) f.Facing = dir;

        Fix targetVx = dir * (f.Grounded ? t.RunSpeed : t.AirSpeed);
        f.Velocity.X = Fix.MoveToward(f.Velocity.X, targetVx, f.Grounded ? t.GroundAccel : t.AirAccel);

        if ((pressed & Buttons.Dodge) != 0 && f.DodgeCooldown == 0 && (f.Grounded || !f.AirDodgeUsed))
        {
            StartDodge(ref f, input, t);
            return false;
        }

        MoveId move = MoveId.None;
        if ((pressed & Buttons.Heavy) != 0) move = HeavyFor(in f, input, dir);
        if (move == MoveId.None && (pressed & Buttons.Light) != 0) move = MoveId.Jab;
        if (move == MoveId.None)
        {
            if ((pressed & Buttons.Jump) != 0) Jump(ref f, t);
            return true;
        }

        f.Action = FighterAction.Attack;
        f.Move = move;
        f.ActionFrame = 0;
        f.AttackConnected = false;
        return AttackMotion(ref f, input, t, steer: false);
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
            if (f.Grounded) f.Velocity.X = Fix.MoveToward(f.Velocity.X, Fix.Zero, t.GroundAccel);
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
    private static void ResolveHits(Fighter[] fighters, ReadOnlySpan<bool> frozen, MatchRules rules)
    {
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
            if (!contactParried[i]) ApplyHit(ref fighters[contactAttacker[i]], ref fighters[contactTarget[i]], t.Moves[(MoveId)contactMove[i]], rules);
    }

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
        atk.ActionFrame = 0;
        atk.HitstunFrames = rules.Tuning.ParryStunFrames;
        atk.Velocity = new FixVec2(Fix.Zero, Fix.Min(atk.Velocity.Y, Fix.Zero));
        atk.HitStop = Math32.Max(atk.HitStop, rules.MaxHitStop);

        if (def.Action == FighterAction.Dodge) // only once, if it parries two attackers on one frame
        {
            def.Action = FighterAction.Idle;
            def.ActionFrame = 0;
            def.AirDodgeUsed = false;
            if (!def.Grounded) def.Velocity = new FixVec2(def.Velocity.X / 2, def.Velocity.Y / 2);
        }
        def.HitStop = Math32.Max(def.HitStop, rules.MaxHitStop);
    }

    private static void ApplyHit(ref Fighter atk, ref Fighter def, MoveData m, MatchRules rules)
    {
        atk.AttackConnected = true;

        def.Josh = Math32.Min(rules.MaxJosh, def.Josh + m.Damage);
        int stop = HitStopFrames(m.Damage, rules);
        atk.HitStop = Math32.Max(atk.HitStop, stop);
        def.HitStop = Math32.Max(def.HitStop, stop);
        def.FrozenByHit = true;

        if (rules.Guts && !def.GutsUsed && def.Josh >= rules.GutsJosh)
        {
            def.GutsUsed = true;
            def.GutsTimer = rules.GutsDelayFrames;
        }

        if (def.ArmorFrames > 0) return; // Guts armor: takes the Josh, keeps its footing

        // A dodge cut short by a hit still costs its cooldown (the dodge would have set it on ending).
        if (def.Action == FighterAction.Dodge) def.DodgeCooldown = rules.Tuning.DodgeCooldownFrames;

        // Launch away from the attacker. For moves whose hitbox reaches behind the attacker (the
        // up heavy, the slam, the sweep) that depends on which side the target is on.
        Fix kb = m.BaseKnockback + m.KnockbackGrowth * def.Josh;
        int side = atk.Facing;
        bool reachesBehind = m.HitsBothSides || m.HitboxNear < Fix.Zero;
        if (reachesBehind && def.Position.X != atk.Position.X) side = def.Position.X > atk.Position.X ? 1 : -1;

        def.Velocity = new FixVec2(kb * m.LaunchX * side, kb * m.LaunchY);
        def.Grounded = false;
        def.Action = FighterAction.Hitstun;
        def.Move = MoveId.None;
        def.ActionFrame = 0;
        def.HitstunFrames = 8 + def.Josh / 8 + m.HitstunBonus;
        def.AirDodgeUsed = false; // getting hit gives back the air dodge and the recovery
        def.RecoveryUsed = false;
    }

    private static void CheckBlastZone(ref Fighter f, MatchRules rules)
    {
        if (!f.IsActive || rules.Stage.BlastZone.Contains(f.Position)) return;

        f.Stocks--;
        f.Velocity = FixVec2.Zero;
        f.Josh = 0;
        f.ActionFrame = 0;
        f.Move = MoveId.None;
        f.HitStop = 0;
        f.FrozenByHit = false;
        f.GutsTimer = 0; // losing the stock cancels a pending Guts
        f.ArmorFrames = 0;
        f.Action = f.Stocks > 0 ? FighterAction.Respawning : FighterAction.Eliminated;
    }

    private static void CheckMatchOver(GameState state)
    {
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
