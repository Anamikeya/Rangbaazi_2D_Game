using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim.Tests;

/// <summary>
/// Room rules: up to 15 players, the timed free-for-all (respawns, KO credit, winner by KOs),
/// rage (stronger hits the more damage you've taken) and the hidden second wind after a respawn.
/// </summary>
public class RoomRulesTests
{
    private static MatchRules Timed(int frames, int rage = 0, int shield = 0) => new()
    {
        Stage = TestStages.Wide,
        TimeLimitFrames = frames,
        RagePercent = rage,
        RespawnShield = shield,
    };

    /// <summary>Fighters standing on the floor, P1 facing right, the rest left.</summary>
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

    private static void Step(GameState s, MatchRules rules, params Buttons[] inputs)
    {
        var all = new Buttons[s.Fighters.Length];
        inputs.CopyTo(all, 0);
        Simulation.Advance(s, rules, all);
    }

    /// <summary>P1 jabs P2 from 0.6 m and the frames play out until the hit lands.</summary>
    private static void LandJab(GameState s, MatchRules rules)
    {
        Step(s, rules, Buttons.Light);
        for (int i = 0; i < 6; i++) Step(s, rules);
    }

    private static void RingOut(GameState s, int index)
    {
        s.Fighters[index].Position = new FixVec2(Fix.FromInt(5000), Fix.Zero);
    }

    [Fact]
    public void RoomsHoldUpToSixteenFighters()
    {
        var rules = MatchRules.Room(Stages.Rooftops, 10);
        var state = Simulation.CreateInitialState(rules, 15, 1);
        var inputs = new Buttons[15];
        for (int f = 0; f < 600; f++)
        {
            for (int p = 0; p < 15; p++) inputs[p] = (f + p) % 40 < 20 ? Buttons.Right | Buttons.Light : Buttons.Left | Buttons.Heavy;
            Simulation.Advance(state, rules, inputs);
        }
        Assert.Equal(600, state.Frame);
        Assert.Throws<ArgumentOutOfRangeException>(() => Simulation.CreateInitialState(rules, Simulation.MaxFighters + 1, 1));
    }

    [Fact]
    public void EveryArenaIsPlayable()
    {
        Assert.Equal(31, ArenaStages.All.Count);
        Assert.Equal(ArenaStages.All.Count, ArenaStages.All.Select(s => s.Id).Distinct().Count());
        foreach (StageDefinition stage in ArenaStages.All)
        {
            var rules = MatchRules.Room(stage, 10);
            var state = Simulation.CreateInitialState(rules, 15, 1);
            var idle = new Buttons[15];
            for (int f = 0; f < 120; f++) Simulation.Advance(state, rules, idle);
            for (int i = 0; i < 15; i++)
            {
                Assert.True(state.Fighters[i].Grounded, $"{stage.Id}: fighter {i} should land after spawning");
                Assert.Equal(0, state.Fighters[i].Falls);
            }
            foreach (var p in stage.Platforms) Assert.True(stage.BlastZone.Contains(new FixVec2(p.MinX, p.MaxY)), $"{stage.Id}: platform outside the blast zone");
        }
    }

    [Fact]
    public void RoomRulesMatchTheDesign()
    {
        var rules = MatchRules.Room(Stages.Rooftops, 10);
        Assert.Equal(10 * 60 * 60, rules.TimeLimitFrames);
        Assert.Equal(50, rules.RagePercent);
        Assert.Equal(20, rules.RespawnShield);
        Assert.Equal("rooftops", rules.Stage.Id);
    }

    [Fact]
    public void TimedMatchesNeverEliminateAndEndOnTheClock()
    {
        var rules = Timed(400);
        var s = Standing(rules, 0, 300);
        for (int i = 0; i < 3; i++)
        {
            RingOut(s, 1);
            Step(s, rules);
            Assert.Equal(FighterAction.Respawning, s.Fighters[1].Action);
            for (int f = 0; f < rules.RespawnFrames; f++) Step(s, rules);
            Assert.True(s.Fighters[1].IsActive);
        }
        Assert.Equal(3, s.Fighters[1].Falls);
        Assert.False(s.MatchOver);

        while (s.Frame < 400) Step(s, rules);
        Assert.True(s.MatchOver);
        Assert.Equal(0, s.Fighters[0].Falls);
        Assert.Equal(0, s.WinnerIndex); // level on KOs, fewer falls
    }

    [Fact]
    public void TheLastAttackerGetsTheKoWithinTheWindow()
    {
        var rules = Timed(100_000);
        var s = Standing(rules, 0, 60);
        LandJab(s, rules);
        Assert.Equal(1, s.Fighters[1].LastHitBy);
        RingOut(s, 1);
        Step(s, rules);
        Assert.Equal(1, s.Fighters[0].Kos);
        Assert.Equal(1, s.Fighters[1].Falls);
    }

