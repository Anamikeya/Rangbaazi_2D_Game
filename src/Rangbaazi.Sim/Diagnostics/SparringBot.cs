using System;
using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim.Diagnostics;

/// <summary>How quickly a <see cref="SparringBot"/> attacks, and how well it dodges.</summary>
public enum CpuLevel : byte
{
    /// <summary>
    /// Attacks on about 1 frame in 16 while in reach (~0.25 s on average, often less). Needs 10
    /// frames to react: enough for the neutral, side and down heavies and for a ground slam whose
    /// slammer starts at least ~0.8 m above its head, not for the up heavy or the jab. Dodges 1 in 4
    /// of those.
    /// </summary>
    Easy,

    /// <summary>
    /// Attacks on about 1 frame in 6 while in reach (~0.1 s on average). Reacts in 6 frames: every
    /// heavy and slam except the up heavy, not the jab. Dodges 1 in 3 of those.
    /// </summary>
    Normal,

    /// <summary>
    /// Attacks on about 1 frame in 2 while in reach: almost at once. Reacts in 3 frames, so it can
    /// parry anything, even the jab, and dodges 1 attack in 2. The smoke-test bot.
    /// </summary>
    Hard,
}

/// <summary>
/// A simple CPU opponent for smoke tests, soak tests, demos and the graybox's CPU players.
/// Not the real AI.
///
/// It is a pure function of (state, player, level): it reads <see cref="GameState"/> and returns
/// <see cref="Buttons"/>, and it never writes state or draws from the match RNG. Running it on
/// every peer therefore gives identical inputs, so it is safe to use inside rollback tests.
///
/// Behaviour: walk to the nearest opponent, turn to face them, attack when in reach, jump after
/// them if they are on a platform above, walk off a floating platform if they are below, and
/// recover when off the stage (air jump, then the up heavy, then an air dodge). Attacks are the
/// jab, a neutral heavy to punish an opponent it has parried, and a side heavy (or a neutral one
/// near the edge) to finish an opponent at high Josh. It spot-dodges attacks that are about to hit
/// it, timed to parry.
///
/// It never walks past the edges of the main stage after an opponent who is off the side (it
/// waits at the edge instead), never jabs in the air when the drift would carry it off the stage,
/// and only uses heavies standing on the ground. Per-frame and per-attack hashes (not the match
/// RNG) decide when it attacks and which attacks it dodges, so mirrored bots don't trade forever;
/// <see cref="CpuLevel"/> sets those odds and its reaction time. Walking, chasing and recovery are
/// the same at every level.
/// </summary>
public static class SparringBot
{
    /// <summary>Stop walking at this distance; the jab hits out to ~1.5 m between centres.</summary>
    private static readonly Fix JabRange = Fix.FromRatio(6, 5);
    private static readonly Fix ChaseHeight = Fix.FromRatio(3, 2);
    private static readonly Fix ChaseDistance = Fix.FromInt(4);

    /// <summary>
    /// When chasing, aim no closer than this to either end of the main stage. Leaves room to stop
    /// (running stops in ~0.4 m, drifting in the air in ~0.6 m) and still reaches an opponent at
    /// the very edge with the jab, because EdgeMargin + StopDistance is less than JabRange: the
    /// bot can come to rest at most that far from the edge.
    /// </summary>
    private static readonly Fix EdgeMargin = Fix.One;

    /// <summary>Close enough to the chase point to stop pressing a direction.</summary>
    private static readonly Fix StopDistance = Fix.FromRatio(1, 8);

    /// <summary>An air jab must leave the bot at least this far inside the stage edge.</summary>
    private static readonly Fix LandingMargin = Fix.Half;

    /// <summary>
    /// The side heavy lunges about 0.7 m and friction then stops it. Only use it with at least this
    /// much stage ahead; nearer the edge, use the neutral heavy.
    /// </summary>
    private static readonly Fix LungeRoom = Fix.FromRatio(3, 2);

    /// <summary>
    /// Start throwing heavies as reads once the opponent's Josh is this high. The side heavy KOs a
    /// recovering opponent from 1 m inside the edge at ~61% (<see cref="KoChart"/>).
    /// </summary>
    public const int FinisherJosh = 70;

    /// <summary>At <see cref="FinisherJosh"/> or more, 1 attack in this many is a heavy.</summary>
    public const int FinisherOdds = 3;

    /// <summary>
    /// How much bigger than an attack's hitbox to treat the danger zone, because both fighters move
    /// during the startup. Lunging moves get another metre.
    /// </summary>
    private static readonly Fix ThreatPadding = Fix.Half;

