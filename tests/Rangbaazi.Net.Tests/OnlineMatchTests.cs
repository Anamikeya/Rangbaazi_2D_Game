using Rangbaazi.Net.Transport;
using Rangbaazi.Sim;

namespace Rangbaazi.Net.Tests;

/// <summary>
/// Two online matches over the in-memory network, played by bots: they connect, stay in step
/// through latency, jitter and loss, match an offline replay of their confirmed inputs, and end
/// cleanly in every way a match can end.
/// </summary>
[Collection(nameof(OnlineMatchTests))] // real-time; keep these off each other's CPU time
public class OnlineMatchTests
{
    private const int Frames = 600;

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(2, 0, 0)]
    [InlineData(3, 2, 5)]
    [InlineData(6, 3, 15)]
    public void BotsPlayInStepThroughLatencyJitterAndLoss(int latency, int jitter, int loss)
    {
        var network = new InMemoryNetwork(seed: (ulong)(latency * 100 + loss + 1)) { LatencyTicks = latency, JitterTicks = jitter, LossPercent = loss };
        using var pair = new PeerPair(network);
        bool reached = pair.RunUntil(() => (pair.A.FinalFrame >= Frames && pair.B.FinalFrame >= Frames) || pair.A.Phase == OnlinePhase.Ended || pair.B.Phase == OnlinePhase.Ended, 20000);
        Assert.True(reached && pair.BothRunning, $"A {pair.A.Phase} {pair.A.EndMessage} frame {pair.A.FinalFrame}; B {pair.B.Phase} {pair.B.EndMessage} frame {pair.B.FinalFrame}\n{pair.Logs()}");

        OnlineMatch a = pair.A, b = pair.B;
        Assert.Equal(a.Seed, b.Seed);
        Assert.NotEqual(0UL, a.Seed);
        Assert.True(a.ChecksumsVerified > 0 && b.ChecksumsVerified > 0);
        Assert.Equal(-1, a.DesyncFrame);

        // Both sides agree on a common final frame, and an offline replay of the confirmed inputs gets there too.
        int common = Math.Min(a.FinalFrame, b.FinalFrame);
        Assert.True(a.Checksums!.TryGet(common, out ulong ca));
        Assert.True(b.Checksums!.TryGet(common, out ulong cb));
        Assert.Equal(ca, cb);
        Assert.Equal(ca, a.ConfirmedInputs!.Replay(a.Rules, a.Seed, common).Checksum());
        for (int f = 0; f < common; f++) Assert.True(a.ConfirmedInputs[f].SequenceEqual(b.ConfirmedInputs![f]), $"inputs differ at frame {f}");

        // The bots really fought, and rollbacks stayed inside the prediction window.
        GameState replay = a.ConfirmedInputs.Replay(a.Rules, a.Seed, common);
        Assert.True(replay.Fighters.Sum(f => f.Josh) > 0 || replay.Fighters.Any(f => f.Stocks < a.Rules.Stocks), "nobody got hit");
        int predictionFrames = a.Options.PredictionFrames;
        Assert.InRange(a.Rollbacks.Max, 0, predictionFrames);
        Assert.InRange(b.Rollbacks.Max, 0, predictionFrames);
        if (latency > 0) Assert.True(a.Rollbacks.Count + b.Rollbacks.Count > 0, "latency but no rollbacks");
    }

    [Fact]
    public void AnInjectedDesyncIsCaughtOnBothSides()
    {
        // A's game changes P1's Josh on frame 200 every time it simulates it: a real, lasting desync.
        using var pair = new PeerPair(a: o => o with { AfterAdvance = s => { if (s.Frame == 200) s.Fighters[0].Josh += 3; } });
        Assert.True(pair.RunUntil(() => pair.BothEnded, 20000), pair.Logs());
        Assert.Equal(OnlineEndReason.Desync, pair.A.EndReason);
        Assert.Equal(OnlineEndReason.Desync, pair.B.EndReason);
        int found = Math.Max(pair.A.DesyncFrame, pair.B.DesyncFrame);
        Assert.True(found >= 200, $"desync reported at frame {found}");
    }

    [Fact]
    public void DifferentRulesAreRefusedBeforeTheMatchStarts()
    {
        using var pair = new PeerPair(b: o => o with { Rules = new MatchRules { Stage = Stages.Graybox, Guts = false } });
        Assert.True(pair.RunUntil(() => pair.BothEnded, 2000), pair.Logs());
        Assert.Contains(pair.A.EndReason, new[] { OnlineEndReason.RulesMismatch, OnlineEndReason.OpponentRejected });
        Assert.Contains(pair.B.EndReason, new[] { OnlineEndReason.RulesMismatch, OnlineEndReason.OpponentRejected });
        Assert.True(pair.A.EndReason == OnlineEndReason.RulesMismatch || pair.B.EndReason == OnlineEndReason.RulesMismatch);
        Assert.Equal(0, pair.A.State.Frame);
    }

    [Fact]
    public void BothPickingP1IsRefused()
    {
        using var pair = new PeerPair(b: o => o with { LocalSlot = 0 });
        Assert.True(pair.RunUntil(() => pair.BothEnded, 2000), pair.Logs());
        Assert.Contains(pair.A.EndReason, new[] { OnlineEndReason.SameSide, OnlineEndReason.OpponentRejected });
        Assert.Contains(pair.B.EndReason, new[] { OnlineEndReason.SameSide, OnlineEndReason.OpponentRejected });
    }

    [Fact]
    public void ThePlayerWhoStartsFirstWaitsForTheOther()
    {
        using var pair = new PeerPair { TickB = false };
        pair.RunUntil(() => false, 300);
        Assert.Equal(OnlinePhase.Handshaking, pair.A.Phase);
        pair.TickB = true;
        Assert.True(pair.RunUntil(() => pair.BothRunning && pair.A.FinalFrame > 60, 5000), pair.Logs());
    }

    [Fact]
    public void LeavingEndsTheMatchForTheOpponentAtOnce()
    {
        using var pair = new PeerPair();
        Assert.True(pair.RunUntil(() => pair.BothRunning && pair.B.FinalFrame > 120, 10000), pair.Logs());
        pair.A.Leave();
        Assert.Equal(OnlineEndReason.LocalLeft, pair.A.EndReason);
        Assert.True(pair.RunUntil(() => pair.B.Phase == OnlinePhase.Ended, 30), pair.Logs());
        Assert.Equal(OnlineEndReason.OpponentLeft, pair.B.EndReason);
    }

    [Fact]
    public void ARematchStartsAFreshMatchOnTheSamePorts()
    {
        using var pair = new PeerPair();
        Assert.True(pair.RunUntil(() => pair.BothRunning && pair.B.FinalFrame > 120, 10000), pair.Logs());
        ulong firstSeed = pair.A.Seed;
        pair.A.Leave(rematch: true);
        Assert.True(pair.RunUntil(() => pair.B.Phase == OnlinePhase.Ended, 30), pair.Logs());
        Assert.Equal(OnlineEndReason.OpponentRematch, pair.B.EndReason);

        pair.A.Dispose();
        pair.B.Dispose();
        pair.Create();
        Assert.True(pair.RunUntil(() => pair.BothRunning && pair.A.FinalFrame > 120 && pair.A.ChecksumsVerified > 0, 10000), pair.Logs());
        Assert.NotEqual(firstSeed, pair.A.Seed);
        Assert.Equal(pair.A.Seed, pair.B.Seed);
    }

    [Fact]
    public void ALostConnectionEndsTheMatchAfterTheTimeout()
    {
        static OnlineMatchOptions Short(OnlineMatchOptions o) => o with
        {
            DisconnectNotifyStart = TimeSpan.FromMilliseconds(150),
            DisconnectTimeout = TimeSpan.FromMilliseconds(600),
        };
        using var pair = new PeerPair(a: Short, b: Short);
        Assert.True(pair.RunUntil(() => pair.BothRunning && pair.A.FinalFrame > 120, 10000), pair.Logs());
        pair.Network.Blocked = true;
        Assert.True(pair.RunUntil(() => pair.BothEnded, 20000), pair.Logs());
        Assert.Equal(OnlineEndReason.OpponentLeft, pair.A.EndReason);
        Assert.Equal(OnlineEndReason.OpponentLeft, pair.B.EndReason);
        Assert.True(pair.SawInterrupted);
    }

    [Fact]
    public void AFrozenOpponentIsGivenUpOn()
    {
        // B's game stops ticking but its connection stays alive (Backdash keeps sending
        // keep-alives from background timers), so only the stall timeout can end it.
        using var pair = new PeerPair(a: o => o with { StallTimeoutTicks = 120 });
        Assert.True(pair.RunUntil(() => pair.BothRunning && pair.A.FinalFrame > 120, 10000), pair.Logs());
        pair.TickB = false;
        Assert.True(pair.RunUntil(() => pair.A.Phase == OnlinePhase.Ended, 3000), pair.Logs());
        Assert.Equal(OnlineEndReason.OpponentNotResponding, pair.A.EndReason);

        pair.TickB = true; // B wakes up and hears that A gave up
        Assert.True(pair.RunUntil(() => pair.B.Phase == OnlinePhase.Ended, 60), pair.Logs());
        Assert.Equal(OnlineEndReason.OpponentLeft, pair.B.EndReason);
    }
}
