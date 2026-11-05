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
}

/// <summary>
/// The deterministic step function: (state, inputs) -> next state, at a fixed 60 Hz.
/// Graybox scope today: run, jump + air jump, one-way platforms, a placeholder jab,
/// Josh % knockback and ring-out. Dodge, heavies, grabs, weapons and Taali come next.
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
            for (int i = 0; i < fighters.Length; i++) UpdateFighter(ref fighters[i], i, inputs[i], rules);
            ResolveHits(fighters, rules);
            for (int i = 0; i < fighters.Length; i++) CheckBlastZone(ref fighters[i], rules);
            CheckMatchOver(state);
        }

        state.Frame++;
    }

    private static void UpdateFighter(ref Fighter f, int index, Buttons input, MatchRules rules)
    {
        FighterTuning t = rules.Tuning;
        Buttons pressed = input & ~f.PreviousButtons;
        f.PreviousButtons = input;

        switch (f.Action)
        {
            case FighterAction.Eliminated:
                return;

            case FighterAction.Respawning:
                f.ActionFrame++;
                if (f.ActionFrame >= rules.RespawnFrames) Spawn(ref f, index, rules);
                return;

            case FighterAction.Hitstun:
                f.HitstunFrames--;
                if (f.HitstunFrames <= 0) f.Action = FighterAction.Idle;
                if (f.Grounded) f.Velocity.X = Fix.MoveToward(f.Velocity.X, Fix.Zero, t.GroundAccel);
                break;

            case FighterAction.LightAttack:
                f.ActionFrame++;
                if (f.ActionFrame >= t.LightTotalFrames) f.Action = FighterAction.Idle;
                if (f.Grounded) f.Velocity.X = Fix.MoveToward(f.Velocity.X, Fix.Zero, t.GroundAccel);
                break;

            case FighterAction.Idle:
                HandleControl(ref f, input, pressed, t);
                break;
        }

        ApplyPhysics(ref f, rules);
    }

    private static void HandleControl(ref Fighter f, Buttons input, Buttons pressed, FighterTuning t)
    {
        int dir = ((input & Buttons.Right) != 0 ? 1 : 0) - ((input & Buttons.Left) != 0 ? 1 : 0);
        if (dir != 0) f.Facing = dir;

        Fix targetVx = dir * (f.Grounded ? t.RunSpeed : t.AirSpeed);
        f.Velocity.X = Fix.MoveToward(f.Velocity.X, targetVx, f.Grounded ? t.GroundAccel : t.AirAccel);

        if ((pressed & Buttons.Jump) != 0)
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

        if ((pressed & Buttons.Light) != 0)
        {
            f.Action = FighterAction.LightAttack;
            f.ActionFrame = 0;
            f.AttackConnected = false;
        }
    }

    private static void ApplyPhysics(ref Fighter f, MatchRules rules)
    {
        FighterTuning t = rules.Tuning;
        if (!f.Grounded) f.Velocity.Y = Fix.Max(f.Velocity.Y - t.Gravity, -t.MaxFallSpeed);

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
        }
        else
        {
            f.Grounded = false;
        }

        f.Position = next;
    }

    private static void ResolveHits(Fighter[] fighters, MatchRules rules)
    {
        FighterTuning t = rules.Tuning;
        int n = fighters.Length;

        // Collect first, apply after, so simultaneous hits trade fairly regardless of player index.
        Span<int> hitAttacker = stackalloc int[n * n];
        Span<int> hitTarget = stackalloc int[n * n];
        int hitCount = 0;

        for (int a = 0; a < n; a++)
        {
            ref Fighter atk = ref fighters[a];
            if (atk.Action != FighterAction.LightAttack || atk.AttackConnected) continue;
            if (atk.ActionFrame < t.LightStartup || atk.ActionFrame >= t.LightStartup + t.LightActive) continue;

            FixRect hitbox = JabHitbox(in atk, t);
            for (int d = 0; d < n; d++)
            {
                if (d == a || !fighters[d].IsActive) continue;
                if (!hitbox.Overlaps(Hurtbox(in fighters[d], t))) continue;
                hitAttacker[hitCount] = a;
                hitTarget[hitCount] = d;
                hitCount++;
            }
        }

        for (int i = 0; i < hitCount; i++)
        {
            ref Fighter atk = ref fighters[hitAttacker[i]];
            ref Fighter def = ref fighters[hitTarget[i]];
            atk.AttackConnected = true;

            def.Josh = Math32.Min(rules.MaxJosh, def.Josh + t.LightDamage);
            Fix kb = t.LightBaseKnockback + t.LightKnockbackPerJosh * def.Josh;
            def.Velocity = new FixVec2(kb * Diagonal * atk.Facing, kb * Diagonal);
            def.Grounded = false;
            def.Action = FighterAction.Hitstun;
            def.ActionFrame = 0;
            def.HitstunFrames = 8 + def.Josh / 8;
        }
    }

    public static FixRect Hurtbox(in Fighter f, FighterTuning t) =>
        new(f.Position.X - t.HalfWidth, f.Position.Y, f.Position.X + t.HalfWidth, f.Position.Y + t.Height);

    public static FixRect JabHitbox(in Fighter f, FighterTuning t)
    {
        Fix near = f.Position.X + Fix.FromRatio(1, 5) * f.Facing;
        Fix far = f.Position.X + Fix.FromRatio(11, 10) * f.Facing;
        return new FixRect(near, f.Position.Y + Fix.FromRatio(3, 5), far, f.Position.Y + Fix.FromRatio(7, 5));
    }

    private static void CheckBlastZone(ref Fighter f, MatchRules rules)
    {
        if (!f.IsActive || rules.Stage.BlastZone.Contains(f.Position)) return;

        f.Stocks--;
        f.Velocity = FixVec2.Zero;
        f.Josh = 0;
        f.ActionFrame = 0;
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
        f.ActionFrame = 0;
        f.HitstunFrames = 0;
        f.AttackConnected = false;
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
