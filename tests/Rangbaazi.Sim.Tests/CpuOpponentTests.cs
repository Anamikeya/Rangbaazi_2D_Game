using Rangbaazi.Sim.Diagnostics;
using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim.Tests;

/// <summary>
/// The graybox's CPU players are <see cref="SparringBot"/>s at a <see cref="CpuLevel"/>. These
/// check what someone playing against one relies on: higher levels jab sooner, every level can
/// win, the CPU never throws away its own stocks, and CPU inputs are rollback-safe.
/// </summary>
public class CpuOpponentTests
{
    private static readonly MatchRules Rules = new() { Stage = Stages.Graybox };
    private static readonly CpuLevel[] AllLevels = { CpuLevel.Easy, CpuLevel.Normal, CpuLevel.Hard };

    /// <summary>Long soak matches: enough stocks that nobody runs out within the test.</summary>
    private static readonly MatchRules Soak = new() { Stage = Stages.Graybox, Stocks = 1000 };
    private const int SoakFrames = 5 * 60 * 60;

    public static TheoryData<CpuLevel> Levels => new() { CpuLevel.Easy, CpuLevel.Normal, CpuLevel.Hard };

    /// <summary>Two fighters landed and standing on the main stage, P1 on the left facing right.</summary>
    private static GameState Standing(int p1X, int p2X)
    {
        var s = Simulation.CreateInitialState(Rules, 2, 1);
        for (int i = 0; i < 2; i++)
        {
            ref Fighter f = ref s.Fighters[i];
            f.Position = new FixVec2(Fix.FromInt(i == 0 ? p1X : p2X), Fix.Zero);
            f.Velocity = FixVec2.Zero;
            f.Grounded = true;
            f.Facing = i == 0 ? 1 : -1;
        }
        return s;
    }

    [Fact]
    public void HigherLevelsJabSooner()
    {
        const int frames = 2400;
        var jabs = new int[AllLevels.Length];
        for (int l = 0; l < AllLevels.Length; l++)
        {
            GameState s = Standing(0, 1); // in reach and facing: every frame is a chance to jab
            for (int f = 0; f < frames; f++)
            {
                s.Frame = f;
                Buttons b = SparringBot.Think(s, Rules, 0, AllLevels[l]);
                Assert.True(b == Buttons.None || b == Buttons.Light, $"{AllLevels[l]} frame {f}: {b}");
                if (b == Buttons.Light) jabs[l]++;
            }

            int expected = frames / SparringBot.JabOdds(AllLevels[l]);
            Assert.InRange(jabs[l], expected * 3 / 4, expected * 5 / 4);
        }

        Assert.True(jabs[0] < jabs[1] && jabs[1] < jabs[2], $"jabs per level: {string.Join(", ", jabs)}");
    }

    [Fact]
    public void LevelOnlyChangesWhenItAttacksAndDodges()
    {
        // Same state, every level: identical buttons whenever neither is attacking or dodging, so
        // walking, chasing and recovering are the same at every level.
        const Buttons decisions = Buttons.Light | Buttons.Heavy | Buttons.Dodge;
        GameState s = Simulation.CreateInitialState(Rules, 4, 5);
        var inputs = new Buttons[4];
        var rng = new SimRandom(11);
        for (int f = 0; f < 3600; f++)
        {
            for (int p = 0; p < inputs.Length; p++)
            {
                Buttons hard = SparringBot.Think(s, Rules, p, CpuLevel.Hard);
                foreach (CpuLevel level in AllLevels)
                {
                    Buttons other = SparringBot.Think(s, Rules, p, level);
                    // Off the stage it is recovering, which ignores the level: compare everything.
                    ref readonly Fighter me = ref s.Fighters[p];
                    FixRect main = Rules.Stage.Platforms[0];
                    bool off = me.Position.X < main.MinX || me.Position.X > main.MaxX || (!me.Grounded && me.Position.Y < main.MaxY);
                    if (off || ((hard | other) & decisions) == 0) Assert.Equal(hard, other);
                }
                // Mix levels so the match visits many situations.
                inputs[p] = SparringBot.Think(s, Rules, p, AllLevels[rng.Range(0, AllLevels.Length)]);
            }
            Simulation.Advance(s, Rules, inputs);
        }
    }

