using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim.Tests;

/// <summary>
/// Combat spec v0.1 (docs/design/combat-spec-v0.1.md): heavies, the ground slam, dodge, parry,
/// hit-stop and Guts. Most tests use a very wide floor so knockback never rings anyone out.
/// </summary>
public class CombatTests
{
    private static readonly StageDefinition Wide = TestStages.Wide;

    private static readonly MatchRules Rules = new() { Stage = Wide };
    private static readonly FighterTuning T = Rules.Tuning;
    private static readonly MoveSet Moves = T.Moves;

    /// <summary>Fighters standing on the floor at these x positions (metres x 100), P1 facing right, the rest left.</summary>
    private static GameState Standing(MatchRules rules, params int[] xCm)
    {
        var s = Simulation.CreateInitialState(rules, xCm.Length, 1);
        for (int i = 0; i < xCm.Length; i++)
        {
            ref Fighter f = ref s.Fighters[i];
            f.Position = new FixVec2(Fix.FromRatio(xCm[i], 100), Fix.Zero);
            f.Velocity = FixVec2.Zero;
            f.Grounded = true;
            f.Facing = i == 0 ? 1 : -1;
        }
        return s;
    }

    private static GameState Standing(params int[] xCm) => Standing(Rules, xCm);

    private static void Step(GameState s, params Buttons[] inputs)
    {
        var all = new Buttons[s.Fighters.Length];
        inputs.CopyTo(all, 0);
        Simulation.Advance(s, Rules, all);
    }

    private static void Wait(GameState s, int frames)
    {
        for (int i = 0; i < frames; i++) Step(s);
    }

    private static void Airborne(ref Fighter f, int xCm, int yCm)
    {
        f.Position = new FixVec2(Fix.FromRatio(xCm, 100), Fix.FromRatio(yCm, 100));
        f.Velocity = FixVec2.Zero;
        f.Grounded = false;
    }

    // ---- Moves -------------------------------------------------------------------------------

    public static TheoryData<Buttons, bool, MoveId> HeavyInputs => new()
    {
        { Buttons.None, false, MoveId.NeutralHeavy },
        { Buttons.Right, false, MoveId.SideHeavy },
        { Buttons.Left, false, MoveId.SideHeavy },
        { Buttons.Up, false, MoveId.UpHeavy },
        { Buttons.Up | Buttons.Right, false, MoveId.UpHeavy },
        { Buttons.Down, false, MoveId.DownHeavy },
        { Buttons.None, true, MoveId.NeutralHeavy },
        { Buttons.Right, true, MoveId.SideHeavy },
        { Buttons.Up, true, MoveId.UpHeavy },
        { Buttons.Down, true, MoveId.GroundSlam },
        { Buttons.Down | Buttons.Left, true, MoveId.GroundSlam },
    };

    [Theory, MemberData(nameof(HeavyInputs))]
    public void HeavyDirectionPicksTheMove(Buttons direction, bool inAir, MoveId expected)
    {
        GameState s = Standing(0);
        if (inAir) Airborne(ref s.Fighters[0], 0, 1000);
        Step(s, direction | Buttons.Heavy);

        Assert.Equal(FighterAction.Attack, s.Fighters[0].Action);
        Assert.Equal(expected, s.Fighters[0].Move);
        if ((direction & Buttons.Left) != 0) Assert.Equal(-1, s.Fighters[0].Facing);
    }

    [Fact]
    public void LightIsTheJabWhateverTheDirection()
    {
        foreach (Buttons direction in new[] { Buttons.None, Buttons.Up, Buttons.Down, Buttons.Right })
        {
            GameState s = Standing(0);
            Step(s, direction | Buttons.Light);
            Assert.Equal(MoveId.Jab, s.Fighters[0].Move);
        }
    }

    [Fact]
    public void OneActionPerPressFrameDodgeThenHeavyThenLight()
    {
        GameState s = Standing(0);
        Step(s, Buttons.Dodge | Buttons.Heavy | Buttons.Light);
        Assert.Equal(FighterAction.Dodge, s.Fighters[0].Action);

        s = Standing(0);
        Step(s, Buttons.Heavy | Buttons.Light);
        Assert.Equal(MoveId.NeutralHeavy, s.Fighters[0].Move);
    }

    public static TheoryData<MoveId, Buttons, bool> AllMoves => new()
    {
        { MoveId.Jab, Buttons.Light, false },
        { MoveId.NeutralHeavy, Buttons.Heavy, false },
        { MoveId.SideHeavy, Buttons.Heavy | Buttons.Right, false },
        { MoveId.UpHeavy, Buttons.Heavy | Buttons.Up, false },
        { MoveId.DownHeavy, Buttons.Heavy | Buttons.Down, false },
        { MoveId.GroundSlam, Buttons.Heavy | Buttons.Down, true },
    };

    [Theory, MemberData(nameof(AllMoves))]
    public void HitboxIsOutExactlyOnTheActiveFramesThenTheFighterCanAct(MoveId id, Buttons press, bool inAir)
    {
        MoveData m = Moves[id];
        GameState s = Standing(0); // alone, so nothing is hit and there is no hit-stop
        if (inAir) Airborne(ref s.Fighters[0], 0, 3000); // high enough that the slam never lands

        Step(s, press);
        Assert.Equal(id, s.Fighters[0].Move);
        for (int n = 1; n < m.TotalFrames; n++)
        {
            Assert.Equal(m.IsActiveOn(n - 1), Simulation.TryGetActiveHitbox(in s.Fighters[0], T, out _));
            Step(s);
            Assert.Equal(FighterAction.Attack, s.Fighters[0].Action);
            Assert.Equal(n, s.Fighters[0].ActionFrame);
        }
        Assert.Equal(m.IsActiveOn(m.TotalFrames - 1), Simulation.TryGetActiveHitbox(in s.Fighters[0], T, out _));

        Step(s);
        Assert.Equal(FighterAction.Idle, s.Fighters[0].Action);
        Assert.Equal(MoveId.None, s.Fighters[0].Move);
        Step(s, Buttons.Light);
        Assert.Equal(MoveId.Jab, s.Fighters[0].Move);
    }

