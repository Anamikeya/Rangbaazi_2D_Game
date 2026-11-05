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
    public void LevelOnlyChangesJabTiming()
    {
        // Same state, every level: identical buttons whenever the bot isn't deciding to jab.
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
                    Assert.Equal(hard & ~Buttons.Light, other & ~Buttons.Light);
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
    /// Three- and four-CPU free-for-alls that check how every lost stock started. A self-destruct
    /// is a CPU leaving the main stage under its own control (not in hitstun, not hit in the last
    /// second, not carried faster than it can run) and then losing the stock without being hit
    /// again. Time since the last hit can't tell these apart from real knockouts, which can take
    /// over two seconds to fall out of the blast zone.
    /// </summary>
    private static void AssertNoSelfDestructs(string name, Func<int, int, CpuLevel> levelOf)
    {
        FixRect main = Soak.Stage.Platforms[0];
        int knockouts = 0;
        foreach (int players in new[] { 3, 4 })
        for (int start = 0; start < 8; start++)
        {
            GameState s = Simulation.CreateInitialState(Soak, players, 1);
            s.Frame = start * 7919; // the sim doesn't use the seed yet; this varies the bots instead
            var levels = new CpuLevel[players];
            var inputs = new Buttons[players];
            var lastHit = new int[players];
            var stocks = new int[players];
            var wasOff = new bool[players];
            var leftByItself = new bool[players];
            for (int p = 0; p < players; p++)
            {
                levels[p] = levelOf(start, p);
                lastHit[p] = -1000;
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
                        Assert.False(leftByItself[p],
                            $"{name}, {players} players, start {start}: P{p + 1} walked or jumped off the stage and lost a stock on frame {f}");
                        stocks[p] = x.Stocks;
                        knockouts++;
                    }

                    if (x.Action == FighterAction.Hitstun)
                    {
                        lastHit[p] = f;
                        leftByItself[p] = false;
                    }

                    bool off = x.IsActive && (x.Position.X < main.MinX || x.Position.X > main.MaxX
                        || (!x.Grounded && x.Position.Y < main.MaxY));
                    if (off && !wasOff[p])
                    {
                        leftByItself[p] = x.Action != FighterAction.Hitstun && f - lastHit[p] > 60
                            && Fix.Abs(x.Velocity.X) <= Soak.Tuning.RunSpeed;
                    }
                    if (!off) leftByItself[p] = false;
                    wasOff[p] = off;
                }
            }
        }
        Assert.True(knockouts >= 50, $"{name}: only {knockouts} knockouts in the free-for-alls");
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