    [Fact]
    public void DefaultLevelIsTheSmokeTestBot()
    {
        GameState s = Simulation.CreateInitialState(Rules, 2, SmokeScript.Seed);
        var inputs = new Buttons[2];
        for (int f = 0; f < SmokeScript.Frames; f++)
        {
            for (int p = 0; p < 2; p++)
            {
                inputs[p] = SparringBot.Think(s, Rules, p, CpuLevel.Hard);
                Assert.Equal(inputs[p], SparringBot.Think(s, Rules, p));
            }
            Simulation.Advance(s, Rules, inputs);
        }
        Assert.Equal(SmokeScript.Run(SmokeScript.CreateRules()).Checksum(), s.Checksum());
    }

    [Fact]
    public void RejectsUnknownLevels()
    {
        GameState s = Standing(0, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => SparringBot.Think(s, Rules, 0, (CpuLevel)99));
    }

    [Theory, MemberData(nameof(Levels))]
    public void BeatsAnOpponentThatDoesNothing(CpuLevel level)
    {
        const int maxFrames = 3 * 60 * 60;
        GameState s = Simulation.CreateInitialState(Rules, 2, 1);
        var inputs = new Buttons[2]; // P1 never presses anything
        for (int f = 0; f < maxFrames && !s.MatchOver; f++)
        {
            inputs[1] = SparringBot.Think(s, Rules, 1, level);
            Simulation.Advance(s, Rules, inputs);
        }

        Assert.True(s.MatchOver, $"{level}: no winner after {s.Frame} frames");
        Assert.Equal(1, s.WinnerIndex);
        Assert.Equal(Rules.Stocks, s.Fighters[1].Stocks);
    }

    [Fact]
    public void OneOnOneCpuMatchesFinish()
    {
        // The bots must keep fighting. A stand-off once stopped matches for minutes: both stacked
        // near an edge, taking turns to jump, neither willing to jab in the air.
        const int maxFrames = 4 * 60 * 60;
        foreach (CpuLevel a in AllLevels)
        foreach (CpuLevel b in AllLevels)
        for (int start = 0; start < 10; start++)
        {
            GameState s = Simulation.CreateInitialState(Rules, 2, 1);
            s.Frame = start * 37; // shifts the hesitation draws, so each match plays out differently
            var inputs = new Buttons[2];
            for (int f = 0; f < maxFrames && !s.MatchOver; f++)
            {
                inputs[0] = SparringBot.Think(s, Rules, 0, a);
                inputs[1] = SparringBot.Think(s, Rules, 1, b);
                Simulation.Advance(s, Rules, inputs);
            }
            Assert.True(s.MatchOver, $"{a} vs {b} (start {start}): no winner after 4 minutes");
        }
    }

    [Theory, MemberData(nameof(Levels))]
    public void NeverFallsOffChasingAnOpponentWhoNeverAttacks(CpuLevel level)
    {
        // P1 runs between the stage edges, jumps onto the platforms and off the stage, and never
        // attacks, so the CPU can't be knocked out: any stock it loses, it lost by itself.
        // P1 gets knocked out over and over (and sometimes falls off itself), so the CPU spends a
        // lot of time chasing someone who is off the side, which is when it used to fall off.
        GameState s = Simulation.CreateInitialState(Soak, 2, 1);
        var inputs = new Buttons[2];
        int knockouts = 0;
        for (int f = 0; f < SoakFrames; f++)
        {
            inputs[0] = Wanderer(s, f);
            inputs[1] = SparringBot.Think(s, Soak, 1, level);
            int before = s.Fighters[0].Stocks;
            Simulation.Advance(s, Soak, inputs);
            if (s.Fighters[0].Stocks < before) knockouts++;
            Assert.True(s.Fighters[1].Stocks == Soak.Stocks, $"{level}: CPU lost a stock by itself on frame {f}");
        }
        Assert.True(knockouts >= 10, $"{level}: only {knockouts} knockouts, so the chase was barely tested");
    }

    [Theory, MemberData(nameof(Levels))]
    public void NeverSelfDestructsInAFreeForAll(CpuLevel level) =>
        AssertNoSelfDestructs(level.ToString(), (start, player) => level);

    [Fact]
    public void NeverSelfDestructsInAMixedLevelFreeForAll() =>
        AssertNoSelfDestructs("mixed", (start, player) => AllLevels[(start + player) % AllLevels.Length]);