    /// <summary>Press dodge this many frames before an attack's first active frame: inside the parry window.</summary>
    private const int ParryLead = 3;

    /// <summary>
    /// The bot's buttons for this frame. <paramref name="level"/> defaults to
    /// <see cref="CpuLevel.Hard"/>, which is what <see cref="SmokeScript"/> plays.
    /// </summary>
    public static Buttons Think(GameState state, MatchRules rules, int self, CpuLevel level = CpuLevel.Hard)
    {
        Skill skill = SkillFor(level); // validate first, so a bad level fails even when it can't act

        Fighter[] fighters = state.Fighters;
        ref readonly Fighter me = ref fighters[self];
        FixRect stage = rules.Stage.Platforms[0];
        FighterTuning t = rules.Tuning;

        if (me.HitStop > 0) return Buttons.None; // frozen: nothing it presses would do anything yet
        if (me.Action == FighterAction.Attack) return SteerDuringMove(in me, stage, t);
        if (me.Action != FighterAction.Idle) return Buttons.None; // dodging, in hitstun, stunned or out

        Buttons last = me.PreviousButtons; // jump, attacks and dodge only fire on a fresh press
        bool canJump = (last & Buttons.Jump) == 0;
        bool canJab = (last & Buttons.Light) == 0;
        bool canHeavy = (last & Buttons.Heavy) == 0;
        bool canDodge = (last & Buttons.Dodge) == 0 && me.DodgeCooldown == 0 && (me.Grounded || !me.AirDodgeUsed);

        // Recovery: off the sides of the main stage, or below its top surface.
        bool offStage = me.Position.X < stage.MinX || me.Position.X > stage.MaxX
            || (!me.Grounded && me.Position.Y < stage.MaxY);
        if (offStage) return Recover(in me, stage, canJump, canHeavy, canDodge);

        int target = NearestOpponent(fighters, self);
        if (target < 0) return Buttons.None;
        ref readonly Fighter op = ref fighters[target];

        // Defence first: a spot dodge doesn't move, so it can't take the bot off the stage.
        if (me.Grounded && canDodge && ShouldDodge(state, rules, self, skill)) return Buttons.Dodge;

        Fix dx = op.Position.X - me.Position.X;
        Fix dy = op.Position.Y - me.Position.Y;
        int dir = dx >= Fix.Zero ? 1 : -1;
        Buttons toward = dir > 0 ? Buttons.Right : Buttons.Left;

        Buttons b = Buttons.None;
        int floor = me.Grounded ? StandingOn(in me, rules.Stage) : -1;
        if (floor > 0 && dy < -Fix.One)
        {
            // On a floating platform with them below. There is no drop-through, so walk off the end
            // on their side. Waiting would freeze the bot (it can't jab that far down and only
            // chases upwards), giving a person a safe spot under the platform.
            FixRect p = rules.Stage.Platforms[floor];
            b |= op.Position.X * 2 < p.MinX + p.MaxX ? Buttons.Left : Buttons.Right;
        }
        else if (Fix.Abs(dx) <= JabRange && SafeToAttack(in me, stage, t))
        {
            if (me.Facing != dir) b |= toward; // turn to face them
            else if (Fix.Abs(dy) < Fix.One && !Hesitates(state.Frame, self, skill.AttackOdds))
            {
                // Lunge room is measured on whatever it stands on: a floating platform's end is an edge too.
                FixRect ground = floor >= 0 ? rules.Stage.Platforms[floor] : stage;
                b |= Attack(in me, in op, ground, t, state.Frame, self, toward, canJab, canHeavy);
            }
        }
        else
        {
            // Walk after them, but only as far as the edge margin: following an opponent who is
            // off the side is how the bot used to walk and drift off the stage itself.
            Fix goal = Fix.Clamp(op.Position.X, stage.MinX + EdgeMargin, stage.MaxX - EdgeMargin);
            Fix toGoal = goal - me.Position.X;
            if (toGoal > StopDistance) b |= Buttons.Right;
            else if (toGoal < -StopDistance) b |= Buttons.Left;
        }

        // Jump after them only when they stand on a platform above. Jumping at someone who is just
        // in the air above makes two bots take turns jumping at each other, with every jab whiffing
        // because the height gap changes faster than the jab comes out.
        if (me.Grounded && canJump && op.Grounded && dy > ChaseHeight && Fix.Abs(dx) < ChaseDistance) b |= Buttons.Jump;
        return b;
    }