    [Fact]
    public void AnOldHitOrALoneFallScoresNoKo()
    {
        var rules = Timed(100_000);
        var s = Standing(rules, 0, 60);
        LandJab(s, rules);
        while (s.Fighters[1].LastHitTimer > 0) Step(s, rules);
        RingOut(s, 1);
        Step(s, rules);
        Assert.Equal(0, s.Fighters[0].Kos);

        RingOut(s, 0);
        Step(s, rules);
        Assert.Equal(0, s.Fighters[1].Kos);
        Assert.Equal(1, s.Fighters[0].Falls);
    }

    [Fact]
    public void TheWinnerHasTheMostKosAndATieHasNoWinner()
    {
        var fighters = new Fighter[3];
        fighters[0].Kos = 2;
        fighters[1].Kos = 3;
        fighters[1].Falls = 4;
        fighters[2].Kos = 3;
        fighters[2].Falls = 1;
        Assert.Equal(2, Simulation.TimedWinner(fighters));
        fighters[2].Falls = 4;
        Assert.Equal(-1, Simulation.TimedWinner(fighters));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(75, 125)]
    [InlineData(150, 150)]
    [InlineData(299, 150)]
    public void RageGrowsWithTheAttackersJosh(int josh, int expected)
    {
        Assert.Equal(expected, Simulation.RageMultiplier(josh, Timed(1, rage: 50)));
        Assert.Equal(100, Simulation.RageMultiplier(josh, Timed(1)));
    }

    [Fact]
    public void AnAttackerCloseToDeathHitsHarder()
    {
        var calm = Timed(100_000, rage: 50);
        var a = Standing(calm, 0, 60);
        LandJab(a, calm);

        var b = Standing(calm, 0, 60);
        b.Fighters[0].Josh = 150;
        LandJab(b, calm);

        int jab = calm.Tuning.Moves[MoveId.Jab].Damage;
        Assert.Equal(jab, a.Fighters[1].Josh);
        Assert.Equal(jab * 150 / 100, b.Fighters[1].Josh);
        Assert.True(Fix.Abs(b.Fighters[1].Velocity.X) > Fix.Abs(a.Fighters[1].Velocity.X));
    }

    [Fact]
    public void TheSecondWindSoaksDamageAfterARespawnOnly()
    {
        var rules = Timed(100_000, shield: 20);
        var s = Standing(rules, 0, 60);
        Assert.Equal(0, s.Fighters[1].Shield); // not at the start of the match

        RingOut(s, 1);
        Step(s, rules);
        for (int f = 0; f < rules.RespawnFrames; f++) Step(s, rules);
        Assert.Equal(20, s.Fighters[1].Shield);

        // Put the respawned fighter back in jab range and hit it: the shield takes the damage.
        s.Fighters[1].Position = new FixVec2(Fix.FromRatio(60, 100), Fix.Zero);
        s.Fighters[1].Velocity = FixVec2.Zero;
        s.Fighters[1].Grounded = true;
        LandJab(s, rules);
        int jab = rules.Tuning.Moves[MoveId.Jab].Damage;
        Assert.Equal(0, s.Fighters[1].Josh);
        Assert.Equal(20 - jab, s.Fighters[1].Shield);
    }

    [Fact]
    public void RoomMatchesRollBackExactly()
    {
        var rules = MatchRules.Room(Stages.Rooftops, 1);
        const int players = 15, frames = 1500;
        var reference = Simulation.CreateInitialState(rules, players, 5);
        var checksums = new ulong[frames];
        var inputs = new Buttons[players];
        Buttons Input(int f, int p) => (Buttons)(new SimRandom(((ulong)f << 8) ^ (ulong)p ^ 0xABCDUL).NextUInt() & 0x7FF);
        for (int f = 0; f < frames; f++)
        {
            for (int p = 0; p < players; p++) inputs[p] = Input(f, p);
            Simulation.Advance(reference, rules, inputs);
            checksums[f] = reference.Checksum();
        }

        var replay = Simulation.CreateInitialState(rules, players, 5);
        GameState? saved = null;
        for (int f = 0; f < frames; f++)
        {
            if (f == 700) saved = replay.Clone();
            for (int p = 0; p < players; p++) inputs[p] = Input(f, p);
            Simulation.Advance(replay, rules, inputs);
        }
        replay.CopyFrom(saved!);
        for (int f = 700; f < frames; f++)
        {
            for (int p = 0; p < players; p++) inputs[p] = Input(f, p);
            Simulation.Advance(replay, rules, inputs);
            Assert.Equal(checksums[f], replay.Checksum());
        }
        Assert.True(reference.Fighters.Sum(f => f.Kos) > 0, "the random match should score some KOs");
    }
}
