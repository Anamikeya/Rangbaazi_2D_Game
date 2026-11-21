using Rangbaazi.Sim.Diagnostics;
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

    // ---- Teams (rooms v1, section 1) ----

    private static Fighter[] Scores(params (int kos, int falls)[] scores)
    {
        var fighters = new Fighter[scores.Length];
        for (int i = 0; i < scores.Length; i++)
        {
            fighters[i].Kos = scores[i].kos;
            fighters[i].Falls = scores[i].falls;
        }
        return fighters;
    }

    private static MatchRules WithTeams(params int[] teams) => new() { Stage = TestStages.Wide, TimeLimitFrames = 100_000, Teams = teams };

    [Fact]
    public void Team_kos_add_up_and_the_winner_is_the_best_teams_mvp()
    {
        // Team 1: 3 + 3 = 6 KOs; team 2: 5 + 0 = 5. Fighter 2 has the most KOs alone, but team 1 wins,
        // and its MVP is the one of its two with fewer falls.
        Fighter[] f = Scores((3, 4), (5, 0), (3, 1), (0, 2));
        MatchRules rules = WithTeams(1, 2, 1, 2);
        Assert.True(rules.HasTeams());
        Assert.Equal(1, Simulation.TimedWinner(f));
        Assert.Equal(2, Simulation.TimedTeamWinner(f, rules));

        // Through the match itself: the clock runs out with those scores.
        var timed = new MatchRules { Stage = TestStages.Wide, TimeLimitFrames = 10, Teams = new[] { 1, 2, 1, 2 } };
        GameState s = Standing(timed, -300, -100, 100, 300);
        for (int i = 0; i < 4; i++) (s.Fighters[i].Kos, s.Fighters[i].Falls) = (f[i].Kos, f[i].Falls);
        while (!s.MatchOver) Step(s, timed);
        Assert.Equal(2, s.WinnerIndex);
    }

    [Fact]
    public void Level_team_kos_go_to_fewer_falls_then_a_draw()
    {
        MatchRules rules = WithTeams(1, 2, 1, 2);
        Fighter[] f = Scores((2, 3), (4, 1), (2, 0), (0, 1)); // 4 KOs each; falls 3 v 2
        Assert.Equal(1, Simulation.TimedTeamWinner(f, rules));
        f[0].Falls = 2;                                        // 4 KOs and 2 falls each: a draw
        Assert.Equal(-1, Simulation.TimedTeamWinner(f, rules));

        // Three teams: two level at the top is a draw, even with a worse third.
        MatchRules three = WithTeams(1, 2, 3);
        Assert.Equal(-1, Simulation.TimedTeamWinner(Scores((2, 1), (2, 1), (0, 0)), three));
        Assert.Equal(2, Simulation.TimedTeamWinner(Scores((2, 1), (2, 1), (3, 5)), three));
    }

    [Fact]
    public void Mvp_ties_go_to_the_lower_index()
    {
        MatchRules rules = WithTeams(2, 1, 2, 1, 2);
        Fighter[] f = Scores((1, 1), (0, 3), (2, 1), (0, 0), (2, 1));
        Assert.Equal(2, Simulation.TimedTeamWinner(f, rules)); // 2 and 4 are level: the lower index
        f[4].Falls = 0;
        Assert.Equal(4, Simulation.TimedTeamWinner(f, rules));
    }

    [Fact]
    public void With_no_teams_the_timed_winner_is_unchanged()
    {
        var random = new SimRandom(77);
        for (int round = 0; round < 500; round++)
        {
            int n = 2 + (int)(random.NextUInt() % 14);
            var f = new Fighter[n];
            for (int i = 0; i < n; i++)
            {
                f[i].Kos = (int)(random.NextUInt() % 4);
                f[i].Falls = (int)(random.NextUInt() % 4);
            }
            int expected = Simulation.TimedWinner(f);
            Assert.Equal(expected, Simulation.TimedTeamWinner(f, new MatchRules { Stage = TestStages.Wide }));
            Assert.Equal(expected, Simulation.TimedTeamWinner(f, WithTeams(new int[n])));
            Assert.False(WithTeams(new int[n]).HasTeams());
        }
        Assert.False(new MatchRules { Stage = TestStages.Wide }.HasTeams());
    }

    [Fact]
    public void A_teammate_never_gets_ring_out_credit()
    {
        MatchRules rules = WithTeams(1, 1, 2);
        GameState s = Standing(rules, 0, 300, 600);
        s.Fighters[1].LastHitBy = 1; // fighter 0, a teammate, however it came about
        s.Fighters[1].LastHitTimer = 100;
        s.Fighters[2].LastHitBy = 1; // and the enemy too
        s.Fighters[2].LastHitTimer = 100;
        RingOut(s, 1);
        RingOut(s, 2);
        Step(s, rules);
        Assert.Equal(1, s.Fighters[0].Kos);   // only the enemy's fall scores
        Assert.Equal(1, s.Fighters[1].Falls); // the teammate's fall still counts
        Assert.Equal(1, s.Fighters[2].Falls);
    }

    [Fact]
    public void A_bat_does_not_return_a_teammates_shot()
    {
        // 0 fires along the floor; teammate 1 swings a bat in its path; the enemy 2 stands behind.
        var rules = new MatchRules { Stage = TestStages.Wide, Weapons = true, Teams = new[] { 1, 1, 2 } };
        GameState s = Standing(rules, 0, 300, 600);
        s.PropTimer = 1_000_000;
        s.Fighters[0].Weapon = WeaponId.Uzi;
        s.Fighters[0].WeaponSupply = 1;
        s.Fighters[1].Weapon = WeaponId.BaseballBat;
        s.Fighters[1].WeaponSupply = Weapons.Get(WeaponId.BaseballBat).Supply;
        Step(s, rules, Buttons.None, Buttons.Light); // the batter starts swinging first ...
        for (int i = 0; i < 8; i++) Step(s, rules);
        Step(s, rules, Buttons.Light);               // ... so the bullet meets the bat as it comes round
        for (int i = 0; i < 60; i++) Step(s, rules);
        Assert.Equal(0, s.Fighters[0].Josh);
        Assert.Equal(0, s.Fighters[1].Josh);
        Assert.Equal(2, s.Fighters[2].Josh);         // it flew on through the swing and hit the enemy

        // With no teams the same swing returns it to its shooter.
        var free = new MatchRules { Stage = TestStages.Wide, Weapons = true };
        GameState t = Standing(free, 0, 300, 600);
        t.PropTimer = 1_000_000;
        t.Fighters[0].Weapon = WeaponId.Uzi;
        t.Fighters[0].WeaponSupply = 1;
        t.Fighters[1].Weapon = WeaponId.BaseballBat;
        t.Fighters[1].WeaponSupply = Weapons.Get(WeaponId.BaseballBat).Supply;
        Step(t, free, Buttons.None, Buttons.Light);
        for (int i = 0; i < 8; i++) Step(t, free);
        Step(t, free, Buttons.Light);
        for (int i = 0; i < 60; i++) Step(t, free);
        Assert.Equal(2, t.Fighters[0].Josh);
        Assert.Equal(0, t.Fighters[2].Josh);
    }

    [Fact]
    public void A_four_team_cpu_room_ends_with_the_best_teams_mvp()
    {
        int[] teams = { 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4 };
        var rules = MatchRules.Room(Stages.Rooftops, 1, null, teams);
        var s = Simulation.CreateInitialState(rules, 15, 9);
        var inputs = new Buttons[15];
        for (int f = 0; f < rules.TimeLimitFrames; f++)
        {
            for (int p = 0; p < 15; p++) inputs[p] = SparringBot.Think(s, rules, p, CpuLevel.Normal);
            Simulation.Advance(s, rules, inputs);
        }
        Assert.True(s.MatchOver);
        Assert.Equal(Simulation.TimedTeamWinner(s.Fighters, rules), s.WinnerIndex);
        Assert.True(s.Fighters.Sum(f => f.Kos) > 0, "the CPUs should score");
        Assert.True(s.Fighters.Sum(f => f.Kos) <= s.Fighters.Sum(f => f.Falls)); // every KO is someone's fall
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
