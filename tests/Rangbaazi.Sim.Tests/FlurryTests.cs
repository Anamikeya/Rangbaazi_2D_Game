using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim.Tests;

/// <summary>
/// The fist flurry (combat spec v0.2, built in v0.3): empty-handed, Light and Heavy on the same
/// frame start one of four combos at random, with equal odds.
/// </summary>
public class FlurryTests
{
    private static readonly MatchRules Rules = new() { Stage = TestStages.Wide, Flurry = true };
    private const Buttons Both = Buttons.Light | Buttons.Heavy;

    private static GameState Standing(ulong seed)
    {
        var s = Simulation.CreateInitialState(Rules, 2, seed);
        for (int i = 0; i < 2; i++)
        {
            ref Fighter f = ref s.Fighters[i];
            f.Position = new FixVec2(Fix.FromInt(i * 50), Fix.Zero);
            f.Velocity = FixVec2.Zero;
            f.Grounded = true;
        }
        return s;
    }

    private static void Step(GameState s, MatchRules rules, Buttons p1) => Simulation.Advance(s, rules, new[] { p1, Buttons.None });

    /// <summary>The moves a flurry plays, in order, from the press until the fighter is idle again.</summary>
    private static List<MoveId> Play(ulong seed)
    {
        GameState s = Standing(seed);
        Step(s, Rules, Both);
        var moves = new List<MoveId> { s.Fighters[0].Move };
        for (int i = 0; i < 200 && s.Fighters[0].Action == FighterAction.Attack; i++)
        {
            int frame = s.Fighters[0].ActionFrame;
            Step(s, Rules, Buttons.None);
            if (s.Fighters[0].Action == FighterAction.Attack && s.Fighters[0].ActionFrame < frame) moves.Add(s.Fighters[0].Move);
        }
        return moves;
    }

    [Fact]
    public void BothButtonsStartOneOfFourCombosWithEqualOdds()
    {
        var counts = new Dictionary<string, int>();
        for (ulong seed = 1; seed <= 400; seed++)
        {
            string combo = string.Join("+", Play(seed));
            counts[combo] = counts.GetValueOrDefault(combo) + 1;
        }
        Assert.Equal(new[] { "Jab", "Jab+NeutralHeavy", "NeutralHeavy", "NeutralHeavy+NeutralHeavy" }, counts.Keys.Order().ToArray());
        Assert.All(counts.Values, n => Assert.InRange(n, 70, 130)); // 100 each, give or take
    }

    [Fact]
    public void TheSecondHitStartsTheFrameAfterTheFirstEndsAndCanHitAgain()
    {
        // Find a seed that rolls the jab-then-heavy combo, then watch the hand-over.
        ulong seed = Enumerable.Range(1, 100).Select(i => (ulong)i).First(sd => string.Join("+", Play(sd)) == "Jab+NeutralHeavy");
        GameState s = Standing(seed);
        s.Fighters[1].Position = new FixVec2(Fix.One, Fix.Zero);
        s.Fighters[0].Facing = 1;
        Step(s, Rules, Both);
        int jab = Rules.Tuning.Moves.Jab.TotalFrames;
        for (int i = 0; i < jab - 1; i++) Step(s, Rules, Buttons.None);
        Assert.Equal(MoveId.Jab, s.Fighters[0].Move);
        while (s.Fighters[0].HitStop > 0 || s.Fighters[0].Move == MoveId.Jab) Step(s, Rules, Buttons.Dodge); // dodge can't cut in
        Assert.Equal(FighterAction.Attack, s.Fighters[0].Action);
        Assert.Equal(MoveId.NeutralHeavy, s.Fighters[0].Move);
        Assert.Equal(0, s.Fighters[0].ActionFrame);
        Assert.False(s.Fighters[0].AttackConnected); // the heavy is a new hit
    }

    [Fact]
    public void UpWithBothButtonsIsStillTheRecovery()
    {
        GameState s = Standing(1);
        Step(s, Rules, Both | Buttons.Up);
        Assert.Equal(MoveId.UpHeavy, s.Fighters[0].Move);
    }

    [Fact]
    public void ArmedBothButtonsFireTheWeaponOnce()
    {
        GameState s = Standing(1);
        s.Fighters[0].Weapon = WeaponId.Belan;
        s.Fighters[0].WeaponSupply = 5;
        Step(s, Rules, Both);
        Assert.Equal(MoveId.BelanBonk, s.Fighters[0].Move);
        Assert.Equal(MoveId.None, s.Fighters[0].FlurryNext);
    }

    [Fact]
    public void WithTheFlurryOffBothButtonsAreTheHeavy()
    {
        var rules = new MatchRules { Stage = TestStages.Wide };
        GameState s = Simulation.CreateInitialState(rules, 2, 1);
        ulong random = s.Random.State;
        s.Fighters[0].Grounded = true;
        Step(s, rules, Both);
        Assert.Equal(MoveId.NeutralHeavy, s.Fighters[0].Move);
        Assert.Equal(random, s.Random.State); // and the match's random stream isn't touched
    }

    [Fact]
    public void BeingHitCancelsTheQueuedSecondHit()
    {
        ulong seed = Enumerable.Range(1, 100).Select(i => (ulong)i).First(sd => string.Join("+", Play(sd)) == "NeutralHeavy+NeutralHeavy");
        GameState s = Standing(seed);
        s.Fighters[1].Position = new FixVec2(Fix.One, Fix.Zero);
        s.Fighters[1].Facing = -1;
        Simulation.Advance(s, Rules, new[] { Both, Buttons.Light }); // P2 jabs first and lands it
        for (int i = 0; i < 8; i++) Simulation.Advance(s, Rules, new Buttons[2]);
        Assert.Equal(FighterAction.Hitstun, s.Fighters[0].Action);
        Assert.Equal(MoveId.None, s.Fighters[0].FlurryNext);
    }
}