    /// <summary>Where to stand a target so each move hits it (metres x 100 from the attacker, which faces right).</summary>
    public static TheoryData<MoveId, Buttons, int, int> MoveHits => new()
    {
        { MoveId.Jab, Buttons.Light, 80, 0 },
        { MoveId.NeutralHeavy, Buttons.Heavy, 100, 0 },
        { MoveId.SideHeavy, Buttons.Heavy | Buttons.Right, 150, 0 },
        { MoveId.UpHeavy, Buttons.Heavy | Buttons.Up, 50, 0 },
        { MoveId.DownHeavy, Buttons.Heavy | Buttons.Down, -80, 0 }, // behind: the sweep hits both sides
        { MoveId.GroundSlam, Buttons.Heavy | Buttons.Down, 0, 300 }, // attacker starts 3 m up
    };

    [Theory, MemberData(nameof(MoveHits))]
    public void EachMoveDamagesLaunchesAndFreezesByItsFrameData(MoveId id, Buttons press, int targetX, int attackerY)
    {
        MoveData m = Moves[id];
        GameState s = Standing(0, targetX);
        if (attackerY > 0) Airborne(ref s.Fighters[0], 0, attackerY);

        Step(s, press);
        for (int f = 0; f < 80 && s.Fighters[1].Josh == 0; f++) Step(s);

        ref Fighter atk = ref s.Fighters[0];
        ref Fighter def = ref s.Fighters[1];
        Assert.Equal(m.Damage, def.Josh);
        Assert.True(m.IsActiveOn(atk.ActionFrame), $"hit on frame {atk.ActionFrame}");
        Assert.Equal(FighterAction.Hitstun, def.Action);
        Assert.Equal(8 + def.Josh / 8 + m.HitstunBonus, def.HitstunFrames);

        Fix kb = m.BaseKnockback + m.KnockbackGrowth * def.Josh;
        int away = def.Position.X >= atk.Position.X ? 1 : -1;
        if (!m.HitsBothSides) Assert.Equal(atk.Facing, away);
        Assert.Equal(new FixVec2(kb * m.LaunchX * away, kb * m.LaunchY), def.Velocity);

        int stop = Simulation.HitStopFrames(m.Damage, Rules);
        Assert.Equal(stop, atk.HitStop);
        Assert.Equal(stop, def.HitStop);
    }

    [Fact]
    public void EveryHeavyOutdamagesAndOutlaunchesTheJab()
    {
        MoveData jab = Moves.Jab;
        Fix jabLaunch = jab.BaseKnockback + jab.KnockbackGrowth * jab.Damage;
        foreach (MoveId id in new[] { MoveId.NeutralHeavy, MoveId.SideHeavy, MoveId.UpHeavy, MoveId.DownHeavy, MoveId.GroundSlam })
        {
            MoveData m = Moves[id];
            Assert.True(m.Damage > jab.Damage, $"{id} damage");
            Assert.True(m.BaseKnockback + m.KnockbackGrowth * m.Damage > jabLaunch, $"{id} launch from 0%");
            Assert.True(m.Startup > jab.Startup, $"{id} should be slower than the jab");
            Assert.True(Simulation.HitStopFrames(m.Damage, Rules) > Simulation.HitStopFrames(jab.Damage, Rules), $"{id} hit-stop");
        }
    }

    [Fact]
    public void SideHeavyKnockbackGrowsWithJosh()
    {
        Fix SpeedAt(int josh)
        {
            GameState s = Standing(0, 150);
            s.Fighters[1].Josh = josh;
            Step(s, Buttons.Heavy | Buttons.Right);
            for (int f = 0; f < 80 && s.Fighters[1].Action != FighterAction.Hitstun; f++) Step(s);
            return s.Fighters[1].Velocity.Length;
        }

        Assert.True(SpeedAt(100) > SpeedAt(0));
    }

    [Fact]
    public void SideHeavyLungesForward()
    {
        MoveData m = Moves.SideHeavy;
        GameState s = Standing(0);
        Step(s, Buttons.Heavy | Buttons.Right);
        Wait(s, m.ImpulseFrame - 1);
        Assert.True(s.Fighters[0].Velocity.X < m.LungeSpeed);

        Step(s);
        Assert.Equal(m.LungeSpeed, s.Fighters[0].Velocity.X);
        Wait(s, m.TotalFrames);
        Assert.True(s.Fighters[0].Position.X > Fix.Half, $"lunged only to {s.Fighters[0].Position.X}");
    }

    [Fact]
    public void UpHeavyRisesAndIsOncePerTripIntoTheAir()
    {
        MoveData m = Moves.UpHeavy;
        GameState s = Standing(0);
        Airborne(ref s.Fighters[0], 0, 1000);

        Step(s, Buttons.Heavy | Buttons.Up);
        Wait(s, m.ImpulseFrame);
        Assert.Equal(m.RiseSpeed - T.Gravity, s.Fighters[0].Velocity.Y);
        Assert.True(s.Fighters[0].RecoveryUsed);

        Wait(s, m.TotalFrames);
        Assert.False(s.Fighters[0].Grounded);
        Step(s, Buttons.Heavy | Buttons.Up);
        Assert.Equal(FighterAction.Idle, s.Fighters[0].Action); // spent: nothing happens

        for (int f = 0; f < 600 && !s.Fighters[0].Grounded; f++) Step(s);
        Assert.False(s.Fighters[0].RecoveryUsed);
        Step(s, Buttons.Heavy | Buttons.Up);
        Assert.Equal(MoveId.UpHeavy, s.Fighters[0].Move);
    }

