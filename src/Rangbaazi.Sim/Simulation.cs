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

    /// <summary>
    /// Each fighter's team, by fighter index: 0 is no team. Fighters on the same team never hit each
    /// other (<see cref="Simulation.Allies"/>). Null or shorter: no teams. Part of the rules, not of
    /// the state: a fighter's team never changes during a match.
    /// </summary>
    public int[]? Teams { get; init; }

    public int TeamOf(int fighter) =>
        Teams != null && fighter >= 0 && fighter < Teams.Length ? Teams[fighter] : 0;

    /// <summary>A team match: some fighter has a team. Then a timed match is won by a team (<see cref="Simulation.TimedTeamWinner"/>).</summary>
    public bool HasTeams()
    {
        if (Teams == null) return false;
        for (int i = 0; i < Teams.Length; i++)
        {
            if (Teams[i] != 0) return true;
        }
        return false;
    }

    /// <summary>A room match: the timed free-for-all with weapons, rage and the hidden second wind.</summary>
    public static MatchRules Room(StageDefinition stage, int minutes, WeaponId[]? signatures = null, int[]? teams = null) => new()
    {
        Stage = stage,
        TimeLimitFrames = minutes * 60 * Simulation.TicksPerSecond,
        RagePercent = 50,
        RespawnShield = 20,
        Weapons = true,
        Hazards = true,
        Flurry = true,
        Signatures = signatures,
        Teams = teams,
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

    /// <summary>
    /// Bumped when the sim's behaviour changes on purpose; equal to <c>Simulation.SIM_VERSION</c> in
    /// client/sim. 8: the weapon table, prop slots by fighter count, fizzling shots, the placed-prop
    /// limit, the statuses' state, teams, the six-slot record and weighted crates (combat spec v0.4);
    /// team wins, big stages (rooms v1).
    /// </summary>
    public const int SimVersion = 8;

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
            if (rules.Weapons) Sprays(state, frozen, rules);
            if (rules.Weapons) UpdateProps(state, frozen, rules);
            if (rules.Hazards) ApplyHazards(state, rules);
            for (int i = 0; i < fighters.Length; i++)
            {
                EndPlungeOnLanding(ref fighters[i], rules.Tuning);
                FreeIfAirborne(ref fighters[i]);
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

    /// <summary>
    /// The same fighter, or two fighters of one team (<see cref="MatchRules.TeamOf"/>, 0 is none).
    /// Allies never hit each other; with no teams set this is just <c>a == b</c>.
    /// </summary>
    public static bool Allies(MatchRules rules, int a, int b)
    {
        if (a == b) return true;
        int team = rules.TeamOf(a);
        return team != 0 && team == rules.TeamOf(b);
    }

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
        TickTimers(state, ref f, index, rules);

        // Each fresh attack press works a root 12 frames looser, in the middle of an attack too.
        if (f.RootFrames > 0 && (pressed & (Buttons.Light | Buttons.Heavy)) != 0)
        {
            f.RootFrames = Math32.Max(0, f.RootFrames - Weapons.RootPressFrames);
            if (f.RootFrames == 0) f.StunWeapon = 0;
        }

        bool gravity = true;
        switch (f.Action)
        {
            case FighterAction.Hitstun:
            case FighterAction.Stunned:
                f.HitstunFrames--;
                if (f.HitstunFrames <= 0)
                {
                    f.Action = FighterAction.Idle;
                    if (f.RootFrames == 0) f.StunWeapon = 0; // a stun, freeze or slip is over
                }
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

    /// <summary>
    /// The fighter's timers count down, statuses included (so they pause in hit-stop like the
    /// rest). Poison or burn ticks while it runs, on every frame where (frame + index) is a
    /// multiple of its row's <c>dotEvery</c>: the phase doesn't depend on the timer, so refreshing
    /// it every frame neither stops the ticks nor speeds them up.
    /// </summary>
    private static void TickTimers(GameState state, ref Fighter f, int index, MatchRules rules)
    {
        if (f.DodgeCooldown > 0) f.DodgeCooldown--;
        if (f.ArmorFrames > 0) f.ArmorFrames--;
        if (f.LastHitTimer > 0) f.LastHitTimer--;
        if (f.GutsTimer > 0 && --f.GutsTimer == 0)
        {
            f.Josh = Math32.Max(0, f.Josh - rules.GutsHeal);
            f.ArmorFrames = rules.GutsArmorFrames;
        }
        if (f.SlowFrames > 0) f.SlowFrames--;
        if (f.MarkFrames > 0) f.MarkFrames--;
        if (f.StunImmune > 0) f.StunImmune--;
        if (f.RootFrames > 0 && --f.RootFrames == 0) f.StunWeapon = 0;
        if (f.DotFrames > 0)
        {
            if ((state.Frame + index) % WeaponTable.Rows[f.DotWeapon].DotEvery == 0) DotTick(state, ref f, index, rules);
            if (--f.DotFrames == 0)
            {
                f.DotBy = 0;
                f.DotWeapon = 0;
            }
        }
    }

    /// <summary>
    /// One poison or burn tick: 1 damage, and nothing else a hit does (no knockback, hitstun,
    /// hit-stop, rage, mark or hit count). It goes through the hidden second wind, can trigger Guts,
    /// and goes into the poisoner's record. It takes the KO credit only when nobody else holds it,
    /// so poison never steals a ring-out from whoever launched the victim; a fighter's own poison
    /// never takes it.
    /// </summary>
    private static void DotTick(GameState state, ref Fighter f, int index, MatchRules rules)
    {
        int damage = Weapons.DotDamage;
        int absorbed = Math32.Min(f.Shield, damage);
        f.Shield -= absorbed;
        f.Josh = Math32.Min(rules.MaxJosh, f.Josh + damage - absorbed);
        int by = f.DotBy - 1;
        if (by >= 0 && by != index)
        {
            if (f.LastHitTimer == 0 || f.LastHitBy == 0 || f.LastHitBy == f.DotBy || f.LastHitBy == index + 1)
            {
                f.LastHitBy = f.DotBy;
                f.LastHitTimer = rules.KoCreditFrames;
                f.LastHitWeapon = (WeaponId)f.DotWeapon;
                f.LastHitSweet = false;
            }
            RecordHit(state, by, (WeaponId)f.DotWeapon, damage, false);
        }
        if (rules.Guts && !f.GutsUsed && f.Josh >= rules.GutsJosh)
        {
            f.GutsUsed = true;
            f.GutsTimer = rules.GutsDelayFrames;
        }
    }

    /// <summary>
    /// A hold (root, stun, freeze) ends on the first frame its fighter is not on the ground:
    /// nobody is ever held while falling. A parry's stun has no hold weapon and is left alone.
    /// </summary>
    private static void FreeIfAirborne(ref Fighter f)
    {
        if (f.Grounded || f.StunWeapon == 0 || !f.IsActive) return;
        EndHold(ref f);
    }

    /// <summary>Ends a hold: a root, or a stun, freeze or slip (which leave the fighter Idle).</summary>
    private static void EndHold(ref Fighter f)
    {
        if (f.StunWeapon == 0) return;
        if (f.RootFrames == 0 && f.Action == FighterAction.Stunned)
        {
            f.Action = FighterAction.Idle;
            f.HitstunFrames = 0;
        }
        f.RootFrames = 0;
        f.StunWeapon = 0;
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
        int carry = WeaponTable.Rows[(int)f.Weapon].Carry;
        if (carry != 100) targetVx = targetVx * carry / 100; // a heavy weapon slows its carrier
        if (f.SlowFrames > 0) targetVx = targetVx / 2;       // slowed: half speed
        if (f.RootFrames > 0) targetVx = Fix.Zero;           // rooted: it can turn, not walk
        f.Velocity.X = Fix.MoveToward(f.Velocity.X, targetVx, f.Grounded ? t.GroundAccel : t.AirAccel);

        bool rooted = f.RootFrames > 0; // no jump, dodge or recovery either
        if ((pressed & Buttons.Dodge) != 0 && !rooted && f.DodgeCooldown == 0 && (f.Grounded || !f.AirDodgeUsed))
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
            if ((pressed & Buttons.Jump) != 0 && !rooted) Jump(ref f, t);
            return true;
        }

        f.Action = FighterAction.Attack;
        f.Move = move;
        f.ActionFrame = 0;
        f.AttackConnected = false;
        f.BurstShots = 0;
        return AttackMotion(ref f, input, t, steer: false);
    }

    /// <summary>
    /// Armed, the attack buttons are one button: Light, Heavy or both fire the weapon's attack once,
    /// or throw it when it's empty. Drop weapon throws it. Up + Heavy stays the recovery. A weapon
    /// that cycles (the Jugaad Gauntlet) has no attack of its own: each press starts the next of
    /// its hidden rows, picked by the supply left (pull, punch, bolt, from a full one).
    /// </summary>
    private static MoveId ArmedMove(in Fighter f, Buttons input, Buttons pressed)
    {
        if ((pressed & Buttons.Heavy) != 0 && (input & Buttons.Up) != 0) return f.RecoveryUsed || f.RootFrames > 0 ? MoveId.None : MoveId.UpHeavy;
        if ((pressed & Buttons.DropWeapon) != 0) return MoveId.WeaponThrow;
        if ((pressed & (Buttons.Light | Buttons.Heavy)) == 0) return MoveId.None;
        if (f.WeaponSupply <= 0) return MoveId.WeaponThrow;
        WeaponRow row = WeaponTable.Rows[(int)f.Weapon];
        if (row.Use == WeaponUse.Cycle && row.CycleCount > 0)
            return WeaponTable.Rows[row.CycleFirst + (row.CycleCount - f.WeaponSupply % row.CycleCount) % row.CycleCount].Move;
        return row.Move;
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
        if ((input & Buttons.Up) != 0) return f.RecoveryUsed || f.RootFrames > 0 ? MoveId.None : MoveId.UpHeavy;
        if ((input & Buttons.Down) != 0) return f.Grounded ? MoveId.DownHeavy : MoveId.GroundSlam;
        return dir != 0 ? MoveId.SideHeavy : MoveId.NeutralHeavy;
    }

    private static bool UpdateAttack(ref Fighter f, Buttons input, FighterTuning t)
    {
        MoveData m = t.Moves[f.Move];
        f.ActionFrame++;
        // A repeating weapon fires again every interval while it has rounds: while an attack button
        // is held, or until the row's burst is out whether held or not (the Burst Rifle's three),
        // and never more than its burstMax in one hold (the Minigun overheats).
        // A spray's stream loops through its tick while a button is held and it has supply left.
        WeaponRow? row = Weapons.RowOfMove(f.Move);
        bool held = (input & (Buttons.Light | Buttons.Heavy)) != 0;
        if (row != null && row.Use == WeaponUse.Spray)
        {
            if (f.ActionFrame == m.Startup + m.Active && f.WeaponSupply > 0 && held) f.ActionFrame = m.Startup;
        }
        else if (row != null && row.Defence == WeaponDefence.Hold)
        {
            // The Parat: while a button is held the tray stays up, waiting on its last startup
            // frame, for at most 90 frames (counted in BurstShots). Then the bash comes out. A tray
            // that has spent its last supply on a block guards nothing, so it isn't held up either.
            if (f.ActionFrame == m.Startup && held && f.BurstShots < Weapons.HoldMaxFrames && f.WeaponSupply > 0)
            {
                f.ActionFrame = m.Startup - 1;
                f.BurstShots++;
            }
        }
        else if (row != null && row.Interval > 0 && f.ActionFrame == m.Startup + row.Interval && f.WeaponSupply > 0
            && (row.BurstMax == 0 || f.BurstShots < row.BurstMax)
            && (held || f.BurstShots < row.Burst))
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
            // Slowed, a walk or an air steer is half as fast; rooted, there is no walking at all.
            WeaponRow? row = Weapons.RowOfMove(f.Move);
            if (f.Grounded && row != null && row.Walk) // walk at half speed while firing, without turning
            {
                Fix walk = Horizontal(input) * t.RunSpeed / 2;
                if (f.SlowFrames > 0) walk = walk / 2;
                if (f.RootFrames > 0) walk = Fix.Zero;
                f.Velocity.X = Fix.MoveToward(f.Velocity.X, walk, t.GroundAccel);
            }
            else if (f.Grounded) f.Velocity.X = Fix.MoveToward(f.Velocity.X, Fix.Zero, t.GroundAccel);
            else if (m.AirControl)
            {
                Fix toward = Horizontal(input) * t.AirSpeed;
                if (f.SlowFrames > 0) toward = toward / 2;
                f.Velocity.X = Fix.MoveToward(f.Velocity.X, toward, t.AirAccel);
            }
            else if (m.LungeSpeed != Fix.Zero && f.ActionFrame > m.ImpulseFrame)
                f.Velocity.X = Fix.MoveToward(f.Velocity.X, Fix.Zero, t.AirAccel);
        }

        if (f.ActionFrame == m.ImpulseFrame)
        {
            if (m.LungeSpeed != Fix.Zero && f.RootFrames == 0) f.Velocity.X = m.LungeSpeed * f.Facing; // a rooted fighter doesn't lunge or kick
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

    // What a contact of ResolveHits' collect pass is.
    private const byte ContactHit = 0, ContactParried = 1, ContactBlocked = 2, ContactProp = 3;

    /// <summary>
    /// Finds every attacker whose active hitbox touches another fighter or a breakable prop, then
    /// applies them, in this order across all attackers: parries, blocks, prop swings, hits
    /// (combat spec v0.4, section 1.7). Every contact's kind is decided from the state before any
    /// of them is applied, so two hits on one frame never depend on the fighters' indices: a guard
    /// knocked away by one attacker still blocks another's hit of the same frame. Fighters that
    /// spent this frame frozen in hit-stop don't attack: a frozen hitbox waits until its owner
    /// moves again (this matters for an armored fighter hit mid-attack).
    /// </summary>
    private static void ResolveHits(GameState state, ReadOnlySpan<bool> frozen, MatchRules rules)
    {
        Fighter[] fighters = state.Fighters;
        Prop[] props = state.Props;
        FighterTuning t = rules.Tuning;
        int n = fighters.Length;

        // Each contact records the move, because applying a parry clears the attacker's move. A
        // prop contact's target is the prop's slot.
        int most = n * (n + props.Length);
        Span<int> contactAttacker = stackalloc int[most];
        Span<int> contactTarget = stackalloc int[most];
        Span<byte> contactMove = stackalloc byte[most];
        Span<byte> contactKind = stackalloc byte[most];
        int contacts = 0;

        for (int a = 0; a < n; a++)
        {
            ref Fighter atk = ref fighters[a];
            if (atk.AttackConnected || frozen[a]) continue;
            if (!TryGetActiveHitbox(in atk, t, out FixRect hitbox)) continue;

            WeaponRow? row = Weapons.RowOfMove(atk.Move);
            if (row != null && row.Use == WeaponUse.Spray) continue; // a spray's stream is the Sprays pass
            bool first = row != null && row.First;
            bool guardBreak = row != null && row.GuardBreak;
            Fix ax = atk.Position.X;

            // Every breakable prop in the box that isn't the attacker's side's takes a swing (a
            // `first` row only touches the wall that stops it, below).
            if (!first)
            {
                for (int i = 0; i < props.Length; i++)
                {
                    if (!Weapons.Breakable(in props[i]) || (props[i].Owner >= 0 && Allies(rules, a, props[i].Owner))) continue;
                    if (!hitbox.Overlaps(PropBox(in props[i]))) continue;
                    contactAttacker[contacts] = a;
                    contactTarget[contacts] = i;
                    contactMove[contacts] = (byte)atk.Move;
                    contactKind[contacts] = ContactProp;
                    contacts++;
                }
            }

            // A row with `first` (the Sniper Rifle) keeps only its nearest contact, by horizontal
            // distance, the lower index on a tie. A dodging fighter was never a contact, so it
            // doesn't shield the fighter behind it; a parrying or guarding one is, and does.
            int firstAt = contacts;
            Fix firstDist = Fix.Zero;
            for (int d = 0; d < n; d++)
            {
                ref Fighter def = ref fighters[d];
                if (Allies(rules, a, d) || !def.IsActive) continue; // nobody hits a teammate, fists included
                if (!hitbox.Overlaps(Hurtbox(in def, t))) continue;

                bool parried = InParryWindow(in def, t);
                if (!parried && IsIntangible(in def, t)) continue; // dodged: the attack can still hit later
                if (WallBetween(state, ax, def.Position.X, hitbox)) continue; // a wall is in the way
                bool blocked = !parried && !guardBreak && Guarding(in def) && FacesToward(in def, ax);

                if (first)
                {
                    Fix dist = Fix.Abs(def.Position.X - ax);
                    if (contacts > firstAt && dist >= firstDist) continue;
                    contacts = firstAt; // this one is nearer: it takes the place of the one kept so far
                    firstDist = dist;
                }

                contactAttacker[contacts] = a;
                contactTarget[contacts] = d;
                contactMove[contacts] = (byte)atk.Move;
                contactKind[contacts] = parried ? ContactParried : blocked ? ContactBlocked : ContactHit;
                contacts++;
            }

            // A `first` row stops in the nearest wall on its line when nobody stands before it, and
            // takes a swing off it (unless it is its own side's).
            if (first)
            {
                int wall = NearestWall(state, ax, hitbox);
                if (wall >= 0 && (contacts == firstAt || Fix.Abs(props[wall].Position.X - ax) < firstDist)
                    && Weapons.Breakable(in props[wall]) && (props[wall].Owner < 0 || !Allies(rules, a, props[wall].Owner)))
                {
                    contacts = firstAt;
                    contactAttacker[contacts] = a;
                    contactTarget[contacts] = wall;
                    contactMove[contacts] = (byte)atk.Move;
                    contactKind[contacts] = ContactProp;
                    contacts++;
                }
            }
        }

        // Parries before blocks before hits: an attacker who is parried by one fighter and hit by
        // another on the same frame ends up knocked back, not just stunned.
        for (int i = 0; i < contacts; i++)
            if (contactKind[i] == ContactParried) ApplyParry(ref fighters[contactAttacker[i]], ref fighters[contactTarget[i]], rules);
        for (int i = 0; i < contacts; i++)
            if (contactKind[i] == ContactBlocked) ApplyBlock(ref fighters[contactAttacker[i]], ref fighters[contactTarget[i]], rules);
        for (int i = 0; i < contacts; i++)
            if (contactKind[i] == ContactProp) SwingProp(state, contactAttacker[i], contactTarget[i], (MoveId)contactMove[i]);
        for (int i = 0; i < contacts; i++)
            if (contactKind[i] == ContactHit) ApplyHit(state, contactAttacker[i], contactTarget[i], (MoveId)contactMove[i], rules);
    }

    /// <summary>
    /// A guard is up: the fighter's move is a guard (or the Parat's held guard) on one of its
    /// guarding frames, from frame 2 to the end of its startup, and its weapon has supply left
    /// (each block costs one: a guard that has spent its last blocks nothing more). It covers the
    /// side it faces.
    /// </summary>
    public static bool Guarding(in Fighter f)
    {
        if (f.Action != FighterAction.Attack) return false;
        WeaponRow? row = Weapons.RowOfMove(f.Move);
        return row != null && (row.Defence == WeaponDefence.Guard || row.Defence == WeaponDefence.Hold)
            && f.ActionFrame >= Weapons.GuardFrom && f.ActionFrame < row.Startup
            && (f.WeaponSupply > 0 || row.Spend == WeaponSpend.Never);
    }

    /// <summary>Whether a guard held by <paramref name="f"/> covers something at <paramref name="x"/>: on the side it faces, or exactly where it stands.</summary>
    public static bool FacesToward(in Fighter f, Fix x) => x == f.Position.X || Fix.Sign(x - f.Position.X) == f.Facing;

    /// <summary>
    /// Whether a shot comes at a guard held by <paramref name="f"/> from the front: by the way it
    /// moves (towards the guard's face), not by where it is, because a fast bullet is often first
    /// seen overlapping the guard after it has passed the guard's middle. A shot with no sideways
    /// speed is judged by where it is.
    /// </summary>
    public static bool ShotFromFront(in Fighter f, in Prop p) =>
        p.Velocity.X != Fix.Zero ? Fix.Sign(p.Velocity.X) == -f.Facing : FacesToward(in f, p.Position.X);

    /// <summary>
    /// A wall stands between an attacker at <paramref name="ax"/> and a target at
    /// <paramref name="tx"/>: its centre lies between their two x positions and its box overlaps the
    /// attack's box. Anyone's wall stops a swing, a blast, a spray or a pulse.
    /// </summary>
    public static bool WallBetween(GameState state, Fix ax, Fix tx, FixRect box)
    {
        Fix lo = Fix.Min(ax, tx), hi = Fix.Max(ax, tx);
        Prop[] props = state.Props;
        for (int i = 0; i < props.Length; i++)
        {
            if (props[i].Kind != PropKind.Shot || props[i].Shot != ShotKind.Wall) continue;
            Fix cx = props[i].Position.X;
            if (cx > lo && cx < hi && box.Overlaps(PropBox(in props[i]))) return true;
        }
        return false;
    }

    /// <summary>The wall in a box whose centre is nearest <paramref name="ax"/> (the lower slot on a tie), or -1.</summary>
    private static int NearestWall(GameState state, Fix ax, FixRect box)
    {
        Prop[] props = state.Props;
        int best = -1;
        Fix bestDist = Fix.Zero;
        for (int i = 0; i < props.Length; i++)
        {
            if (props[i].Kind != PropKind.Shot || props[i].Shot != ShotKind.Wall || !box.Overlaps(PropBox(in props[i]))) continue;
            Fix dist = Fix.Abs(props[i].Position.X - ax);
            if (best < 0 || dist < bestDist)
            {
                best = i;
                bestDist = dist;
            }
        }
        return best;
    }

    /// <summary>
    /// A fighter hidden in a cloud (a zone whose row <c>hides</c>): its middle is inside one. No
    /// screen draws it, the CPU doesn't see it from more than 2 m, and a homing missile doesn't
    /// steer at it. Worked out from the props, so it adds no state.
    /// </summary>
    public static bool IsHidden(GameState state, MatchRules rules, int fighter)
    {
        ref Fighter f = ref state.Fighters[fighter];
        if (!f.IsActive) return false;
        var middle = new FixVec2(f.Position.X, f.Position.Y + rules.Tuning.Height / 2);
        Prop[] props = state.Props;
        for (int i = 0; i < props.Length; i++)
        {
            if (props[i].Kind != PropKind.Shot || props[i].Shot != ShotKind.Zone || !Weapons.ShotRow(in props[i]).Hides) continue;
            if (PropBox(in props[i]).Contains(middle)) return true;
        }
        return false;
    }

    /// <summary>
    /// A swing or blast blocked by a guard: the attacker's move has connected and it is pushed back,
    /// both freeze for the shortest hit-stop, and the guard's weapon loses one.
    /// </summary>
    private static void ApplyBlock(ref Fighter atk, ref Fighter def, MatchRules rules)
    {
        atk.AttackConnected = true;
        atk.Velocity.X = Weapons.BlockPush * def.Facing;
        atk.HitStop = Math32.Max(atk.HitStop, rules.MinHitStop);
        def.HitStop = Math32.Max(def.HitStop, rules.MinHitStop);
        GuardCost(ref def);
    }

    /// <summary>Each block, of a swing, a shot or a burst, costs the guard one of its weapon's supply.</summary>
    private static void GuardCost(ref Fighter def)
    {
        if (def.WeaponSupply > 0) def.WeaponSupply--;
    }

    /// <summary>
    /// A swing or a blast takes one swing off a breakable prop (counted in IgnoreFighter); at its
    /// row's <c>brk</c> it is gone, without bursting. A row with <c>gbreak</c> breaks it at once.
    /// </summary>
    private static void SwingProp(GameState state, int attacker, int index, MoveId move)
    {
        ref Prop p = ref state.Props[index];
        state.Fighters[attacker].AttackConnected = true;
        if (!Weapons.Breakable(in p)) return; // another swing of this frame already broke it
        WeaponRow row = Weapons.ShotRow(in p);
        WeaponRow? swung = Weapons.RowOfMove(move);
        p.IgnoreFighter = swung != null && swung.GuardBreak ? row.Break : p.IgnoreFighter + 1;
        if (p.IgnoreFighter >= row.Break) p = default;
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
        public int Effect;           // what the hit adds (WeaponEffect) and its number
        public int EffectN;
        public bool NoHitStop;       // chip hits (Uzi bullets): the target doesn't freeze
        public bool MaxHitStop;      // sweet spots always freeze the longest
        public bool Soft;            // damage and effect only: no knockback, hitstun or hit-stop
        public bool ArmorBreak;      // gets past Guts armor
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
            Effect = m.Effect;
            EffectN = m.EffectN;
            NoHitStop = false;
            MaxHitStop = false;
            Soft = false;
            ArmorBreak = false;
            Weapon = WeaponId.None;
            Sweet = false;
        }

        /// <summary>A row's hit flags: chip, soft and armor break.</summary>
        public void Flags(WeaponRow row)
        {
            NoHitStop = row.Chip;
            Soft = row.Soft;
            ArmorBreak = row.ArmorBreak;
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

    /// <summary>A fighter's move connects: fists, or a weapon's swing or blast.</summary>
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
        WeaponRow? row = Weapons.RowOfMove(move);
        if (row != null)
        {
            weapon = atk.Weapon;
            hit.Flags(row);
            MeleeWeaponHit(ref atk, in def, row, ref hit, rules);
        }
        if (weapon != WeaponId.None && rules.SignatureOf(attacker) == weapon) hit.Percent = hit.Percent * Weapons.SignaturePercent / 100;
        hit.Weapon = weapon;
        LandHit(state, attacker, target, in hit, side, freezeAttacker: true, rules);
    }

    /// <summary>
    /// A swing's or a blast's row rules. A row that spends per hit loses one. Beyond its far
    /// distance a blast does its far damage (the shotgun past 3 m). Otherwise its sweet spot:
    /// hitting someone in the middle of their own attack, the tip (at least so far away), point
    /// blank (at most so far), a target in the air, or a target facing away from the attacker (from
    /// behind). A sweet hit is 1.25x; the row may also give it its own launch angle (the bat's flat
    /// "six") and extra hitstun (the belan's dizzy).
    /// </summary>
    private static void MeleeWeaponHit(ref Fighter atk, in Fighter def, WeaponRow row, ref Hit hit, MatchRules rules)
    {
        FixRect hurt = Hurtbox(in def, rules.Tuning);
        Fix near = atk.Facing > 0 ? hurt.MinX - atk.Position.X : atk.Position.X - hurt.MaxX;
        if (row.Spend == WeaponSpend.Hit && atk.WeaponSupply > 0) atk.WeaponSupply--;

        bool sweet = false;
        if (row.FarDamage > 0 && near > row.FarDist) hit.Damage = row.FarDamage;
        else if (row.Sweet == WeaponSweet.Midattack) sweet = def.Action == FighterAction.Attack;
        else if (row.Sweet == WeaponSweet.Tip) sweet = near >= Fix.FromRaw(row.SweetArg);
        else if (row.Sweet == WeaponSweet.Pointblank) sweet = near <= Fix.FromRaw(row.SweetArg);
        else if (row.Sweet == WeaponSweet.Airborne) sweet = !def.Grounded;
        else if (row.Sweet == WeaponSweet.Back)
            sweet = def.Facing == (def.Position.X > atk.Position.X ? 1 : def.Position.X < atk.Position.X ? -1 : atk.Facing);
        if (row.SweetOnly && !sweet) hit.Effect = WeaponEffect.None; // the Chimta disarms with its tip only
        if (!sweet) return;

        hit.Sweeten();
        if (row.SweetLaunchX != Fix.Zero || row.SweetLaunchY != Fix.Zero)
        {
            hit.LaunchX = row.SweetLaunchX;
            hit.LaunchY = row.SweetLaunchY;
        }
        hit.HitstunBonus += row.SweetStun;
    }

    /// <summary>
    /// Applies a hit: rage, sweet and signature percent, the mark's 125%, the hidden second wind,
    /// Josh, the last-hit record and stats, hit-stop (not for chip or soft hits), Guts, the armor
    /// check (skipped by armor break), the row's effect, and then, unless the hit is soft or a hold
    /// took, knockback in direction <paramref name="side"/>. Projectiles don't freeze their owner.
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
        if (def.MarkFrames > 0) damage = damage * Weapons.MarkPercent / 100; // marked: 25% more from every hit

        // The hidden second wind soaks damage first.
        int absorbed = Math32.Min(def.Shield, damage);
        def.Shield -= absorbed;
        def.Josh = Math32.Min(rules.MaxJosh, def.Josh + damage - absorbed);
        def.LastHitBy = attacker + 1;
        def.LastHitTimer = rules.KoCreditFrames;
        def.LastHitWeapon = hit.Weapon;
        def.LastHitSweet = hit.Sweet;
        def.HitCount++;
        if (attacker != target) RecordHit(state, attacker, hit.Weapon, damage, hit.Sweet); // for the winner's nickname
        if (!hit.NoHitStop && !hit.Soft)
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

        if (def.ArmorFrames > 0 && !hit.ArmorBreak) return; // Guts armor: takes the Josh, keeps its footing, gets no status

        bool held = hit.Effect != WeaponEffect.None && ApplyEffect(state, attacker, target, in hit, damage, rules);
        if (hit.Soft || held) return; // a soft hit, or a hold instead of knockback

        // A dodge cut short by a hit still costs its cooldown (the dodge would have set it on ending).
        if (def.Action == FighterAction.Dodge) def.DodgeCooldown = rules.Tuning.DodgeCooldownFrames;
        def.RootFrames = 0; // a hit with knockback ends any hold
        def.StunWeapon = 0;
        if (hit.Effect == WeaponEffect.Reel) side = -side; // reeled in: launched towards the attacker

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

    /// <summary>
    /// What a hit adds (combat spec v0.4, section 2.4). True when a hold took, which replaces the
    /// hit's knockback. A hold (root, stun, freeze) takes only on a fighter who is on the ground,
    /// not stun-immune and not in Guts armor; on one in the air the hit is an ordinary hit and adds
    /// slow of the same length instead.
    /// </summary>
    private static bool ApplyEffect(GameState state, int attacker, int target, in Hit hit, int damage, MatchRules rules)
    {
        ref Fighter def = ref state.Fighters[target];
        int n = hit.EffectN;
        bool canHold = def.Grounded && def.StunImmune == 0 && def.ArmorFrames == 0;
        switch (hit.Effect)
        {
            case WeaponEffect.Slow:
                AddSlow(ref def, n);
                return false;
            case WeaponEffect.Chill:
                // Chill builds up as slow; enough of it on someone standing freezes them solid.
                AddSlow(ref def, n);
                if (def.SlowFrames < Weapons.FreezeAt || !canHold) return false;
                Stun(ref def, Weapons.FreezeFrames, hit.Weapon, rules);
                def.SlowFrames = 0;
                def.StunImmune = Weapons.FreezeImmuneFrames;
                return true;
            case WeaponEffect.Root:
            case WeaponEffect.Stun:
            case WeaponEffect.Slip:
                if (!canHold)
                {
                    if (!def.Grounded) AddSlow(ref def, n);
                    return false;
                }
                if (hit.Effect == WeaponEffect.Root)
                {
                    def.RootFrames = n;
                    def.StunWeapon = (int)hit.Weapon;
                }
                else if (hit.Effect == WeaponEffect.Slip) Slip(ref def, n, hit.Weapon, rules);
                else Stun(ref def, n, hit.Weapon, rules);
                def.StunImmune = n + Weapons.HoldImmuneFrames;
                return true;
            case WeaponEffect.Disarm:
                Disarm(state, target);
                return false;
            case WeaponEffect.Poison:
            case WeaponEffect.Burn:
                // Never stacks: the longer of the two, credited to the newest.
                def.DotFrames = Math32.Max(def.DotFrames, n);
                def.DotBy = attacker + 1;
                def.DotWeapon = (int)hit.Weapon;
                return false;
            case WeaponEffect.Mark:
                def.MarkFrames = Math32.Max(def.MarkFrames, n);
                return false;
            case WeaponEffect.Leech:
                ref Fighter atk = ref state.Fighters[attacker];
                atk.Josh = Math32.Max(0, atk.Josh - damage);
                return false;
            case WeaponEffect.Swap:
                Swap(state, attacker, target, rules);
                return false;
        }
        return false;
    }

    /// <summary>
    /// The swap (the Swap Ray): the attacker (the shot's owner) and the target change places, both
    /// standing still, if both spots are safe (<see cref="SafeSpot"/>). Otherwise nothing happens
    /// but the damage. The shooter's own mines and bombs are no part of the test: swapping someone
    /// onto them is the trick.
    /// </summary>
    private static void Swap(GameState state, int attacker, int target, MatchRules rules)
    {
        if (attacker == target) return;
        ref Fighter a = ref state.Fighters[attacker];
        ref Fighter b = ref state.Fighters[target];
        if (!a.IsActive || !SafeSpot(state, rules, a.Position) || !SafeSpot(state, rules, b.Position)) return;
        FixVec2 at = a.Position;
        bool grounded = a.Grounded;
        a.Position = b.Position;
        a.Grounded = b.Grounded;
        b.Position = at;
        b.Grounded = grounded;
        a.Velocity = FixVec2.Zero;
        b.Velocity = FixVec2.Zero;
    }

    private static void AddSlow(ref Fighter f, int frames) => f.SlowFrames = Math32.Min(Weapons.SlowCapFrames, f.SlowFrames + frames);

    /// <summary>
    /// A slip: a stun hold, sliding the way the fighter faces at 0.3 m a frame, which ground
    /// friction brings to a stop in about 2.3 m. Off a ledge the hold ends (FreeIfAirborne).
    /// </summary>
    private static void Slip(ref Fighter f, int frames, WeaponId weapon, MatchRules rules)
    {
        Stun(ref f, frames, weapon, rules);
        f.Velocity.X = Weapons.SlipSpeed * f.Facing;
    }

    /// <summary>
    /// The disarm: the target's weapon drops at its feet as a loose weapon with the supply it had,
    /// ignoring the target for 1 s, and its burst and streak counters are cleared. Nothing happens
    /// if it has no weapon, an empty one, or no prop slot is free.
    /// </summary>
    private static void Disarm(GameState state, int target)
    {
        ref Fighter def = ref state.Fighters[target];
        if (def.Weapon == WeaponId.None) return;
        if (def.WeaponSupply <= 0 && WeaponTable.Rows[(int)def.Weapon].Spend != WeaponSpend.Never) return;
        int slot = FreeSlot(state, forShot: false);
        if (slot < 0) return;
        state.Props[slot] = new Prop
        {
            Kind = PropKind.Item,
            Weapon = def.Weapon,
            Supply = def.WeaponSupply,
            Owner = -1,
            Position = def.Position,
            Grounded = def.Grounded,
            IgnoreFighter = target + 1,
            IgnoreTimer = Weapons.NoSwapBackFrames,
        };
        def.Weapon = WeaponId.None;
        def.WeaponSupply = 0;
        def.BurstShots = 0;
        def.Streak = 0;
        def.StreakTarget = 0;
    }

    /// <summary>
    /// A stun hold, as a parried attacker is stunned: move cleared, sideways speed zero, no rising.
    /// The weapon is kept in <see cref="Fighter.StunWeapon"/>, which is how the client draws it.
    /// </summary>
    private static void Stun(ref Fighter f, int frames, WeaponId weapon, MatchRules rules)
    {
        if (f.Action == FighterAction.Dodge) f.DodgeCooldown = rules.Tuning.DodgeCooldownFrames;
        f.Action = FighterAction.Stunned;
        f.Move = MoveId.None;
        f.FlurryNext = MoveId.None;
        f.ActionFrame = 0;
        f.HitstunFrames = frames;
        f.Velocity = new FixVec2(Fix.Zero, Fix.Min(f.Velocity.Y, Fix.Zero));
        f.StunWeapon = (int)weapon;
    }

    private static void CheckBlastZone(GameState state, int index, MatchRules rules)
    {
        Fighter[] fighters = state.Fighters;
        ref Fighter f = ref fighters[index];
        if (!f.IsActive || rules.Stage.BlastZone.Contains(f.Position)) return;

        // The KO goes to whoever hit this fighter last, if that was recent; a lone fall scores none,
        // and so does a fall a teammate set up (whatever path set the credit).
        f.Falls++;
        if (f.LastHitTimer > 0 && f.LastHitBy > 0 && !Allies(rules, f.LastHitBy - 1, index))
        {
            fighters[f.LastHitBy - 1].Kos++;
            int slot = RecordSlot(state, f.LastHitBy - 1, f.LastHitWeapon);
            if (slot >= 0) state.WeaponStats[slot + 2]++; // the weapon's KO, if the record still holds it
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
        f.ClearStatuses();
        f.Action = timed || f.Stocks > 0 ? FighterAction.Respawning : FighterAction.Eliminated;
    }

    private static void CheckMatchOver(GameState state, MatchRules rules)
    {
        if (rules.TimeLimitFrames > 0)
        {
            if (state.Frame + 1 < rules.TimeLimitFrames) return;
            state.MatchOver = true;
            state.WinnerIndex = rules.HasTeams() ? TimedTeamWinner(state.Fighters, rules) : TimedWinner(state.Fighters);
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

    /// <summary>Where the slot holding this weapon starts in <see cref="GameState.WeaponStats"/>, or -1.</summary>
    private static int RecordSlot(GameState state, int fighter, WeaponId weapon)
    {
        int from = fighter * Weapons.RecordInts;
        for (int k = 0; k < Weapons.RecordSlots; k++)
            if (state.WeaponStats[from + k * Weapons.RecordSlotInts] == (int)weapon + 1) return from + k * Weapons.RecordSlotInts;
        return -1;
    }

    /// <summary>
    /// A hit on someone else goes into the attacker's record: the total, and the slot of the weapon
    /// it is credited to (None: fists). A weapon with no slot takes the first empty one; with none
    /// empty it takes over the slot with the least damage (the lowest on a tie), cleared.
    /// </summary>
    private static void RecordHit(GameState state, int fighter, WeaponId weapon, int damage, bool sweet)
    {
        int[] record = state.WeaponStats;
        int from = fighter * Weapons.RecordInts;
        record[from + Weapons.RecordSlots * Weapons.RecordSlotInts] += damage;
        int slot = RecordSlot(state, fighter, weapon);
        if (slot < 0)
        {
            slot = from;
            for (int k = 0; k < Weapons.RecordSlots; k++)
            {
                int at = from + k * Weapons.RecordSlotInts;
                if (record[at] == 0)
                {
                    slot = at;
                    break;
                }
                if (record[at + 1] < record[slot + 1]) slot = at;
            }
            record[slot] = (int)weapon + 1;
            record[slot + 1] = 0;
            record[slot + 2] = 0;
            record[slot + 3] = 0;
        }
        record[slot + 1] += damage;
        if (sweet) record[slot + 3]++;
    }

    /// <summary>
    /// A fighter's record with a weapon (None: fists): <paramref name="stat"/> 0 is the damage dealt
    /// to others, 1 the KOs, 2 the sweet-spot hits. 0 for a weapon the record holds no slot for.
    /// </summary>
    public static int WeaponStat(GameState state, int fighter, WeaponId weapon, int stat)
    {
        int slot = RecordSlot(state, fighter, weapon);
        return slot < 0 ? 0 : state.WeaponStats[slot + 1 + stat];
    }

    /// <summary>All the damage a fighter has done to others, with anything.</summary>
    public static int DamageDealt(GameState state, int fighter) =>
        state.WeaponStats[fighter * Weapons.RecordInts + Weapons.RecordSlots * Weapons.RecordSlotInts];

    /// <summary>
    /// A fighter's main weapon in this match, for their nickname: the weapon (0 is fists) that did
    /// the most of their damage (the lower id on a tie), if that is at least 40% of all the damage
    /// they did; otherwise -1, an all-rounder. <paramref name="master"/>: they also landed 3 or
    /// more sweet spots and a KO with it.
    /// </summary>
    public static int MainWeapon(GameState state, int fighter, out bool master)
    {
        master = false;
        int[] record = state.WeaponStats;
        int from = fighter * Weapons.RecordInts;
        int best = -1, bestWeapon = -1, bestDamage = 0;
        for (int k = 0; k < Weapons.RecordSlots; k++)
        {
            int at = from + k * Weapons.RecordSlotInts;
            if (record[at] == 0) continue;
            int weapon = record[at] - 1, damage = record[at + 1];
            if (damage > bestDamage || (damage == bestDamage && damage > 0 && weapon < bestWeapon))
            {
                best = at;
                bestWeapon = weapon;
                bestDamage = damage;
            }
        }
        if (best < 0 || bestDamage * 100 < DamageDealt(state, fighter) * 40) return -1;
        master = record[best + 3] >= 3 && record[best + 2] >= 1;
        return bestWeapon;
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

    /// <summary>
    /// A timed team match's winner (rooms v1, section 1.7): the team with the most KOs, then the
    /// fewest falls, summed over its members (a team-0 fighter is a team of one); -1 if the best
    /// two teams are level on both. Otherwise that team's MVP: the most KOs, then the fewest
    /// falls, then the lowest index.
    /// </summary>
    public static int TimedTeamWinner(Fighter[] fighters, MatchRules rules)
    {
        int n = fighters.Length;
        Span<int> kos = stackalloc int[n];
        Span<int> falls = stackalloc int[n];
        Span<int> leader = stackalloc int[n];
        for (int i = 0; i < n; i++)
        {
            int j = 0;
            while (!Allies(rules, i, j)) j++; // the lowest index on i's team (i itself at worst)
            leader[i] = j;
            kos[j] += fighters[i].Kos;
            falls[j] += fighters[i].Falls;
        }

        int best = -1;
        bool tied = false;
        for (int i = 0; i < n; i++)
        {
            if (leader[i] != i) continue;
            if (best < 0) { best = i; continue; }
            if (kos[i] > kos[best] || (kos[i] == kos[best] && falls[i] < falls[best]))
            {
                best = i;
                tied = false;
            }
            else if (kos[i] == kos[best] && falls[i] == falls[best])
            {
                tied = true;
            }
        }
        if (best < 0 || tied) return -1;

        int mvp = -1;
        for (int i = 0; i < n; i++)
        {
            if (leader[i] != best) continue;
            if (mvp < 0 || fighters[i].Kos > fighters[mvp].Kos || (fighters[i].Kos == fighters[mvp].Kos && fighters[i].Falls < fighters[mvp].Falls))
                mvp = i;
        }
        return mvp;
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
        f.BurstShots = 0;
        f.Streak = 0;
        f.StreakTarget = 0;
        f.FlurryNext = MoveId.None;
        f.ClearStatuses();
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
