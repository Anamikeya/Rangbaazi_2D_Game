using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Rangbaazi.Net.Transport;

/// <summary>
/// A simulated network for tests and tools: transports bound to it on 127.0.0.1 ports exchange
/// datagrams with a latency counted in network steps, plus optional jitter (which reorders
/// datagrams), seeded random loss, and a switch that cuts every link. Call <see cref="Step"/> once
/// per game tick; a datagram sent with <see cref="LatencyTicks"/> = 3 is delivered on the third
/// step after it was sent. Thread-safe.
/// </summary>
public sealed class InMemoryNetwork
{
    private readonly object _gate = new();
    private readonly Dictionary<int, InMemoryTransport> _bound = new();
    private readonly PriorityQueue<Packet, (long Due, long Order)> _inFlight = new();
    private ulong _rng;
    private long _tick;
    private long _order;

    public InMemoryNetwork(ulong seed = 0x9E3779B97F4A7C15UL) => _rng = seed == 0 ? 1 : seed;

    /// <summary>One-way delay, in steps.</summary>
    public int LatencyTicks { get; set; }

    /// <summary>Extra random delay of 0 to this many steps per datagram; reorders datagrams.</summary>
    public int JitterTicks { get; set; }

    /// <summary>Chance, in percent, that a datagram is lost.</summary>
    public int LossPercent { get; set; }

    /// <summary>While true every datagram is lost, as if the cable were pulled.</summary>
    public bool Blocked { get; set; }

    public long DatagramsSent { get; private set; }
    public long DatagramsDropped { get; private set; }

    public InMemoryTransport Bind(int port)
    {
        if (port <= 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        lock (_gate)
        {
            if (_bound.ContainsKey(port)) throw new InvalidOperationException($"Port {port} is already in use.");
            var transport = new InMemoryTransport(this, port);
            _bound.Add(port, transport);
            return transport;
        }
    }

    /// <summary>Advances time by one step and delivers every datagram that is due.</summary>
    public void Step()
    {
        lock (_gate)
        {
            _tick++;
            while (_inFlight.TryPeek(out Packet packet, out var due) && due.Due <= _tick)
            {
                _inFlight.Dequeue();
                if (_bound.TryGetValue(packet.ToPort, out InMemoryTransport? target)) target.Deliver(packet.Bytes, packet.From);
            }
        }
    }

    internal void Send(InMemoryTransport from, ReadOnlySpan<byte> datagram, SocketAddress to)
    {
        int port = PortOf(to);
        lock (_gate)
        {
            DatagramsSent++;
            if (Blocked || (LossPercent > 0 && (int)(Next() % 100) < LossPercent))
            {
                DatagramsDropped++;
                return;
            }
            long delay = LatencyTicks + (JitterTicks > 0 ? (long)(Next() % (ulong)(JitterTicks + 1)) : 0);
            var packet = new Packet(datagram.ToArray(), from.Address, port);
            if (delay <= 0)
            {
                if (_bound.TryGetValue(port, out InMemoryTransport? target)) target.Deliver(packet.Bytes, packet.From);
                return;
            }
            _inFlight.Enqueue(packet, (_tick + delay, _order++));
        }
    }

    internal void Unbind(InMemoryTransport transport)
    {
        lock (_gate)
        {
            if (_bound.TryGetValue(transport.LocalEndPoint.Port, out InMemoryTransport? bound) && bound == transport)
                _bound.Remove(transport.LocalEndPoint.Port);
        }
    }

    private ulong Next()
    {
        ulong z = _rng += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static int PortOf(SocketAddress address) => address.Size >= 4 ? (address[2] << 8) | address[3] : 0;

    private readonly record struct Packet(byte[] Bytes, SocketAddress From, int ToPort);
}

/// <summary>One endpoint on an <see cref="InMemoryNetwork"/>, at 127.0.0.1:port.</summary>
public sealed class InMemoryTransport : IDatagramTransport
{
    private readonly InMemoryNetwork _network;
    private readonly Channel<(byte[] Bytes, SocketAddress From)> _inbox =
        Channel.CreateBounded<(byte[], SocketAddress)>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    internal InMemoryTransport(InMemoryNetwork network, int port)
    {
        _network = network;
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, port);
        Address = LocalEndPoint.Serialize();
    }

    public IPEndPoint LocalEndPoint { get; }

    internal SocketAddress Address { get; }

    public void Send(ReadOnlySpan<byte> datagram, SocketAddress to) => _network.Send(this, datagram, to);

    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, SocketAddress from, CancellationToken cancellationToken)
    {
        (byte[] bytes, SocketAddress sender) item;
        try
        {
            item = await _inbox.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new OperationCanceledException("The transport is closed.");
        }
        int length = Math.Min(item.bytes.Length, buffer.Length);
        item.bytes.AsSpan(0, length).CopyTo(buffer.Span);
        from.Size = item.sender.Size;
        item.sender.Buffer.Span[..item.sender.Size].CopyTo(from.Buffer.Span);
        return length;
    }

    internal void Deliver(byte[] bytes, SocketAddress from) => _inbox.Writer.TryWrite((bytes, from));

    public void Dispose()
    {
        _network.Unbind(this);
        _inbox.Writer.TryComplete();
    }
}