    [Fact]
    public void UpHeavyFromTheGroundStillSpendsTheRecovery()
    {
        MoveData m = Moves.UpHeavy;
        GameState s = Standing(0);
        Step(s, Buttons.Heavy | Buttons.Up);
        Wait(s, m.TotalFrames);

        Assert.False(s.Fighters[0].Grounded); // still coming down
        Step(s, Buttons.Heavy | Buttons.Up);
        Assert.Equal(FighterAction.Idle, s.Fighters[0].Action);
    }

    [Fact]
    public void UpHeavySteersInTheAir()
    {
        GameState s = Standing(0);
        Airborne(ref s.Fighters[0], 0, 1000);
        Step(s, Buttons.Heavy | Buttons.Up);
        for (int f = 0; f < 20; f++) Step(s, Buttons.Left);
        Assert.True(s.Fighters[0].Velocity.X < Fix.Zero);
    }

    [Fact]
    public void GroundSlamHangsThenPlungesAndEndsOnLanding()
    {
        MoveData m = Moves.GroundSlam;
        GameState s = Standing(0);
        Airborne(ref s.Fighters[0], 0, 500);
        s.Fighters[0].Velocity = new FixVec2(Fix.FromRatio(1, 10), Fix.FromRatio(1, 10));

        Step(s, Buttons.Heavy | Buttons.Down);
        FixVec2 hover = s.Fighters[0].Position;
        Wait(s, m.Startup - 1);
        Assert.Equal(hover, s.Fighters[0].Position);

        Step(s);
        Assert.Equal(-m.PlungeSpeed, s.Fighters[0].Velocity.Y);
        Assert.Equal(hover.Y - m.PlungeSpeed, s.Fighters[0].Position.Y);

        int frames = 0;
        while (!s.Fighters[0].Grounded && frames++ < 60) Step(s);
        Assert.Equal(Fix.Zero, s.Fighters[0].Position.Y);
        Assert.Equal(m.Startup + m.Active - 1, s.Fighters[0].ActionFrame); // the landing is its last active frame
        Assert.True(Simulation.TryGetActiveHitbox(in s.Fighters[0], T, out _)); // the impact

        Step(s);
        Assert.False(Simulation.TryGetActiveHitbox(in s.Fighters[0], T, out _));
        Wait(s, m.Recovery - 1); // the full recovery follows the impact
        Assert.Equal(FighterAction.Attack, s.Fighters[0].Action);
        Step(s);
        Assert.Equal(FighterAction.Idle, s.Fighters[0].Action);
    }

    // ---- Dodge -------------------------------------------------------------------------------

    [Fact]
    public void SpotDodgeStaysPutAndDashesMoveInAStraightLine()
    {
        GameState spot = Standing(0);
        Step(spot, Buttons.Dodge);
        for (int f = 0; f < T.DodgeTotalFrames; f++)
        {
            Assert.Equal(Fix.Zero, spot.Fighters[0].Position.X);
            Step(spot);
        }

        GameState dash = Standing(0);
        Step(dash, Buttons.Dodge | Buttons.Right);
        Wait(dash, T.DodgeIntangibleFrames - 1);
        Assert.Equal(T.GroundDodgeSpeed * T.DodgeIntangibleFrames, dash.Fighters[0].Position.X);
        Step(dash);
        Assert.True(dash.Fighters[0].Velocity.X < T.GroundDodgeSpeed); // slowing down

        GameState air = Standing(0);
        Airborne(ref air.Fighters[0], 0, 500);
        Step(air, Buttons.Dodge | Buttons.Up | Buttons.Right);
        FixVec2 v = air.Fighters[0].Velocity;
        Assert.True(v.X > Fix.Zero && v.X == v.Y, $"diagonal: {v}");
        Wait(air, T.DodgeIntangibleFrames - 1);
        Assert.Equal(v, air.Fighters[0].Velocity); // no gravity while intangible
        Assert.Equal(Fix.FromInt(5) + v.Y * T.DodgeIntangibleFrames, air.Fighters[0].Position.Y);
        Step(air);
        Assert.Equal(new FixVec2(v.X / 2, v.Y / 2 - T.Gravity), air.Fighters[0].Velocity);
    }

    [Fact]
    public void OneAirDodgePerTripIntoTheAir()
    {
        GameState s = Standing(0);
        Airborne(ref s.Fighters[0], 0, 3000);
        Step(s, Buttons.Dodge);
        Assert.True(s.Fighters[0].AirDodgeUsed);
        Wait(s, T.DodgeTotalFrames + T.DodgeCooldownFrames);
        Assert.False(s.Fighters[0].Grounded);

        Step(s, Buttons.Dodge);
        Assert.Equal(FighterAction.Idle, s.Fighters[0].Action);

        for (int f = 0; f < 900 && !s.Fighters[0].Grounded; f++) Step(s);
        Assert.False(s.Fighters[0].AirDodgeUsed);
    }

