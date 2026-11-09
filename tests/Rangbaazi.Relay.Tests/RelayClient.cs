using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Rangbaazi.Relay.Tests;

/// <summary>A received WebSocket message (reassembled), or the close frame.</summary>
internal sealed record Received(WebSocketMessageType Type, byte[] Data, WebSocketCloseStatus? CloseStatus)
{
    public string Text => Encoding.UTF8.GetString(Data);
}

/// <summary>A test player: a ClientWebSocket with receive helpers that time out instead of hanging.</summary>
internal sealed class RelayClient : IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    public RelayClient(HttpMessageInvoker? invoker = null)
    {
        Invoker = invoker;
        Socket.Options.CollectHttpResponseDetails = true; // so a refused upgrade exposes its HTTP status
    }

    public ClientWebSocket Socket { get; } = new();

    /// <summary>Custom connection handler (see <see cref="RelayHost.ConnectStalledAsync"/>), or null.</summary>
    public HttpMessageInvoker? Invoker { get; }

    public async Task<Received> ReceiveAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? DefaultTimeout);
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result = await Socket.ReceiveAsync(buffer, cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return new Received(WebSocketMessageType.Close, Array.Empty<byte>(), result.CloseStatus);
            }
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return new Received(result.MessageType, message.ToArray(), null);
        }
    }

    /// <summary>Next message must be a TEXT JSON object with the given type.</summary>
    public async Task<JsonElement> ReceiveJsonAsync(string expectedType)
    {
        Received received = await ReceiveAsync();
        Assert.True(received.Type == WebSocketMessageType.Text,
            $"Expected TEXT '{expectedType}', got {received.Type} (close status {received.CloseStatus})");
        JsonElement json = JsonDocument.Parse(received.Data).RootElement.Clone();
        Assert.Equal(expectedType, json.GetProperty("type").GetString());
        return json;
    }

    /// <summary>Next message must be start for <paramref name="expectedSlot"/>; returns its seed.</summary>
    public async Task<long> ReceiveStartAsync(int expectedSlot)
    {
        JsonElement start = await ReceiveJsonAsync("start");
        Assert.Equal(expectedSlot, start.GetProperty("slot").GetInt32());
        Assert.Equal(JsonValueKind.String, start.GetProperty("seed").ValueKind);
        long seed = long.Parse(start.GetProperty("seed").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(seed, 1L, (1L << 53) - 1);
        return seed;
    }

    public async Task<byte[]> ReceiveBinaryAsync()
    {
        Received received = await ReceiveAsync();
        Assert.True(received.Type == WebSocketMessageType.Binary,
            $"Expected BINARY, got {received.Type} '{(received.Type == WebSocketMessageType.Text ? received.Text : "")}' (close status {received.CloseStatus})");
        return received.Data;
    }

    /// <summary>Next message must be the server's close frame; acknowledges it and returns its status.</summary>
    public async Task<WebSocketCloseStatus?> ReceiveCloseAsync()
    {
        Received received = await ReceiveAsync();
        Assert.True(received.Type == WebSocketMessageType.Close,
            $"Expected close, got {received.Type} '{(received.Type == WebSocketMessageType.Text ? received.Text : "")}'");
        await AcknowledgeCloseAsync();
        return received.CloseStatus;
    }

    /// <summary>Reads (and ignores) messages until the close frame; acknowledges it and returns its status.</summary>
    public async Task<WebSocketCloseStatus?> SkipToCloseAsync()
    {
        while (true)
        {
            Received received = await ReceiveAsync();
            if (received.Type != WebSocketMessageType.Close) continue;
            await AcknowledgeCloseAsync();
            return received.CloseStatus;
        }
    }

    public Task SendBinaryAsync(byte[] data, bool endOfMessage = true) =>
        Socket.SendAsync(data, WebSocketMessageType.Binary, endOfMessage, CancellationToken.None);

    public Task SendTextAsync(string text) =>
        Socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>Client-initiated close with the full handshake.</summary>
    public async Task CloseAsync()
    {
        using var cts = new CancellationTokenSource(DefaultTimeout);
        await Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
    }

    public void Dispose()
    {
        Socket.Dispose();
        Invoker?.Dispose();
    }

    private async Task AcknowledgeCloseAsync()
    {
        if (Socket.State != WebSocketState.CloseReceived) return;
        using var cts = new CancellationTokenSource(DefaultTimeout);
        await Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", cts.Token);
    }
}
