using System;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Rangbaazi.Relay;

internal readonly record struct OutgoingMessage(byte[] Payload, WebSocketMessageType Type);

/// <summary>
/// One player's WebSocket. Two loops run per connection:
/// <list type="bullet">
/// <item>the receive loop (the only reader) reassembles messages, enforces size and rate limits and
/// forwards binary messages into the partner's queue without ever waiting on the partner;</item>
/// <item>the send loop (the only writer) drains this connection's bounded queue. If the queue
/// overflows, the player is too slow (or gone) and the connection is aborted.</item>
/// </list>
/// Every close goes through <see cref="RequestClose"/>: the send loop then sends the close frame while the
/// receive loop keeps draining until the client's close reply arrives or the close timeout aborts the socket.
/// </summary>
internal sealed class RelayConnection
{
    private readonly WebSocket _socket;
    private readonly RelayServer _server;
    private readonly Channel<OutgoingMessage> _outgoing;
    private readonly TokenBucket _bucket;

    // Guards _peer. Forward holds it from reading the peer until the message is in the peer's queue, and
    // Leave clears _peer under it before queueing peer_left, so no forward can land after peer_left.
    // Lock order: server gate, then a connection's peer lock, then a channel's own lock. Never the reverse.
    private readonly object _peerLock = new();
    private RelayConnection? _peer;
    private CloseRequest? _close;
    private long _lastReceiveMs;
    private int _abortQueued;
    private int _queueCompleted;

    public RelayConnection(WebSocket socket, RelayServer server)
    {
        _socket = socket;
        _server = server;
        RelayOptions options = server.Options;
        _outgoing = Channel.CreateBounded<OutgoingMessage>(new BoundedChannelOptions(options.OutgoingQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait, // TryWrite fails when full; nothing ever waits.
        });
        _bucket = new TokenBucket(options.RateLimitBurst, options.RateLimitPerSecond, Stopwatch.GetTimestamp(), Stopwatch.Frequency);
        _lastReceiveMs = Environment.TickCount64;
    }

    /// <summary>Room membership; read and written only under the server lock.</summary>
    public Room? Room { get; set; }

    /// <summary>Slot in <see cref="Room"/> (0 or 1, -1 when not in a room); server lock only.</summary>
    public int Slot { get; set; } = -1;

    public bool IsClosing => Volatile.Read(ref _close) is not null;

    /// <summary>Links or unlinks the partner. Once this returns, no forward to the old partner is in flight.</summary>
    public void SetPeer(RelayConnection? peer)
    {
        lock (_peerLock) _peer = peer;
    }

    /// <summary>
    /// Queues each player's start and links them, holding both peer locks throughout. A message
    /// either player sends the moment it sees start goes through Forward, which takes its own peer
    /// lock: it waits until both are linked, so it is neither dropped nor delivered before the
    /// other player's start. Called under the server lock; nothing else holds two peer locks, so
    /// the fixed order (first, then second) can't deadlock.
    /// </summary>
    public static void Pair(RelayConnection first, byte[] firstStart, RelayConnection second, byte[] secondStart)
    {
        lock (first._peerLock)
        lock (second._peerLock)
        {
            first.Enqueue(firstStart);
            second.Enqueue(secondStart);
            first._peer = second;
            second._peer = first;
        }
    }

    /// <summary>Joins room <paramref name="code"/> and relays until the connection ends.</summary>
    public async Task RunAsync(string code)
    {
        switch (_server.Join(code, this))
        {
            case JoinOutcome.Full:
                await _server.RejectAsync(_socket, RelayMessages.Full, RelayCloseCodes.RoomFull, "room full").ConfigureAwait(false);
                return;
            case JoinOutcome.Busy:
                await _server.RejectAsync(_socket, RelayMessages.Busy, RelayCloseCodes.Busy, "server busy").ConfigureAwait(false);
                return;
        }

        _server.Track(this);
        Task sender = SendLoopAsync();
        try
        {
            await ReceiveLoopAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Reset, abort or protocol error: nothing to say to the client any more.
            Abort();
        }
        finally
        {
            RequestClose(WebSocketCloseStatus.NormalClosure, "closed");
            _server.Leave(this); // Idempotent; covers an abort that came before any close request.
            await sender.ConfigureAwait(false);
            _server.Untrack(this);
        }
    }

    /// <summary>
    /// Queues a message for this player. Never blocks: a full queue means the player cannot keep up, so the
    /// connection is aborted (on the thread pool, so callers holding the server lock are never re-entered).
    /// </summary>
    public void Enqueue(byte[] payload, WebSocketMessageType type = WebSocketMessageType.Text)
    {
        if (_outgoing.Writer.TryWrite(new OutgoingMessage(payload, type))) return;
        if (Volatile.Read(ref _queueCompleted) != 0) return; // Closing or aborted: drop the message.
        if (Interlocked.Exchange(ref _abortQueued, 1) != 0) return;
        _server.CountOverflow();
        _server.LogConnectionClosed("send queue overflow");
        ThreadPool.UnsafeQueueUserWorkItem(static c => c.Abort(), this, preferLocal: false);
    }