    [Fact]
    public void DodgeCooldownLastsOneSecondFromTheEndOfTheDodge()
    {
        GameState s = Standing(0);
        Step(s, Buttons.Dodge);
        Wait(s, T.DodgeTotalFrames - 1);
        Assert.Equal(FighterAction.Dodge, s.Fighters[0].Action);
        Step(s);
        Assert.Equal(FighterAction.Idle, s.Fighters[0].Action);
        Assert.Equal(T.DodgeCooldownFrames, s.Fighters[0].DodgeCooldown);

        GameState early = s.Clone();
        Wait(early, T.DodgeCooldownFrames - 2);
        Step(early, Buttons.Dodge);
        Assert.Equal(FighterAction.Idle, early.Fighters[0].Action);

        Wait(s, T.DodgeCooldownFrames - 1);
        Step(s, Buttons.Dodge);
        Assert.Equal(FighterAction.Dodge, s.Fighters[0].Action);
    }

    [Fact]
    public void HoldingDodgeDoesNotRepeatIt()
    {
        GameState s = Standing(0);
        for (int f = 0; f < T.DodgeTotalFrames + T.DodgeCooldownFrames + 10; f++) Step(s, Buttons.Dodge);
        Assert.Equal(FighterAction.Idle, s.Fighters[0].Action);
    }