    /// <summary>
    /// Three- and four-CPU free-for-alls that check how every lost stock came about. A CPU that
    /// loses a stock must have been hit or parried since it last stood on a platform in control
    /// (idle, at no more than running speed). So walking, jumping, dodging or lunging off the
    /// stage by itself fails, however fast it was moving, and so does losing a stock after its own
    /// parry. Being knocked out, including sliding off the edge from a hit, does not.
    /// </summary>
    private static void AssertNoSelfDestructs(string name, Func<int, int, CpuLevel> levelOf)
    {
        int knockouts = 0;
        foreach (int players in new[] { 3, 4 })
        for (int start = 0; start < 8; start++)
        {
            GameState s = Simulation.CreateInitialState(Soak, players, 1);
            s.Frame = start * 7919; // the sim doesn't use the seed yet; this varies the bots instead
            var levels = new CpuLevel[players];
            var inputs = new Buttons[players];
            var stocks = new int[players];
            var knockedAbout = new bool[players]; // hit or parried since it last stood in control
            for (int p = 0; p < players; p++)
            {
                levels[p] = levelOf(start, p);
                stocks[p] = s.Fighters[p].Stocks;
            }

            for (int f = 0; f < SoakFrames; f++)
            {
                for (int p = 0; p < players; p++) inputs[p] = SparringBot.Think(s, Soak, p, levels[p]);
                Simulation.Advance(s, Soak, inputs);
                for (int p = 0; p < players; p++)
                {
                    ref readonly Fighter x = ref s.Fighters[p];
                    if (x.Stocks < stocks[p])
                    {
                        Assert.True(knockedAbout[p],
                            $"{name}, {players} players, start {start}: P{p + 1} lost a stock on frame {f} without being hit since it last stood on a platform");
                        stocks[p] = x.Stocks;
                        knockedAbout[p] = false;
                        knockouts++;
                    }

                    if (x.Action is FighterAction.Hitstun or FighterAction.Stunned) knockedAbout[p] = true;
                    else if (x.Grounded && x.Action == FighterAction.Idle && Fix.Abs(x.Velocity.X) <= Soak.Tuning.RunSpeed)
                        knockedAbout[p] = false;
                }
            }
        }
        Assert.True(knockouts >= 50, $"{name}: only {knockouts} knockouts in the free-for-alls");
    }

    [Fact]
    public void CpuMatchesUseTheWholeMoveSet()
    {
        // What a person sparring with the CPU should see it do: dodge and parry, punish, throw
        // heavies, recover with the up heavy and the air dodge. Mixed levels, several starts.
        int dodges = 0, airDodges = 0, parries = 0, heavies = 0, upHeavies = 0;
        for (int start = 0; start < 6; start++)
        {
            GameState s = Simulation.CreateInitialState(Rules, 2, 1);
            s.Frame = start * 101;
            var inputs = new Buttons[2];
            for (int f = 0; f < 3 * 60 * 60 && !s.MatchOver; f++)
            {
                inputs[0] = SparringBot.Think(s, Rules, 0, AllLevels[start % 3]);
                inputs[1] = SparringBot.Think(s, Rules, 1, CpuLevel.Hard);
                Fighter[] before = (Fighter[])s.Fighters.Clone();
                Simulation.Advance(s, Rules, inputs);
                for (int p = 0; p < 2; p++)
                {
                    Fighter x = s.Fighters[p];
                    bool started = x.ActionFrame == 0 && before[p].Action == FighterAction.Idle;
                    if (x.Action == FighterAction.Dodge && started)
                    {
                        dodges++;
                        if (!before[p].Grounded) airDodges++;
                    }
                    if (x.Action == FighterAction.Stunned && before[p].Action != FighterAction.Stunned) parries++;
                    if (x.Action == FighterAction.Attack && started && x.Move is MoveId.NeutralHeavy or MoveId.SideHeavy) heavies++;
                    if (x.Action == FighterAction.Attack && started && x.Move == MoveId.UpHeavy) upHeavies++;
                }
            }
        }
        Assert.True(dodges > 0 && airDodges > 0 && parries > 0 && heavies > 0 && upHeavies > 0,
            $"dodges {dodges} (air {airDodges}), parries {parries}, heavies {heavies}, up heavies {upHeavies}");
    }

