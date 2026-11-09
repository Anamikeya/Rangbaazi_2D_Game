using System.Diagnostics;
using System.Net;
using Rangbaazi.Net.Transport;
using Rangbaazi.Sim;
using Rangbaazi.Sim.Diagnostics;

namespace Rangbaazi.Net.Tests;

/// <summary>The same match over real UDP sockets on 127.0.0.1 (loopback: no firewall prompt).</summary>
[Collection(nameof(OnlineMatchTests))]
public class UdpOnlineTests
{
    [Fact]
    public void BotsPlayOverRealUdp()
    {
        var ta = new UdpTransport(new IPEndPoint(IPAddress.Loopback, 0));
        var tb = new UdpTransport(new IPEndPoint(IPAddress.Loopback, 0));
        using var a = new OnlineMatch(PeerPair.Fast(new OnlineMatchOptions { Peer = tb.LocalEndPoint, LocalSlot = 0 }), ta);
        using var b = new OnlineMatch(PeerPair.Fast(new OnlineMatchOptions { Peer = ta.LocalEndPoint, LocalSlot = 1 }), tb);

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(30) && (a.FinalFrame < 400 || b.FinalFrame < 400))
        {
            a.Tick(Bot(a));
            b.Tick(Bot(b));
            if (a.Phase == OnlinePhase.Ended || b.Phase == OnlinePhase.Ended) break;
            Thread.Sleep(1);
        }

        Assert.True(a.Phase == OnlinePhase.Running && b.Phase == OnlinePhase.Running, $"A {a.EndMessage}; B {b.EndMessage}");
        Assert.True(a.ChecksumsVerified > 0 && b.ChecksumsVerified > 0);
        int common = Math.Min(a.FinalFrame, b.FinalFrame);
        Assert.True(a.Checksums!.TryGet(common, out ulong ca) && b.Checksums!.TryGet(common, out ulong cb) && ca == cb);
        Assert.Equal(0, a.DroppedMalformed + b.DroppedMalformed);
    }

    [Fact]
    public void ASecondInstanceCannotTakeAPortInUse()
    {
        using var first = new UdpTransport(new IPEndPoint(IPAddress.Loopback, 0));
        if (!OperatingSystem.IsWindows()) return; // exclusive binding is the Windows default elsewhere
        Assert.ThrowsAny<System.Net.Sockets.SocketException>(() => new UdpTransport(first.LocalEndPoint).Dispose());
    }

    private static Buttons Bot(OnlineMatch m) =>
        m.Phase == OnlinePhase.Running ? SparringBot.Think(m.State, m.Rules, m.LocalSlot) : Buttons.None;
}