    /// <summary>
    /// Jab by default. Heavies only standing on the ground, when they can't be interrupted, or as a
    /// read once the opponent is at <see cref="FinisherJosh"/>:
    /// <list type="bullet">
    /// <item>a neutral heavy into an opponent the bot has parried, while enough stun is left for it
    /// to land (otherwise a jab);</item>
    /// <item>a heavy into the recovery of an opponent's move that has more frames left than the
    /// heavy's startup (a whiffed heavy, for example);</item>
    /// <item>at high Josh, a heavy on 1 attack in <see cref="FinisherOdds"/>. Against an opponent
    /// who is free to act, the heavy's slow startup can be jabbed or dodged.</item>
    /// </list>
    /// The side heavy when there is room to lunge on <paramref name="ground"/> (the platform the bot
    /// stands on), the neutral heavy otherwise.
    /// </summary>
    private static Buttons Attack(in Fighter me, in Fighter op, FixRect ground, FighterTuning t, int frame, int self,
        Buttons toward, bool canJab, bool canHeavy)
    {
        if (me.Grounded && canHeavy)
        {
            if (op.Action == FighterAction.Stunned)
            {
                if (op.HitStop + op.HitstunFrames > t.Moves.NeutralHeavy.Startup) return Buttons.Heavy;
                return canJab ? Buttons.Light : Buttons.None; // too little stun left for the heavy to land
            }

            Fix lungeEnd = me.Position.X + LungeRoom * me.Facing;
            bool room = lungeEnd > ground.MinX && lungeEnd < ground.MaxX;

            // Punish a move with no active frame left (the state shows the frame just played, so
            // its last active frame counts as spent), with whichever heavy lands before the
            // opponent can act again: the side heavy if it fits and there's room, else the neutral one.
            MoveData? theirs = Simulation.CurrentMove(in op, t);
            if (theirs != null && op.ActionFrame + 1 >= theirs.Startup + theirs.Active)
            {
                int left = op.HitStop + theirs.TotalFrames - op.ActionFrame;
                if (room && left > t.Moves.SideHeavy.Startup) return Buttons.Heavy | toward;
                if (left > t.Moves.NeutralHeavy.Startup) return Buttons.Heavy;
            }
            if (op.Josh >= FinisherJosh && PicksHeavy(frame, self)) return room ? Buttons.Heavy | toward : Buttons.Heavy;
        }
        return canJab ? Buttons.Light : Buttons.None;
    }

    /// <summary>
    /// Off the stage: steer home, air-jump, then up heavy, then air-dodge up and home. Each is saved
    /// until falling below the stage top: using one while still above it wastes it (a falling
    /// fighter gains the most), and a bot that drifted out above the stage would have nothing left
    /// by the time it needs it.
    /// </summary>
    private static Buttons Recover(in Fighter me, FixRect stage, bool canJump, bool canHeavy, bool canDodge)
    {
        Buttons home = me.Position.X < Fix.Zero ? Buttons.Right : Buttons.Left;
        bool falling = me.Velocity.Y < Fix.Zero && me.Position.Y < stage.MaxY;
        if (!falling) return home;
        if (me.AirJumpsLeft > 0) return canJump ? home | Buttons.Jump : home;
        if (!me.RecoveryUsed) return canHeavy ? home | Buttons.Up | Buttons.Heavy : home;
        return canDodge ? home | Buttons.Up | Buttons.Dodge : home;
    }

    /// <summary>During a move the bot can steer (the up heavy), steer home if it is off the side.</summary>
    private static Buttons SteerDuringMove(in Fighter me, FixRect stage, FighterTuning t)
    {
        MoveData? m = Simulation.CurrentMove(in me, t);
        if (m == null || !m.AirControl || me.Grounded) return Buttons.None;
        if (me.Position.X > stage.MaxX - EdgeMargin) return Buttons.Left;
        if (me.Position.X < stage.MinX + EdgeMargin) return Buttons.Right;
        return Buttons.None;
    }