    [Theory, MemberData(nameof(Levels))]
    public void ComesDownFromAPlatformToReachAnOpponentBelow(CpuLevel level)
    {
        // The CPU stands on the right platform; P1 stands still on the main stage underneath it.
        // There is no drop-through, so the CPU must walk off the end to get at P1.
        foreach (int p1X in new[] { 2, 3, 4, 5, 6, 7 })
        {
            GameState s = Standing(p1X, 4);
            ref Fighter cpu = ref s.Fighters[1];
            cpu.Position = new FixVec2(Fix.FromRatio(7, 2), Fix.FromRatio(11, 4));
            cpu.Grounded = true;

            var inputs = new Buttons[2];
            int f = 0;
            for (; f < 600 && s.Fighters[0].Josh == 0; f++)
            {
                inputs[1] = SparringBot.Think(s, Rules, 1, level);
                Simulation.Advance(s, Rules, inputs);
            }
            Assert.True(s.Fighters[0].Josh > 0, $"{level}: P1 at x={p1X} under the platform was never hit");
        }
    }

    [Theory, MemberData(nameof(Levels))]
    public void ReachesAnOpponentStandingOnTheVeryEdge(CpuLevel level)
    {
        // P1 stands still right at the stage edge. Wherever the CPU comes to rest near its chase
        // limit (1 m in from the edge), the jab must still reach.
        foreach (int cpuX100 in new[] { 760, 770, 775, 778, 780, 790, 800 })
        foreach (int side in new[] { 1, -1 })
        {
            GameState s = Standing(0, 0);
            ref Fighter p1 = ref s.Fighters[0];
            p1.Position = new FixVec2(Fix.FromInt(9 * side), Fix.Zero);
            ref Fighter cpu = ref s.Fighters[1];
            cpu.Position = new FixVec2(Fix.FromRatio(cpuX100 * side, 100), Fix.Zero);
            cpu.Facing = side;

            var inputs = new Buttons[2];
            for (int f = 0; f < 300 && s.Fighters[0].Josh == 0; f++)
            {
                inputs[1] = SparringBot.Think(s, Rules, 1, level);
                Simulation.Advance(s, Rules, inputs);
            }
            Assert.True(s.Fighters[0].Josh > 0, $"{level}: CPU resting at x = {cpuX100 * side} cm never hit P1 at the edge");
        }
    }

    [Fact]
    public void ARecoveringCpuThatParriesStillMakesItBack()
    {
        // The CPU is below the stage lip with only its air dodge left; an opponent at the edge jabs
        // around the moment it air-dodges home. Being parried must never cost the CPU its stock.
        int parries = 0;
        foreach (int xCm in new[] { -1000, -960, -930 })
        foreach (int yCm in new[] { -120, -60, -20 })
        for (int offset = -6; offset <= 6; offset++)
        {
            GameState probe = Recovering(xCm, yCm);
            int dodgeAt = -1;
            var inputs = new Buttons[2];
            for (int f = 0; f < 60 && dodgeAt < 0; f++)
            {
                inputs[1] = SparringBot.Think(probe, Rules, 1);
                if ((inputs[1] & Buttons.Dodge) != 0) dodgeAt = f;
                Simulation.Advance(probe, Rules, inputs);
            }
            if (dodgeAt < 0) continue;

            GameState s = Recovering(xCm, yCm);
            bool hit = false, parried = false;
            for (int f = 0; f < 400 && s.Fighters[1].Stocks == Rules.Stocks; f++)
            {
                inputs[0] = f == dodgeAt + offset ? Buttons.Light : Buttons.None;
                inputs[1] = SparringBot.Think(s, Rules, 1);
                Simulation.Advance(s, Rules, inputs);
                if (s.Fighters[1].Action == FighterAction.Hitstun) hit = true;
                if (s.Fighters[0].Action == FighterAction.Stunned) parried = true;
                if (s.Fighters[1].Grounded && s.Fighters[1].Action == FighterAction.Idle) break; // back on the stage
            }
            if (parried) parries++;
            Assert.False(s.Fighters[1].Stocks < Rules.Stocks && !hit,
                $"CPU at ({xCm}, {yCm}) cm, jab {offset:+0;-0;0} frames from its dodge: lost the stock without being hit (parried: {parried})");
        }
        Assert.True(parries > 0, "no parries happened, so the case was never tested");
    }

