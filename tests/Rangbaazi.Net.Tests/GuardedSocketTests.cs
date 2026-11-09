using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Rangbaazi.Net.Transport;

namespace Rangbaazi.Net.Tests;

public class GuardedSocketTests
{
    private static bool WaitFor(Func<bool> condition, int ms = 2000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (condition()) return true;
            Thread.Sleep(1);
        }
        return condition();
    }

    private static byte[] Framed(byte channel, ReadOnlySpan<byte> payload) => [channel, .. payload];

    private static byte[] Control(in ControlMessage m)
    {
        var buffer = new byte[ControlMessage.MaxSize];
        return Framed(NetProtocol.ControlChannel, buffer.AsSpan(0, m.Write(buffer)));
    }

    [Fact]
    public void OnlyThePeerGetsThrough()
    {
        var net = new InMemoryNetwork();
        InMemoryTransport mine = net.Bind(7000), peer = net.Bind(7001), stranger = net.Bind(7002);
        using var socket = new GuardedSocket(mine, peer.LocalEndPoint);

        stranger.Send(Control(ControlMessage.Hello(1, 0, 1, 2)), mine.LocalEndPoint.Serialize());
        Assert.True(WaitFor(() => socket.DroppedFromStrangers == 1));
        Assert.False(socket.TryReceiveControl(out _));

        peer.Send(Control(ControlMessage.Hello(3, 0, 1, 2)), mine.LocalEndPoint.Serialize());
        ControlMessage got = default;
        Assert.True(WaitFor(() => socket.TryReceiveControl(out got)));
        Assert.Equal(3UL, got.Nonce);
    }

    [Fact]
    public void MalformedBackdashPacketsNeverReachBackdash()
    {
        var net = new InMemoryNetwork();
        InMemoryTransport mine = net.Bind(7000), peer = net.Bind(7001);
        using var socket = new GuardedSocket(mine, peer.LocalEndPoint);
        SocketAddress to = mine.LocalEndPoint.Serialize();

        var evil = new byte[6 + 10 + 3];
        evil[1] = 3;   // Input
        evil[15] = 255; // 255 peers: Backdash would write past its 4-entry buffer
        peer.Send(Framed(NetProtocol.BackdashChannel, evil), to);
        peer.Send(Framed(NetProtocol.BackdashChannel, new byte[] { 0, 1, 0 }), to); // truncated
        peer.Send(new byte[] { 0x55, 1, 2, 3, 4, 5, 6 }, to);                       // no known channel
        var keepAlive = new byte[] { 0, 6, 0, 0, 0, 1 };
        peer.Send(Framed(NetProtocol.BackdashChannel, keepAlive), to);

        var buffer = new byte[64];
        var from = new SocketAddress(AddressFamily.InterNetwork);
        ValueTask<int> receive = socket.ReceiveFromAsync(buffer, from, new CancellationTokenSource(2000).Token);
        int length = receive.AsTask().GetAwaiter().GetResult();
        Assert.Equal(keepAlive, buffer[..length]); // the first packet Backdash sees is the valid one
        Assert.Equal(peer.LocalEndPoint.Serialize(), from);
        Assert.Equal(3, socket.DroppedMalformed);
    }

    [Fact]
    public void BackdashSendsAreFramedAndAlwaysReportedComplete()
    {
        var net = new InMemoryNetwork();
        InMemoryTransport mine = net.Bind(7000), peer = net.Bind(7001);
        using var socket = new GuardedSocket(mine, peer.LocalEndPoint);
        var payload = new byte[] { 0, 6, 0, 0, 0, 9 };

        Assert.Equal(6, socket.SendToAsync(payload, peer.LocalEndPoint.Serialize(), default).Result);
        Assert.Equal(6, socket.SendToAsync(payload, new IPEndPoint(IPAddress.Loopback, 9999).Serialize(), default).Result); // dropped, not to the peer

        var buffer = new byte[64];
        var from = new SocketAddress(AddressFamily.InterNetwork);
        int length = peer.ReceiveAsync(buffer, from, new CancellationTokenSource(2000).Token).AsTask().Result;
        Assert.Equal(Framed(NetProtocol.BackdashChannel, payload), buffer[..length]);
        Assert.Equal(1, net.DatagramsSent);
    }

    [Fact]
    public void ATransportErrorDoesNotStopTheSocket()
    {
        var net = new InMemoryNetwork();
        InMemoryTransport peer = net.Bind(7001);
        var flaky = new FlakyTransport(net.Bind(7000));
        using var socket = new GuardedSocket(flaky, peer.LocalEndPoint);

        peer.Send(Control(ControlMessage.Bye(4, 0, ByeReason.Left)), flaky.LocalEndPoint.Serialize());
        ControlMessage got = default;
        Assert.True(WaitFor(() => socket.TryReceiveControl(out got)));
        Assert.Equal(ControlKind.Bye, got.Kind);
        Assert.True(flaky.Failures > 0);
    }

    [Fact]
    public void ClosingEndsBackdashsReceiveQuietly()
    {
        var net = new InMemoryNetwork();
        var socket = new GuardedSocket(net.Bind(7000), net.Bind(7001).LocalEndPoint);
        ValueTask<int> pending = socket.ReceiveFromAsync(new byte[16], new SocketAddress(AddressFamily.InterNetwork), default);
        socket.Close();
        Assert.ThrowsAny<OperationCanceledException>(() => pending.AsTask().GetAwaiter().GetResult());
    }

    /// <summary>Throws on its first receives, as a buggy transport might, then works.</summary>
    private sealed class FlakyTransport(InMemoryTransport inner) : IDatagramTransport
    {
        public int Failures;

        public IPEndPoint LocalEndPoint => inner.LocalEndPoint;

        public void Send(ReadOnlySpan<byte> datagram, SocketAddress to) => inner.Send(datagram, to);

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, SocketAddress from, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref Failures) <= 3) throw new SocketException((int)SocketError.ConnectionReset);
            return inner.ReceiveAsync(buffer, from, cancellationToken);
        }

        public void Dispose() => inner.Dispose();
    }
}