    /// <summary>
    /// True when an opponent's attack is about to hit the bot, the bot can react to it in time to
    /// parry, and the per-attack roll says this is one it dodges. A dodge pressed while the attacker
    /// is on frame <c>a</c> starts this frame, as the attacker reaches frame <c>a + 1</c>; it is
    /// useful only if <c>a + 1</c> is at most the frame the hitbox first reaches the bot, and parries
    /// if that frame falls in its parry window. For most moves that is the first active frame; a
    /// ground slam reaches the bot only after plunging down to it. An attacker frozen in hit-stop
    /// is skipped until it moves again, since its timing is on hold.
    /// </summary>
    private static bool ShouldDodge(GameState state, MatchRules rules, int self, Skill skill)
    {
        Fighter[] fighters = state.Fighters;
        ref readonly Fighter me = ref fighters[self];
        FixRect hurtbox = Simulation.Hurtbox(in me, rules.Tuning);

        for (int i = 0; i < fighters.Length; i++)
        {
            if (i == self) continue;
            ref readonly Fighter op = ref fighters[i];
            MoveData? m = Simulation.CurrentMove(in op, rules.Tuning);
            if (m == null || op.AttackConnected || op.HitStop > 0) continue;

            int a = op.ActionFrame;
            int arrives = m.Startup;
            if (m.PlungeSpeed != Fix.Zero && !SlamArrival(in op, m, a, hurtbox, rules.Stage, out arrives)) continue;

            // Is the bot in the way? It decides once, so judge where it could be by the time the
            // attack arrives (moving on as it is, then braking), not only where it stands now:
            // a bot walking in would otherwise step into an attack it had judged out of reach.
            FixRect box = Simulation.MoveHitbox(in op, m);
            Fix pad = m.LungeSpeed != Fix.Zero ? ThreatPadding + Fix.One : ThreatPadding;
            Fix bottom = m.PlungeSpeed != Fix.Zero ? hurtbox.MinY : box.MinY - pad; // a slam's height is SlamArrival's job
            var danger = new FixRect(box.MinX - pad, bottom, box.MaxX + pad, Fix.Max(box.MaxY + pad, hurtbox.MaxY));
            if (!danger.Overlaps(Reach(in me, hurtbox, arrives - a, rules.Tuning))) continue;

            // One chance per attack, on the frame it would press to parry. If it can't act then
            // (or the roll says no), it doesn't get another try later in the same attack.
            int pressAt = Math32.Max(skill.Reaction, arrives - ParryLead);
            if (a != pressAt || pressAt >= arrives) continue;

            if (DodgesAttack(state.Frame - a, i, self, skill.DodgeOdds)) return true;
        }
        return false;
    }

    /// <summary>
    /// The hurtbox stretched sideways over where the fighter could be in <paramref name="frames"/>
    /// frames: carrying on at its current speed, then braking to a stop.
    /// </summary>
    private static FixRect Reach(in Fighter me, FixRect hurtbox, int frames, FighterTuning t)
    {
        Fix vx = me.Velocity.X;
        if (vx == Fix.Zero || frames <= 0) return hurtbox;
        Fix brake = me.Grounded ? t.GroundAccel : t.AirAccel;
        Fix travel = Fix.Abs(vx) * frames + vx * vx / (brake * 2);
        return vx > Fix.Zero
            ? new FixRect(hurtbox.MinX, hurtbox.MinY, hurtbox.MaxX + travel, hurtbox.MaxY)
            : new FixRect(hurtbox.MinX - travel, hurtbox.MinY, hurtbox.MaxX, hurtbox.MaxY);
    }

    /// <summary>
    /// When a ground slam on attack frame <paramref name="a"/> will first come down to the height of
    /// <paramref name="hurtbox"/>, as an attack frame. False if it never will: it is already below
    /// the bot, it lands on a platform above the bot first, or its active frames run out before it
    /// gets there. It hangs at its height until its startup ends, then drops PlungeSpeed every
    /// frame. Whether it lines up with the bot sideways is checked by the caller.
    /// </summary>
    private static bool SlamArrival(in Fighter op, MoveData m, int a, FixRect hurtbox, StageDefinition stage, out int arrives)
    {
        arrives = 0;

        // It lands on the highest platform top under it, as Simulation's one-way platforms do.
        Fix land = stage.BlastZone.MinY;
        for (int p = 0; p < stage.Platforms.Count; p++)
        {
            FixRect plat = stage.Platforms[p];
            if (op.Position.X >= plat.MinX && op.Position.X <= plat.MaxX && plat.MaxY <= op.Position.Y && plat.MaxY > land)
                land = plat.MaxY;
        }
        if (land + m.HitboxBottom >= hurtbox.MaxY) return false; // stops above the bot's head

        // It only moves down, and its hitbox is out from its next plunge frame (or its landing
        // frame) on: if the hitbox's top is already no higher than the bot's feet by then, it can't touch.
        if (Fix.Max(op.Position.Y - m.PlungeSpeed, land) + m.HitboxTop <= hurtbox.MinY) return false;

        // Frame `start` is the next one on which it drops; after j more frames it overlaps once
        // (y - PlungeSpeed * (j + 1)) + HitboxBottom is below the hurtbox top.
        int start = Math32.Max(a + 1, m.Startup);
        Fix gap = op.Position.Y + m.HitboxBottom - hurtbox.MaxY;
        arrives = gap < Fix.Zero ? start : start + (gap / m.PlungeSpeed).FloorToInt();
        return arrives < m.Startup + m.Active;
    }

