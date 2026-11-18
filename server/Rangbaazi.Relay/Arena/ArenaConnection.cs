using System;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Rangbaazi.Relay.Arena;

/// <summary>
/// One arena player's WebSocket, built like <see cref="RelayConnection"/>: a receive loop (the only
/// reader) that turns INPUT and TEXT requests into server calls, and a send loop (the only writer)
/// that drains a bounded queue. A player whose queue overflows is too slow and is dropped.
/// </summary>
internal sealed class ArenaConnection
{
    /// <summary>Largest TEXT request accepted; requests are tiny JSON objects.</summary>
    public const int MaxTextBytes = 512;

    private readonly WebSocket _socket;
    private readonly ArenaServer _server;
    private readonly Channel<OutgoingMessage> _outgoing;
    private readonly TokenBucket _bucket;
    private CloseRequest? _close;
    private long _lastReceiveMs;
    private int _abortQueued;
    private int _queueCompleted;

    public ArenaConnection(WebSocket socket, ArenaServer server)
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

    /// <summary>The room and member this connection is; set and cleared under the server lock.</summary>
    public ArenaRoom? Room { get; set; }

    public ArenaPlayer? Player { get; set; }

    public bool IsClosing => Volatile.Read(ref _close) is not null;

    public async Task RunAsync(string code, bool host, int minutes, string name, string character)
    {
        switch (_server.Join(code, host, minutes, name, character, this))
        {
            case ArenaJoinOutcome.Full:
                await Reject("full", "The room is full (15 players).", RelayCloseCodes.RoomFull).ConfigureAwait(false);
                return;
            case ArenaJoinOutcome.Busy:
                await Reject("busy", "The server is busy. Try again in a minute.", RelayCloseCodes.Busy).ConfigureAwait(false);
                return;
            case ArenaJoinOutcome.Taken:
                await Reject("taken", "That room code is already in use.", ArenaCloseCodes.RoomTaken).ConfigureAwait(false);
                return;
            case ArenaJoinOutcome.NoRoom:
                await Reject("no_room", "No room with that code. Check it with the host.", ArenaCloseCodes.NoRoom).ConfigureAwait(false);
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
            Abort();
        }
        finally
        {
            RequestClose(WebSocketCloseStatus.NormalClosure, "closed");
            _server.Leave(this);
            await sender.ConfigureAwait(false);
            _server.Untrack(this);
        }
    }

    private async Task Reject(string code, string message, WebSocketCloseStatus status)
    {
        using var timeout = new CancellationTokenSource(_server.Options.CloseTimeout);
        try
        {
            await _socket.SendAsync(ArenaMessages.Error(code, message), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
            await _socket.CloseAsync(status, code, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            _socket.Abort();
        }
    }

    /// <summary>Queues a message. Never blocks; a full queue aborts the connection (on the thread pool).</summary>
    public void Enqueue(byte[] payload, WebSocketMessageType type)
    {
        if (_outgoing.Writer.TryWrite(new OutgoingMessage(payload, type))) return;
        if (Volatile.Read(ref _queueCompleted) != 0) return;
        if (Interlocked.Exchange(ref _abortQueued, 1) != 0) return;
        _server.LogConnectionClosed("send queue overflow");
        ThreadPool.UnsafeQueueUserWorkItem(static c => c.Abort(), this, preferLocal: false);
    }

    public void RequestClose(WebSocketCloseStatus status, string description)
    {
        var request = new CloseRequest(status, description, Environment.TickCount64);
        if (Interlocked.CompareExchange(ref _close, request, null) is not null) return;
        _server.Leave(this);
        CompleteQueue();
    }

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

    public void Abort()
    {
        CompleteQueue();
        try
        {
            _socket.Abort();
        }
        catch (Exception)
        {
            // Best effort (see RelayConnection.Abort).
        }
    }

    private void CompleteQueue()
    {
        Volatile.Write(ref _queueCompleted, 1);
        _outgoing.Writer.TryComplete();
    }

    private async Task ReceiveLoopAsync()
    {
        int max = Math.Max(_server.Options.MaxMessageBytes, MaxTextBytes);
        byte[] buffer = new byte[max + 1];
        int length = 0;

        while (true)
        {
            bool closing = IsClosing;
            Memory<byte> target = closing ? buffer : buffer.AsMemory(length);
            ValueWebSocketReceiveResult result = await _socket.ReceiveAsync(target, CancellationToken.None).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return;
            if (closing || IsClosing)
            {
                length = 0;
                continue;
            }

            Volatile.Write(ref _lastReceiveMs, Environment.TickCount64);
            if (!_bucket.TryTake(Stopwatch.GetTimestamp()))
            {
                _server.LogConnectionClosed("rate limit");
                RequestClose(WebSocketCloseStatus.PolicyViolation, "rate limit exceeded");
                continue;
            }

            length += result.Count;
            int limit = result.MessageType == WebSocketMessageType.Text ? MaxTextBytes : _server.Options.MaxMessageBytes;
            if (length > limit)
            {
                _server.LogConnectionClosed("message too big");
                RequestClose(WebSocketCloseStatus.MessageTooBig, "message too big");
                continue;
            }
            if (!result.EndOfMessage) continue;

            ReadOnlySpan<byte> message = buffer.AsSpan(0, length);
            length = 0;
            bool ok = result.MessageType == WebSocketMessageType.Binary
                ? HandleBinary(message)
                : ArenaRequest.TryParse(message, out ArenaRequest request) && _server.Request(this, request);
            if (!ok)
            {
                _server.LogConnectionClosed("bad message");
                RequestClose(WebSocketCloseStatus.InvalidPayloadData, "bad message");
            }
        }
    }

    private bool HandleBinary(ReadOnlySpan<byte> message)
    {
        if (!ArenaBinary.TryReadInput(message, out ushort buttons)) return false;
        _server.Input(this, buttons);
        return true;
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
