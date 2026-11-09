using System.Diagnostics;
using Rangbaazi.Net.Transport;
using Rangbaazi.Sim;
using Rangbaazi.Sim.Diagnostics;

namespace Rangbaazi.Net.Tests;

/// <summary>
/// Two online matches talking to each other in one process, each played by a sparring bot. One
/// <see cref="Step"/> = one network step plus one tick of each player, so latency is in ticks.
/// Backdash runs real threads and timers, so the loop is paced to leave them time.
/// </summary>
internal sealed class PeerPair : IDisposable
{
    public readonly InMemoryNetwork Network;
    public OnlineMatch A;
    public OnlineMatch B;
    public readonly List<string> LogA = new();
    public readonly List<string> LogB = new();
    public bool TickA = true;
    public bool TickB = true;
    public CpuLevel LevelA = CpuLevel.Normal;
    public CpuLevel LevelB = CpuLevel.Hard;
    public bool SawInterrupted;
    private readonly int _portA;
    private readonly int _portB;

    public PeerPair(InMemoryNetwork? network = null, Func<OnlineMatchOptions, OnlineMatchOptions>? a = null,
        Func<OnlineMatchOptions, OnlineMatchOptions>? b = null, int portA = 7000, int portB = 7001)
    {
        Network = network ?? new InMemoryNetwork(seed: 12345);
        _portA = portA;
        _portB = portB;
        (A, B) = Create(a, b);
    }

    public (OnlineMatch A, OnlineMatch B) Create(Func<OnlineMatchOptions, OnlineMatchOptions>? a = null,
        Func<OnlineMatchOptions, OnlineMatchOptions>? b = null)
    {
        InMemoryTransport ta = Network.Bind(_portA), tb = Network.Bind(_portB);
        var optionsA = Fast(new OnlineMatchOptions { Peer = tb.LocalEndPoint, LocalSlot = 0 });
        var optionsB = Fast(new OnlineMatchOptions { Peer = ta.LocalEndPoint, LocalSlot = 1 });
        A = new OnlineMatch(a?.Invoke(optionsA) ?? optionsA, ta, line => { lock (LogA) LogA.Add(line); });
        B = new OnlineMatch(b?.Invoke(optionsB) ?? optionsB, tb, line => { lock (LogB) LogB.Add(line); });
        return (A, B);
    }

    /// <summary>Shorter Backdash retries, so a lost handshake packet costs milliseconds, not a second.</summary>
    public static OnlineMatchOptions Fast(OnlineMatchOptions o) => o with
    {
        ConfigureBackdash = n =>
        {
            n.Protocol.SyncFirstRetryInterval = TimeSpan.FromMilliseconds(40);
            n.Protocol.SyncRetryInterval = TimeSpan.FromMilliseconds(40);
            n.Protocol.ResendInputInterval = TimeSpan.FromMilliseconds(40);
        },
    };

    public void Step()
    {
        Network.Step();
        if (TickA) A.Tick(Bot(A, LevelA));
        if (TickB) B.Tick(Bot(B, LevelB));
        SawInterrupted |= A.Interrupted || B.Interrupted;
        Pace();
    }

    /// <summary>Steps until <paramref name="done"/> or <paramref name="maxSteps"/>; returns whether it got done.</summary>
    public bool RunUntil(Func<bool> done, int maxSteps)
    {
        for (int i = 0; i < maxSteps; i++)
        {
            if (done()) return true;
            Step();
        }
        return done();
    }

    public bool BothRunning => A.Phase == OnlinePhase.Running && B.Phase == OnlinePhase.Running;
    public bool BothEnded => A.Phase == OnlinePhase.Ended && B.Phase == OnlinePhase.Ended;

    public string Logs()
    {
        lock (LogA)
        lock (LogB)
            return "A:\n" + string.Join("\n", LogA.TakeLast(20)) + "\nB:\n" + string.Join("\n", LogB.TakeLast(20));
    }

    private static Buttons Bot(OnlineMatch m, CpuLevel level) =>
        m.Phase == OnlinePhase.Running ? SparringBot.Think(m.State, m.Rules, m.LocalSlot, level) : Buttons.None;

    /// <summary>About 1 ms: time for Backdash's background loops to move packets along.</summary>
    private static void Pace()
    {
        long until = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 1000;
        while (Stopwatch.GetTimestamp() < until) Thread.Yield();
    }

    public void Dispose()
    {
        A.Dispose();
        B.Dispose();
    }
}