    /// <summary>
    /// A jab locks out steering until it ends, and in the air the fighter keeps drifting. On the
    /// ground friction stops it; in the air, only attack if the drift (through the whole attack,
    /// then braking by steering) still ends over the main stage, or the bot can jab itself off the
    /// side and below the reach of its air jump. Above the stage top with x over the stage at both
    /// ends of the drift, it lands on the stage whenever it comes down.
    /// </summary>
    private static bool SafeToAttack(in Fighter me, FixRect stage, FighterTuning t)
    {
        if (me.Grounded) return true;
        Fix vx = me.Velocity.X;
        Fix brake = vx * Fix.Abs(vx) / (t.AirAccel * 2);
        Fix endX = me.Position.X + vx * t.Moves.Jab.TotalFrames + brake;
        return endX >= stage.MinX + LandingMargin && endX <= stage.MaxX - LandingMargin;
    }

    /// <summary>Index of the platform the fighter is standing on, or -1. Platform 0 is the main stage.</summary>
    private static int StandingOn(in Fighter me, StageDefinition stage)
    {
        var platforms = stage.Platforms;
        for (int i = 0; i < platforms.Count; i++)
        {
            FixRect p = platforms[i];
            if (me.Position.Y == p.MaxY && me.Position.X >= p.MinX && me.Position.X <= p.MaxX) return i;
        }
        return -1;
    }

    /// <summary>
    /// What a level changes. The bot attacks on about 1 eligible frame in AttackOdds, needs
    /// Reaction frames to see an attack coming, and dodges 1 attack in DodgeOdds.
    /// </summary>
    internal readonly struct Skill
    {
        public readonly int AttackOdds;
        public readonly int Reaction;
        public readonly int DodgeOdds;

        public Skill(int attackOdds, int reaction, int dodgeOdds)
        {
            AttackOdds = attackOdds;
            Reaction = reaction;
            DodgeOdds = dodgeOdds;
        }
    }

    internal static Skill SkillFor(CpuLevel level) => level switch
    {
        CpuLevel.Easy => new Skill(attackOdds: 16, reaction: 10, dodgeOdds: 4),
        CpuLevel.Normal => new Skill(attackOdds: 6, reaction: 6, dodgeOdds: 3),
        CpuLevel.Hard => new Skill(attackOdds: 2, reaction: 3, dodgeOdds: 2),
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown CPU level."),
    };

    /// <summary>The bot attacks on about 1 eligible frame in this many.</summary>
    internal static int JabOdds(CpuLevel level) => SkillFor(level).AttackOdds;

    private static int NearestOpponent(Fighter[] fighters, int self)
    {
        FixVec2 at = fighters[self].Position;
        int best = -1;
        Fix bestDist = Fix.Zero;
        for (int i = 0; i < fighters.Length; i++)
        {
            if (i == self || !fighters[i].IsActive) continue;
            Fix d = (fighters[i].Position - at).LengthSquared;
            if (best < 0 || d < bestDist)
            {
                best = i;
                bestDist = d;
            }
        }
        return best;
    }

    /// <summary>Holds back from attacking on (odds - 1) frames in odds, differently per player.</summary>
    private static bool Hesitates(int frame, int self, int odds)
    {
        var hash = new SimRandom(((ulong)(uint)frame << 8) ^ (ulong)(uint)self ^ 0xA5A5_5A5A_C3C3_3C3CUL);
        return hash.Chance(odds - 1, odds);
    }

    /// <summary>Whether this frame's attack is a heavy read: 1 in <see cref="FinisherOdds"/>, differently per player.</summary>
    private static bool PicksHeavy(int frame, int self)
    {
        var hash = new SimRandom(((ulong)(uint)frame << 8) ^ (ulong)(uint)self ^ 0x9E37_79B9_7F4A_7C15UL);
        return hash.Chance(1, FinisherOdds);
    }

    /// <summary>
    /// Dodges 1 attack in <paramref name="odds"/>. Rolled once per attack (keyed by the frame it
    /// started and who threw it), so waiting a frame doesn't give the bot another try.
    /// </summary>
    internal static bool DodgesAttack(int attackStart, int attacker, int self, int odds)
    {
        var hash = new SimRandom(((ulong)(uint)attackStart << 16) ^ ((ulong)(uint)attacker << 8) ^ (ulong)(uint)self
            ^ 0x3C6E_F372_FE94_F82BUL);
        return hash.Chance(1, odds);
    }
}