    /// <summary>
    /// P1 at 0 m jabs P2 at 0.8 m, who spot-dodges so that the jab's first active frame lands on
    /// dodge frame <paramref name="dodgeFrame"/>. Runs to the jab's last active frame.
    /// </summary>
    private static GameState JabIntoDodge(int dodgeFrame, int fighters = 2)
    {
        GameState s = fighters == 2 ? Standing(0, 80) : Standing(0, 80, -80);
        if (fighters == 3) s.Fighters[2].Facing = 1;
        const int dodgeAt = 10;
        int jabAt = dodgeAt + dodgeFrame - Moves.Jab.Startup;
        int end = jabAt + Moves.Jab.Startup + Moves.Jab.Active - 1;
        for (int f = 0; f <= end && s.Fighters[0].Action != FighterAction.Stunned; f++)
        {
            Buttons p1 = f == jabAt ? Buttons.Light : Buttons.None;
            Buttons p2 = f == dodgeAt ? Buttons.Dodge : Buttons.None;
            if (fighters == 3) Step(s, p1, p2, f == jabAt ? Buttons.Light : Buttons.None);
            else Step(s, p1, p2);
        }
        return s;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    public void AHitInTheFirstSixFramesOfADodgeIsParried(int dodgeFrame)
    {
        GameState s = JabIntoDodge(dodgeFrame);
        ref Fighter atk = ref s.Fighters[0];
        ref Fighter def = ref s.Fighters[1];

        Assert.Equal(FighterAction.Stunned, atk.Action);
        Assert.Equal(T.ParryStunFrames, atk.HitstunFrames);
        Assert.Equal(Fix.Zero, atk.Velocity.X);
        Assert.Equal(0, def.Josh);
        Assert.Equal(FighterAction.Idle, def.Action); // the dodge ends: free to punish
        Assert.Equal(0, def.DodgeCooldown);
        Assert.Equal(Rules.MaxHitStop, atk.HitStop);
        Assert.Equal(Rules.MaxHitStop, def.HitStop);

        Wait(s, Rules.MaxHitStop);
        GameState unpunished = s.Clone();

        // After the freeze the defender acts at once, and a neutral heavy lands before the stun ends.
        Step(s, Buttons.None, Buttons.Heavy);
        Assert.Equal(MoveId.NeutralHeavy, def.Move);
        Wait(s, Moves.NeutralHeavy.Startup);
        Assert.Equal(FighterAction.Hitstun, atk.Action);
        Assert.Equal(Moves.NeutralHeavy.Damage, atk.Josh);

        // Left alone, the attacker stays stunned for exactly ParryStunFrames after the freeze.
        Wait(unpunished, T.ParryStunFrames - 1);
        Assert.Equal(FighterAction.Stunned, unpunished.Fighters[0].Action);
        Step(unpunished);
        Assert.Equal(FighterAction.Idle, unpunished.Fighters[0].Action);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(10)]
    [InlineData(15)]
    public void LaterInTheDodgeTheHitPassesThroughWithoutBeingSpent(int dodgeFrame)
    {
        GameState s = JabIntoDodge(dodgeFrame);
        Assert.Equal(0, s.Fighters[1].Josh);
        Assert.Equal(FighterAction.Attack, s.Fighters[0].Action);
        Assert.False(s.Fighters[0].AttackConnected);
        Assert.Equal(FighterAction.Dodge, s.Fighters[1].Action);
    }

    [Fact]
    public void TheLastFramesOfADodgeCanBeHit()
    {
        GameState s = JabIntoDodge(T.DodgeIntangibleFrames - 1); // second active frame lands on the first hittable one
        Assert.Equal(Moves.Jab.Damage, s.Fighters[1].Josh);
        Assert.Equal(FighterAction.Hitstun, s.Fighters[1].Action);
    }

    [Fact]
    public void AnAttackerParriedAndHitOnTheSameFrameIsKnockedBack()
    {
        // P3 stands behind P1 and jabs it on the same frame that P2 parries P1's jab.
        GameState s = JabIntoDodge(2, fighters: 3);
        Assert.Equal(FighterAction.Hitstun, s.Fighters[0].Action);
        Assert.Equal(Moves.Jab.Damage, s.Fighters[0].Josh);
        Assert.Equal(0, s.Fighters[1].Josh);
        Assert.Equal(FighterAction.Idle, s.Fighters[1].Action);
    }

    [Fact]
    public void GettingHitGivesBackTheAirDodgeAndTheRecovery()
    {
        // In the air, so that landing (which also gives them back) can't be what resets them.
        GameState s = Standing(0, 80);
        ref Fighter def = ref s.Fighters[1];
        Airborne(ref def, 80, 50);
        def.AirDodgeUsed = true;
        def.RecoveryUsed = true;
        def.DodgeCooldown = 30;
        Step(s, Buttons.Light);
        for (int f = 0; f < 20 && def.Josh == 0; f++) Step(s);

        Assert.Equal(Moves.Jab.Damage, def.Josh);
        Assert.False(def.Grounded);
        Assert.False(def.AirDodgeUsed);
        Assert.False(def.RecoveryUsed);
        Assert.True(def.DodgeCooldown > 0); // the cooldown is not refunded
    }

    [Fact]
    public void ADodgeCutShortByAHitStillCostsItsCooldown()
    {
        GameState s = JabIntoDodge(T.DodgeIntangibleFrames - 1); // lands on the first hittable frame
        Assert.Equal(FighterAction.Hitstun, s.Fighters[1].Action);
        Assert.Equal(T.DodgeCooldownFrames, s.Fighters[1].DodgeCooldown);
    }

    // ---- Hit-stop ----------------------------------------------------------------------------

    [Fact]
    public void HitStopScalesWithDamageFromFourToTenFrames()
    {
        Assert.Equal(4, Simulation.HitStopFrames(0, Rules));
        Assert.Equal(5, Simulation.HitStopFrames(Moves.Jab.Damage, Rules));
        Assert.Equal(10, Simulation.HitStopFrames(16, Rules));
        Assert.Equal(10, Simulation.HitStopFrames(300, Rules));
        for (int d = 1; d < 40; d++)
            Assert.True(Simulation.HitStopFrames(d, Rules) >= Simulation.HitStopFrames(d - 1, Rules));
    }

    [Fact]
    public void HitStopFreezesAttackerAndDefenderThenKnockbackStarts()
    {
        GameState s = Standing(0, 80);
        Step(s, Buttons.Light);
        for (int f = 0; f < 20 && s.Fighters[1].Josh == 0; f++) Step(s);

        Fighter atk = s.Fighters[0], def = s.Fighters[1];
        int stop = Simulation.HitStopFrames(Moves.Jab.Damage, Rules);
        for (int f = 0; f < stop; f++)
        {
            Step(s, Buttons.Right, Buttons.Left); // ignored while frozen
            Assert.Equal(atk.Position, s.Fighters[0].Position);
            Assert.Equal(atk.ActionFrame, s.Fighters[0].ActionFrame);
            Assert.Equal(def.Position, s.Fighters[1].Position);
            Assert.Equal(def.HitstunFrames, s.Fighters[1].HitstunFrames);
        }
        Assert.Equal(0, s.Fighters[1].HitStop);

        Step(s);
        Assert.NotEqual(def.Position, s.Fighters[1].Position);
        Assert.Equal(atk.ActionFrame + 1, s.Fighters[0].ActionFrame);
    }

    [Fact]
    public void APressDuringHitStopIsBufferedAndAHeldOneIsNot()
    {
        // After a parry, the defender is free but frozen for MaxHitStop frames.
        GameState pressed = JabIntoDodge(2);
        GameState held = pressed.Clone();

        Step(pressed);
        for (int f = 1; f < Rules.MaxHitStop; f++) Step(pressed, Buttons.None, Buttons.Light);
        Step(pressed, Buttons.None, Buttons.Light);
        Assert.Equal(MoveId.Jab, pressed.Fighters[1].Move);

        held.Fighters[1].PreviousButtons = Buttons.Light; // was already held when the freeze began
        for (int f = 0; f <= Rules.MaxHitStop; f++) Step(held, Buttons.None, Buttons.Light);
        Assert.Equal(FighterAction.Idle, held.Fighters[1].Action);
    }

    // ---- Guts --------------------------------------------------------------------------------

    /// <summary>P1 jabs P2, whose Josh starts at <paramref name="josh"/>. Returns right after the hit.</summary>
    private static GameState JabAt(int josh, MatchRules? rules = null)
    {
        rules ??= Rules;
        GameState s = Standing(rules, 0, 80);
        s.Fighters[1].Josh = josh;
        Simulation.Advance(s, rules, new[] { Buttons.Light, Buttons.None });
        for (int f = 0; f < 20 && s.Fighters[1].Josh == josh; f++) Simulation.Advance(s, rules, new Buttons[2]);
        return s;
    }

    [Fact]
    public void GutsHealsAndArmorsAThreeSecondSurvivor()
    {
        GameState s = JabAt(146); // -> 152
        ref Fighter f = ref s.Fighters[1];
        Assert.True(f.GutsUsed);
        Assert.Equal(Rules.GutsDelayFrames, f.GutsTimer);

        Wait(s, f.HitStop + Rules.GutsDelayFrames - 1); // timers wait out the hit-stop
        Assert.Equal(1, f.GutsTimer);
        Assert.Equal(152, f.Josh);
        Assert.Equal(0, f.ArmorFrames);

        Step(s);
        Assert.Equal(0, f.GutsTimer);
        Assert.Equal(152 - Rules.GutsHeal, f.Josh);
        Assert.Equal(Rules.GutsArmorFrames, f.ArmorFrames);
    }

    [Fact]
    public void GutsNeedsTheHitToLeaveThemAt150()
    {
        GameState s = JabAt(143); // -> 149
        Assert.False(s.Fighters[1].GutsUsed);
        Assert.Equal(0, s.Fighters[1].GutsTimer);
    }

    [Fact]
    public void ArmorTakesTheJoshButNotTheKnockback()
    {
        GameState s = Standing(0, 80);
        ref Fighter def = ref s.Fighters[1];
        def.Josh = 100;
        def.ArmorFrames = 30;
        Step(s, Buttons.Light);
        for (int f = 0; f < 20 && def.Josh == 100; f++) Step(s);

        Assert.Equal(100 + Moves.Jab.Damage, def.Josh);
        Assert.Equal(FighterAction.Idle, def.Action);
        Assert.Equal(FixVec2.Zero, def.Velocity);
        Assert.True(def.Grounded);
        Assert.True(def.HitStop > 0);
    }

    [Fact]
    public void ArmorLastsItsFramesThenRunsOut()
    {
        GameState s = Standing(0);
        s.Fighters[0].ArmorFrames = Rules.GutsArmorFrames;
        Wait(s, Rules.GutsArmorFrames - 1);
        Assert.Equal(1, s.Fighters[0].ArmorFrames);
        Step(s);
        Assert.Equal(0, s.Fighters[0].ArmorFrames);
    }

    [Fact]
    public void GutsIsOncePerStock()
    {
        GameState s = JabAt(146);
        Wait(s, s.Fighters[1].HitStop + Rules.GutsDelayFrames + Rules.GutsArmorFrames);
        Assert.Equal(122, s.Fighters[1].Josh);

        // Back over 150: no second Guts this stock.
        s.Fighters[1].Josh = 150;
        s.Fighters[0].Position = new FixVec2(s.Fighters[1].Position.X - Fix.FromRatio(4, 5), Fix.Zero);
        s.Fighters[0].Facing = 1;
        s.Fighters[1].Action = FighterAction.Idle;
        Step(s, Buttons.Light);
        for (int f = 0; f < 20 && s.Fighters[1].Josh == 150; f++) Step(s);
        Assert.Equal(156, s.Fighters[1].Josh);
        Assert.Equal(0, s.Fighters[1].GutsTimer);
    }

    [Fact]
    public void LosingTheStockCancelsGutsAndTheNextStockGetsItBack()
    {
        var graybox = new MatchRules { Stage = Stages.Graybox };
        GameState s = Standing(graybox, 0, 80);
        s.Fighters[1].Josh = 160;
        s.Fighters[1].GutsUsed = true;
        s.Fighters[1].GutsTimer = 100;
        s.Fighters[1].Position = new FixVec2(Fix.FromInt(30), Fix.Zero); // outside the blast zone
        Simulation.Advance(s, graybox, new Buttons[2]);

        Assert.Equal(FighterAction.Respawning, s.Fighters[1].Action);
        Assert.Equal(0, s.Fighters[1].GutsTimer);
        for (int f = 0; f < graybox.RespawnFrames; f++) Simulation.Advance(s, graybox, new Buttons[2]);
        Assert.Equal(FighterAction.Idle, s.Fighters[1].Action);
        Assert.False(s.Fighters[1].GutsUsed);
    }

    [Fact]
    public void GutsCanBeTurnedOff()
    {
        var noGuts = new MatchRules { Stage = Wide, Guts = false };
        GameState s = JabAt(160, noGuts);
        Assert.False(s.Fighters[1].GutsUsed);
        Assert.Equal(0, s.Fighters[1].GutsTimer);
    }
    // ---- Fixes from review -------------------------------------------------------------------

    [Fact]
    public void AParriedUpHeavyStopsRisingAndAGroundHeavyPunishesIt()
    {
        // P1's up heavy comes out (and starts rising) on frame 6; P2 dodges on frame 4, so it is parried on dodge frame 2.
        GameState s = Standing(0, 50);
        for (int f = 0; f <= Moves.UpHeavy.Startup && s.Fighters[0].Action != FighterAction.Stunned; f++)
            Step(s, f == 0 ? Buttons.Heavy | Buttons.Up : Buttons.None, f == 4 ? Buttons.Dodge : Buttons.None);

        ref Fighter atk = ref s.Fighters[0];
        Assert.Equal(FighterAction.Stunned, atk.Action);
        Assert.True(atk.Velocity.Y <= Fix.Zero, $"still rising at {atk.Velocity.Y}");
        Fix parriedAt = atk.Position.Y;

        Wait(s, Rules.MaxHitStop);
        Step(s, Buttons.None, Buttons.Heavy);
        for (int f = 0; f < Moves.NeutralHeavy.Startup; f++)
        {
            Assert.True(atk.Position.Y <= parriedAt);
            Step(s);
        }
        Assert.Equal(Moves.NeutralHeavy.Damage, atk.Josh);
    }

    [Fact]
    public void AnAttackerParriedInTheAirStopsDriftingAndFalls()
    {
        // P1 jabs while drifting right through the air; P2 parries it.
        GameState s = Standing(0, 120);
        Airborne(ref s.Fighters[0], 0, 100);
        s.Fighters[0].Velocity = new FixVec2(Fix.FromRatio(1, 10), Fix.Zero);
        for (int f = 0; f <= Moves.Jab.Startup && s.Fighters[0].Action != FighterAction.Stunned; f++)
            Step(s, f == 0 ? Buttons.Light : Buttons.None, f == 2 ? Buttons.Dodge : Buttons.None);

        Assert.Equal(FighterAction.Stunned, s.Fighters[0].Action);
        Assert.Equal(Fix.Zero, s.Fighters[0].Velocity.X);
        Assert.True(s.Fighters[0].Velocity.Y <= Fix.Zero);
        Assert.False(s.Fighters[0].Grounded);
    }

    [Fact]
    public void ParryingWithAnAirDodgeKeepsSomeMomentumAndGivesTheAirDodgeBack()
    {
        // P2 air-dodges up and left toward P1 (as when recovering) and parries P1's jab on dodge frame 2.
        GameState s = Standing(0, 120);
        Airborne(ref s.Fighters[1], 120, 20);
        Step(s, Buttons.Light);
        Step(s);
        Step(s, Buttons.None, Buttons.Dodge | Buttons.Up | Buttons.Left);
        FixVec2 dash = s.Fighters[1].Velocity;
        for (int f = 0; f < 3 && s.Fighters[0].Action != FighterAction.Stunned; f++) Step(s);

        ref Fighter def = ref s.Fighters[1];
        Assert.Equal(FighterAction.Stunned, s.Fighters[0].Action);
        Assert.Equal(FighterAction.Idle, def.Action);
        Assert.False(def.AirDodgeUsed);
        Assert.Equal(new FixVec2(dash.X / 2, dash.Y / 2), def.Velocity);
    }

    [Fact]
    public void AGroundSlamFromJustAboveTheFloorHitsOnTheFrameItLands()
    {
        MoveData m = Moves.GroundSlam;
        GameState s = Standing(0, 50);
        Airborne(ref s.Fighters[0], 0, 30); // less than one frame's plunge up
        s.Fighters[0].Velocity = new FixVec2(Fix.Zero, Fix.FromRatio(1, 100));
        Step(s, Buttons.Heavy | Buttons.Down);
        Assert.Equal(MoveId.GroundSlam, s.Fighters[0].Move);
        Wait(s, m.Startup);

        Assert.True(s.Fighters[0].Grounded);
        Assert.Equal(m.Damage, s.Fighters[1].Josh);
    }

    [Fact]
    public void JumpPressedWithAnAttackOrDodgeIsIgnoredAndNotSpent()
    {
        // On the ground: Jump + Down + Heavy is the down heavy, on the ground, and it hits.
        GameState s = Standing(0, 50);
        Step(s, Buttons.Jump | Buttons.Down | Buttons.Heavy);
        Assert.Equal(MoveId.DownHeavy, s.Fighters[0].Move);
        Assert.True(s.Fighters[0].Grounded);
        for (int f = 0; f < 30 && s.Fighters[1].Josh == 0; f++) Step(s);
        Assert.Equal(Moves.DownHeavy.Damage, s.Fighters[1].Josh);

        // Jump + Light: a jab on the ground.
        s = Standing(0);
        Step(s, Buttons.Jump | Buttons.Light);
        Assert.Equal(MoveId.Jab, s.Fighters[0].Move);
        Assert.True(s.Fighters[0].Grounded);

        // In the air, Jump + Dodge: the air dodge, and the air jump is still there afterwards.
        s = Standing(0);
        Airborne(ref s.Fighters[0], 0, 3000);
        Step(s, Buttons.Jump | Buttons.Dodge);
        Assert.Equal(FighterAction.Dodge, s.Fighters[0].Action);
        Assert.Equal(T.AirJumps, s.Fighters[0].AirJumpsLeft);

        // Jump alone still jumps.
        s = Standing(0);
        Step(s, Buttons.Jump);
        Assert.False(s.Fighters[0].Grounded);
        Assert.Equal(T.JumpVelocity - T.Gravity, s.Fighters[0].Velocity.Y);
    }

    [Theory]
    [InlineData(MoveId.UpHeavy, -20, 0)]
    [InlineData(MoveId.GroundSlam, -30, 300)]
    public void MovesThatReachBehindLaunchTargetsBehindAwayFromTheAttacker(MoveId id, int targetX, int attackerY)
    {
        GameState s = Standing(0, targetX);
        if (attackerY > 0) Airborne(ref s.Fighters[0], 0, attackerY);
        Step(s, Buttons.Heavy | (id == MoveId.UpHeavy ? Buttons.Up : Buttons.Down));
        for (int f = 0; f < 80 && s.Fighters[1].Josh == 0; f++) Step(s);

        Assert.Equal(Moves[id].Damage, s.Fighters[1].Josh);
        Assert.True(s.Fighters[1].Velocity.X < Fix.Zero, $"launched toward the attacker: {s.Fighters[1].Velocity}");
    }

    [Fact]
    public void SideHeavyInTheAirBrakesItsLunge()
    {
        MoveData m = Moves.SideHeavy;
        GameState s = Standing(0);
        Airborne(ref s.Fighters[0], 0, 3000);
        Step(s, Buttons.Heavy | Buttons.Right);
        Wait(s, m.TotalFrames);

        Assert.Equal(Fix.Zero, s.Fighters[0].Velocity.X);
        Assert.True(s.Fighters[0].Position.X < Fix.FromInt(2), $"carried {s.Fighters[0].Position.X} m");
    }

    // ---- Guts and armor rules ----------------------------------------------------------------

    [Fact]
    public void ArmorDoesNotInterruptTheArmoredFightersAttack()
    {
        // P2 (armored) starts a neutral heavy; P1's jab lands during its startup. P2's heavy carries on and lands.
        GameState s = Standing(0, 80);
        s.Fighters[1].ArmorFrames = 60;
        Step(s, Buttons.None, Buttons.Heavy);
        Step(s, Buttons.Light);
        for (int f = 0; f < 10 && s.Fighters[1].Josh == 0; f++) Step(s);

        ref Fighter armored = ref s.Fighters[1];
        Assert.Equal(Moves.Jab.Damage, armored.Josh);
        Assert.Equal(FighterAction.Attack, armored.Action);
        Assert.Equal(MoveId.NeutralHeavy, armored.Move);

        for (int f = 0; f < 30 && s.Fighters[0].Josh == 0; f++) Step(s);
        Assert.Equal(Moves.NeutralHeavy.Damage, s.Fighters[0].Josh);
    }

    [Fact]
    public void AFrozenFightersHitboxWaitsForTheFreezeToEnd()
    {
        // P2 is frozen (as when armored and hit) on an active frame of its neutral heavy, with P1 inside the hitbox.
        GameState s = Standing(0, 80);
        ref Fighter p2 = ref s.Fighters[1];
        p2.Action = FighterAction.Attack;
        p2.Move = MoveId.NeutralHeavy;
        p2.ActionFrame = Moves.NeutralHeavy.Startup;
        p2.HitStop = 3;
        p2.ArmorFrames = 60;

        Wait(s, 3);
        Assert.Equal(0, s.Fighters[0].Josh);
        Assert.Equal(Moves.NeutralHeavy.Startup, p2.ActionFrame);

        Step(s);
        Assert.Equal(Moves.NeutralHeavy.Damage, s.Fighters[0].Josh);
    }

    [Fact]
    public void AParryStillStunsAnArmoredAttacker()
    {
        GameState s = Standing(0, 80);
        s.Fighters[0].ArmorFrames = 60;
        for (int f = 0; f <= Moves.Jab.Startup && s.Fighters[0].Action != FighterAction.Stunned; f++)
            Step(s, f == 0 ? Buttons.Light : Buttons.None, f == 2 ? Buttons.Dodge : Buttons.None);

        Assert.Equal(FighterAction.Stunned, s.Fighters[0].Action);
        Assert.Equal(0, s.Fighters[1].Josh);
    }

    [Fact]
    public void ALaterHitDoesNotRestartAPendingGuts()
    {
        GameState s = Standing(0, 80);
        ref Fighter def = ref s.Fighters[1];
        def.Josh = 160;
        def.GutsUsed = true;
        def.GutsTimer = 100;
        Step(s, Buttons.Light);
        for (int f = 0; f < 20 && def.Josh == 160; f++) Step(s);

        Assert.Equal(166, def.Josh);
        Assert.True(def.GutsTimer < 100, $"timer {def.GutsTimer}");
        Wait(s, def.HitStop + def.GutsTimer);
        Assert.Equal(166 - Rules.GutsHeal, def.Josh);
    }
    [Fact]
    public void ParryingTwoAttackersOnOneFrameHalvesTheDodgeSpeedOnce()
    {
        // P1 air-dodges up and right between P2 and P3, who jab it on the same frame: both parried.
        GameState s = Standing(0, 80, -80);
        s.Fighters[2].Facing = 1;
        Airborne(ref s.Fighters[0], 0, 20);
        Step(s, Buttons.None, Buttons.Light, Buttons.Light);
        Step(s);
        Step(s, Buttons.Dodge | Buttons.Up | Buttons.Right);
        FixVec2 dash = s.Fighters[0].Velocity;
        for (int f = 0; f < 3 && s.Fighters[1].Action != FighterAction.Stunned; f++) Step(s);

        Assert.Equal(FighterAction.Stunned, s.Fighters[1].Action);
        Assert.Equal(FighterAction.Stunned, s.Fighters[2].Action);
        Assert.Equal(FighterAction.Idle, s.Fighters[0].Action);
        Assert.Equal(new FixVec2(dash.X / 2, dash.Y / 2), s.Fighters[0].Velocity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AHeavyPressedRightAfterALandedSlamPunishesItBeforeItCanDodge(int parity)
    {
        // P1 slams down next to P2, missing. P2 presses a neutral heavy (startup 14) the frame after
        // the landing; P1 mashes Dodge. The slam's 14 recovery frames after the impact keep P1 from
        // acting until the heavy has landed.
        GameState s = Standing(0, 150);
        Airborne(ref s.Fighters[0], 0, 300);
        Step(s, Buttons.Heavy | Buttons.Down);
        for (int f = 0; f < 60 && !s.Fighters[0].Grounded; f++) Step(s);
        Assert.Equal(0, s.Fighters[1].Josh);

        Step(s, Buttons.None, Buttons.Heavy);
        // Fresh presses need a release in between, so mash on every other frame, in both phases.
        for (int f = 0; f < 30 && s.Fighters[0].Josh == 0; f++) Step(s, f % 2 == parity ? Buttons.Dodge : Buttons.None);
        Assert.Equal(Moves.NeutralHeavy.Damage, s.Fighters[0].Josh);
    }
    [Fact]
    public void FrozenByHitMarksOnlyAFighterFrozenByAHitItTook()
    {
        // A jab lands: the target is frozen by the hit, the attacker by its own hit landing.
        GameState s = Standing(0, 80);
        Step(s, Buttons.Light);
        for (int f = 0; f < 20 && s.Fighters[1].HitStop == 0; f++) Step(s);
        Assert.True(s.Fighters[1].FrozenByHit);
        Assert.False(s.Fighters[0].FrozenByHit);
        Wait(s, Simulation.HitStopFrames(Moves.Jab.Damage, Rules));
        Assert.Equal(0, s.Fighters[1].HitStop);
        Assert.False(s.Fighters[1].FrozenByHit); // cleared when the freeze ends

        // At the Josh cap the hit adds nothing, and with armor it causes no hitstun: still a hit taken.
        foreach (bool armored in new[] { false, true })
        {
            s = Standing(0, 80);
            s.Fighters[1].Josh = Rules.MaxJosh;
            if (armored) s.Fighters[1].ArmorFrames = 60;
            Step(s, Buttons.Light);
            for (int f = 0; f < 20 && s.Fighters[1].HitStop == 0; f++) Step(s);
            Assert.True(s.Fighters[1].FrozenByHit, $"armored {armored}");
        }

        // A parry freezes both, but neither was hit.
        s = JabIntoDodge(2);
        Assert.True(s.Fighters[0].HitStop > 0 && s.Fighters[1].HitStop > 0);
        Assert.False(s.Fighters[0].FrozenByHit);
        Assert.False(s.Fighters[1].FrozenByHit);
    }
}