    /// <summary>Starts closing with <paramref name="status"/> (first request wins) and leaves the room at once.</summary>
    public void RequestClose(WebSocketCloseStatus status, string description)
    {
        var request = new CloseRequest(status, description, Environment.TickCount64);
        if (Interlocked.CompareExchange(ref _close, request, null) is not null) return;
        _server.Leave(this);
        CompleteQueue();
    }

    /// <summary>Called by the server's sweeper: idle timeout, and the deadline for a close handshake.</summary>
    public void CheckTimers(long nowMs)
    {
        CloseRequest? close = Volatile.Read(ref _close);
        if (close is not null)
        {
            if (nowMs - close.RequestedAtMs >= (long)_server.Options.CloseTimeout.TotalMilliseconds) Abort();
            return;
        }
        if (nowMs - Volatile.Read(ref _lastReceiveMs) >= (long)_server.Options.IdleTimeout.TotalMilliseconds)
        {
            _server.LogConnectionClosed("idle timeout");
            RequestClose(RelayCloseCodes.Idle, "idle timeout");
        }
    }

    /// <summary>Tears the socket down at once. Pending sends and receives fail, so both loops finish.</summary>
    public void Abort()
    {
        CompleteQueue();
        try
        {
            _socket.Abort();
        }
        catch (Exception)
        {
            // Best effort. After the request has finished, ASP.NET Core's socket throws ObjectDisposedException
            // (its HttpContext is gone); this runs on the sweeper timer and the thread pool, where an escaping
            // exception would take the whole relay down.
        }
    }

    private void CompleteQueue()
    {
        Volatile.Write(ref _queueCompleted, 1);
        _outgoing.Writer.TryComplete();
    }

    private async Task ReceiveLoopAsync()
    {
        int max = _server.Options.MaxMessageBytes;
        byte[] buffer = new byte[max + 1];
        int length = 0; // bytes of the current BINARY message so far
        bool inText = false; // inside a TEXT message: discarded as it arrives, whatever its size

        while (true)
        {
            bool closing = IsClosing;
            Memory<byte> target = closing || inText ? buffer : buffer.AsMemory(length);
            ValueWebSocketReceiveResult result = await _socket.ReceiveAsync(target, CancellationToken.None).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return;
            if (closing || IsClosing)
            {
                length = 0; // Close already requested: discard until the client's close reply.
                continue;
            }

            Volatile.Write(ref _lastReceiveMs, Environment.TickCount64);
            if (!_bucket.TryTake(Stopwatch.GetTimestamp()))
            {
                _server.LogConnectionClosed("rate limit");
                RequestClose(WebSocketCloseStatus.PolicyViolation, "rate limit exceeded");
                continue;
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                // TEXT is ignored, at any size (the size limit is for binary messages). It still counts as
                // activity, and each read of it takes a token, so a huge or endless TEXT message hits the rate limit.
                inText = !result.EndOfMessage;
                continue;
            }

            length += result.Count;
            if (length > max)
            {
                _server.LogConnectionClosed("message too big");
                RequestClose(WebSocketCloseStatus.MessageTooBig, "message too big");
                continue;
            }
            if (!result.EndOfMessage) continue;

            Forward(buffer.AsSpan(0, length));
            length = 0;
        }
    }

    private void Forward(ReadOnlySpan<byte> message)
    {
        byte[] copy = message.ToArray();
        lock (_peerLock)
        {
            if (_peer is null)
            {
                _server.CountDropped();
                return;
            }
            _peer.Enqueue(copy, WebSocketMessageType.Binary); // Never blocks: a full queue aborts the peer instead.
        }
    }

    private async Task SendLoopAsync()
    {
        ChannelReader<OutgoingMessage> reader = _outgoing.Reader;
        try
        {
            while (!IsClosing && await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (!IsClosing && reader.TryRead(out OutgoingMessage message))
                {
                    await _socket.SendAsync(new ReadOnlyMemory<byte>(message.Payload), message.Type, true, CancellationToken.None).ConfigureAwait(false);
                }
            }

            // Closing: queued messages are dropped. Send our close frame (or the reply to the client's).
            CloseRequest? close = Volatile.Read(ref _close);
            if (close is not null && _socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await _socket.CloseOutputAsync(close.Status, close.Description, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            Abort();
        }
    }

    private sealed record CloseRequest(WebSocketCloseStatus Status, string Description, long RequestedAtMs);
}