    /// <summary>P2 (the CPU) under the left edge of the stage, falling, with only its air dodge left; P1 at the edge facing it.</summary>
    private static GameState Recovering(int xCm, int yCm)
    {
        GameState s = Standing(0, 0);
        ref Fighter p1 = ref s.Fighters[0];
        p1.Position = new FixVec2(Fix.FromRatio(-79, 10), Fix.Zero);
        p1.Facing = -1;
        ref Fighter cpu = ref s.Fighters[1];
        cpu.Position = new FixVec2(Fix.FromRatio(xCm, 100), Fix.FromRatio(yCm, 100));
        cpu.Velocity = new FixVec2(Fix.Zero, Fix.FromRatio(-1, 10));
        cpu.Grounded = false;
        cpu.AirJumpsLeft = 0;
        cpu.RecoveryUsed = true;
        return s;
    }

    [Fact]
    public void CpuInputsSurviveRollback()
    {
        // P1 mashes pseudo-random buttons; P2-P4 are CPUs at different levels, so their inputs
        // depend on the state. Re-simulating from a saved state after a misprediction must
        // reproduce the original timeline exactly, CPU decisions included.
        const int frames = 60 * 60;
        CpuLevel[] levels = { CpuLevel.Hard, CpuLevel.Easy, CpuLevel.Normal, CpuLevel.Hard };

        GameState reference = Simulation.CreateInitialState(Rules, 4, 9);
        var checksums = new ulong[frames];
        var inputs = new Buttons[4];
        for (int f = 0; f < frames; f++)
        {
            FillInputs(reference, f, 3, levels, inputs);
            Simulation.Advance(reference, Rules, inputs);
            checksums[f] = reference.Checksum();
        }

        GameState live = Simulation.CreateInitialState(Rules, 4, 9);
        const int rollback = 8;
        for (int f = 0; f < frames; f++)
        {
            if (f % 10 == 0 && f + rollback < frames)
            {
                GameState saved = live.Clone();
                for (int k = 0; k < rollback; k++)
                {
                    FillInputs(live, f + k, 999, levels, inputs); // wrong guess for P1
                    Simulation.Advance(live, Rules, inputs);
                }
                live.CopyFrom(saved);
            }
            FillInputs(live, f, 3, levels, inputs);
            Simulation.Advance(live, Rules, inputs);
            Assert.Equal(checksums[f], live.Checksum());
        }
    }

    private static void FillInputs(GameState s, int frame, ulong seed, CpuLevel[] levels, Buttons[] inputs)
    {
        var rng = new SimRandom(seed ^ ((ulong)frame * 0x9E3779B97F4A7C15UL));
        inputs[0] = (Buttons)(rng.NextUInt() & 0x7FF);
        for (int p = 1; p < inputs.Length; p++) inputs[p] = SparringBot.Think(s, Rules, p, levels[p]);
    }

    /// <summary>
    /// A scripted player who never attacks: runs to one stage edge, then the other, jumps every
    /// 50 frames (so it uses the platforms), and every fourth lap runs off the edge and back.
    /// </summary>
    private static Buttons Wanderer(GameState s, int frame)
    {
        ref readonly Fighter me = ref s.Fighters[0];
        if (!me.IsActive) return Buttons.None;

        int lap = frame / 240;
        bool goRight = lap % 2 == 0;
        Fix edge = lap % 4 == 3 ? Fix.FromRatio(19, 2) : Fix.FromInt(7); // 9.5 m is past the edge
        Fix targetX = goRight ? edge : -edge;

        Buttons b = Buttons.None;
        bool offStage = !me.Grounded && me.Position.Y < Fix.Zero;
        if (offStage)
        {
            b |= me.Position.X < Fix.Zero ? Buttons.Right : Buttons.Left;
            if (me.Velocity.Y < Fix.Zero && (me.PreviousButtons & Buttons.Jump) == 0) b |= Buttons.Jump;
            return b;
        }

        if (me.Position.X < targetX - Fix.Half) b |= Buttons.Right;
        else if (me.Position.X > targetX + Fix.Half) b |= Buttons.Left;
        if (frame % 50 == 0) b |= Buttons.Jump;
        return b;
    }
}
