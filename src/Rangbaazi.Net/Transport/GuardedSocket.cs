using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Backdash.Network.Client;

namespace Rangbaazi.Net.Transport;

/// <summary>
/// The socket Backdash talks through, wrapped around an <see cref="IDatagramTransport"/>. It:
/// <list type="bullet">
/// <item>accepts datagrams only from the one peer of the match;</item>
/// <item>drops anything that isn't a well-formed Backdash message (<see cref="BackdashPacketValidator"/>);</item>
/// <item>never lets a network error reach Backdash, whose receive and send loops stop for good on one;</item>
/// <item>carries our own control messages beside Backdash's, told apart by a one-byte channel prefix.</item>
/// </list>
/// Its receive pump runs from construction, so control messages flow before Backdash starts.
/// Owns the transport.
/// </summary>
internal sealed class GuardedSocket : IPeerSocket
{
    private const int MaxDatagram = 2048;
    private const int MaxQueuedControl = 256;

    private readonly IDatagramTransport _transport;
    private readonly SocketAddress _peer;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<byte[]> _backdashInbox = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1024)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });
    private readonly ConcurrentQueue<byte[]> _controlInbox = new();
    private int _disposed;
    private long _strangers;
    private long _malformed;

    public GuardedSocket(IDatagramTransport transport, IPEndPoint peer)
    {
        if (peer.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("The peer must be an IPv4 address.", nameof(peer));
        _transport = transport;
        _peer = peer.Serialize();
        _ = Task.Run(PumpAsync);
    }

    public AddressFamily AddressFamily => AddressFamily.InterNetwork;

    public int Port => _transport.LocalEndPoint.Port;

    public IPEndPoint LocalEndPoint => _transport.LocalEndPoint;

    /// <summary>Datagrams dropped because they came from someone other than the peer.</summary>
    public long DroppedFromStrangers => Interlocked.Read(ref _strangers);

    /// <summary>Datagrams from the peer dropped because they were malformed.</summary>
    public long DroppedMalformed => Interlocked.Read(ref _malformed);

    /// <summary>Sends one of our control messages to the peer.</summary>
    public void SendControl(in ControlMessage message)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Span<byte> buffer = stackalloc byte[1 + ControlMessage.MaxSize];
        buffer[0] = NetProtocol.ControlChannel;
        int length = message.Write(buffer[1..]);
        _transport.Send(buffer[..(1 + length)], _peer);
    }

    /// <summary>Takes the next control message the peer sent, if any. Call from the game thread.</summary>
    public bool TryReceiveControl(out ControlMessage message)
    {
        while (_controlInbox.TryDequeue(out byte[]? bytes))
            if (ControlMessage.TryRead(bytes, out message)) return true;
        message = default;
        return false;
    }

    // ---- IPeerSocket (called by Backdash) ----

    public async ValueTask<int> ReceiveFromAsync(Memory<byte> buffer, SocketAddress address, CancellationToken cancellationToken)
    {
        byte[] packet;
        try
        {
            packet = await _backdashInbox.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // Backdash ends its receive loop quietly only on cancellation.
            throw new OperationCanceledException("The socket is closed.");
        }
        packet.CopyTo(buffer);
        // Every packet that gets here came from the peer, so report its address exactly as
        // Backdash configured it (it drops packets whose address bytes differ).
        address.Size = _peer.Size;
        _peer.Buffer.Span[.._peer.Size].CopyTo(address.Buffer.Span);
        return packet.Length;
    }

    public ValueTask<SocketReceiveFromResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Backdash 0.7.8 never calls this.");

    public ValueTask<int> SendToAsync(ReadOnlyMemory<byte> buffer, SocketAddress socketAddress, CancellationToken cancellationToken)
    {
        // Backdash asserts that the whole buffer was sent, so always report that; a datagram that
        // can't go out is simply lost, which its protocol already copes with.
        if (Volatile.Read(ref _disposed) == 0 && socketAddress.Equals(_peer) && buffer.Length < MaxDatagram)
        {
            Span<byte> framed = stackalloc byte[buffer.Length + 1];
            framed[0] = NetProtocol.BackdashChannel;
            buffer.Span.CopyTo(framed[1..]);
            _transport.Send(framed, _peer);
        }
        return ValueTask.FromResult(buffer.Length);
    }

    public ValueTask<int> SendToAsync(ReadOnlyMemory<byte> buffer, EndPoint remoteEndPoint, CancellationToken cancellationToken) =>
        SendToAsync(buffer, remoteEndPoint.Serialize(), cancellationToken);

    public void Close() => Dispose();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _backdashInbox.Writer.TryComplete();
        _transport.Dispose();
    }

    private async Task PumpAsync()
    {
        var buffer = new byte[MaxDatagram];
        var from = new SocketAddress(AddressFamily.InterNetwork);
        CancellationToken stop = _stop.Token;
        while (!stop.IsCancellationRequested)
        {
            int length;
            try
            {
                length = await _transport.ReceiveAsync(buffer, from, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // A transport must only throw on close, but never let a bug end the pump.
                if (stop.IsCancellationRequested) return;
                continue;
            }

            if (!from.Equals(_peer))
            {
                Interlocked.Increment(ref _strangers);
                continue;
            }
            if (length < 2 || length > buffer.Length)
            {
                Interlocked.Increment(ref _malformed);
                continue;
            }

            ReadOnlySpan<byte> payload = buffer.AsSpan(1, length - 1);
            switch (buffer[0])
            {
                case NetProtocol.BackdashChannel when BackdashPacketValidator.IsValid(payload):
                    _backdashInbox.Writer.TryWrite(payload.ToArray());
                    break;
                case NetProtocol.ControlChannel when payload.Length <= ControlMessage.MaxSize && _controlInbox.Count < MaxQueuedControl:
                    _controlInbox.Enqueue(payload.ToArray());
                    break;
                default:
                    Interlocked.Increment(ref _malformed);
                    break;
            }
        }
    }
}
