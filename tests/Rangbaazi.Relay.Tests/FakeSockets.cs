using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace Rangbaazi.Relay.Tests;

/// <summary>A message the relay sent to a fake player.</summary>
internal sealed record SentMessage(WebSocketMessageType Type, byte[] Data)
{
    public string Text => Encoding.UTF8.GetString(Data);

    public bool IsJson(string type) => Type == WebSocketMessageType.Text && Text.Contains("\"type\":\"" + type + "\"", StringComparison.Ordinal);
}

/// <summary>
/// An in-memory player for driving <see cref="RelayConnection"/> directly. Sends complete at once and are
/// recorded; receives come from <see cref="ReceiveFrom"/> (or wait until the relay closes or aborts us).
/// When the relay sends its close frame, the fake answers it like a well-behaved client.
/// </summary>
internal class FakeWebSocket : WebSocket
{
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _stateLock = new();
    private WebSocketState _state = WebSocketState.Open;

    private int _binaryCount;
    private int _textCount;

    public ConcurrentQueue<SentMessage> Sent { get; } = new();

    public int BinaryCount => Volatile.Read(ref _binaryCount);

    public int TextCount => Volatile.Read(ref _textCount);

    /// <summary>Optional source of incoming messages; null means the player never sends anything.</summary>
    public Func<Memory<byte>, ValueTask<ValueWebSocketReceiveResult>>? ReceiveFrom { get; init; }

    public bool WasAborted { get; private set; }

    public override WebSocketCloseStatus? CloseStatus => null;

    public override string? CloseStatusDescription => null;

    public override WebSocketState State
    {
        get { lock (_stateLock) return _state; }
    }

    public override string? SubProtocol => null;

    /// <summary>The TEXT messages sent so far whose JSON type is <paramref name="type"/>.</summary>
    public int CountJson(string type) => Sent.Count(m => m.IsJson(type));

    /// <summary>
    /// Polls a cheap condition (use the counters) until it holds, or fails after the default timeout.
    /// Yields instead of sleeping, because a timer sleep is 15 ms on Windows and these tests loop.
    /// </summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow + RelayClient.DefaultTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting for " + what);
            await Task.Yield();
        }
    }

    /// <summary>The player closes its side, as if it had sent a close frame.</summary>
    public void ClientClose() => _closed.TrySetResult();

    public override void Abort()
    {
        lock (_stateLock)
        {
            _state = WebSocketState.Aborted;
            WasAborted = true;
        }
        _closed.TrySetResult();
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            if (_state == WebSocketState.Open) _state = WebSocketState.CloseSent;
        }
        _closed.TrySetResult(); // The client answers at once.
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
    }

    public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (!_closed.Task.IsCompleted && ReceiveFrom is not null)
        {
            return await ReceiveFrom(buffer).ConfigureAwait(false);
        }
        await _closed.Task.ConfigureAwait(false);
        if (WasAborted) throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
        lock (_stateLock) _state = WebSocketState.Closed;
        return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);
    }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        ValueWebSocketReceiveResult result = await ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        return new WebSocketReceiveResult(result.Count, result.MessageType, result.EndOfMessage);
    }

    public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        if (State != WebSocketState.Open && State != WebSocketState.CloseReceived)
        {
            throw new WebSocketException(WebSocketError.InvalidState);
        }
        Sent.Enqueue(new SentMessage(messageType, buffer.ToArray()));
        Interlocked.Increment(ref messageType == WebSocketMessageType.Binary ? ref _binaryCount : ref _textCount);
        return ValueTask.CompletedTask;
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
        SendAsync(buffer.AsMemory(), messageType, endOfMessage, cancellationToken).AsTask();
}
