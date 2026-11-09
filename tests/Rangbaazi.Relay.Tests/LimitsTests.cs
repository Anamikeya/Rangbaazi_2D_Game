using System.Net.WebSockets;
using System.Text.Json;

namespace Rangbaazi.Relay.Tests;

/// <summary>Size, rate, idle and capacity limits, end to end.</summary>
public sealed class LimitsTests
{
    [Fact]
    public async Task Message_over_256_bytes_closes_with_1009_and_the_partner_gets_peer_left()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        (RelayClient p1, RelayClient p2, _) = await host.PairAsync("BIG1");
        await p1.SendBinaryAsync(new byte[257]);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, await p1.ReceiveCloseAsync());
        await p2.ReceiveJsonAsync("peer_left");
    }

    [Fact]
    public async Task Fragmented_message_over_256_bytes_closes_with_1009()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        RelayClient p1 = await host.JoinAsync("BIG2", 0);
        await p1.SendBinaryAsync(new byte[200], endOfMessage: false);
        await p1.SendBinaryAsync(new byte[100], endOfMessage: true);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, await p1.ReceiveCloseAsync());
    }

    [Fact]
    public async Task Text_over_256_bytes_is_ignored_like_any_text()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        (RelayClient p1, RelayClient p2, _) = await host.PairAsync("BIGTEXT");
        await p1.SendTextAsync("{\"type\":\"hello\",\"pad\":\"" + new string('x', 1000) + "\"}");
        // A fragmented TEXT message, then a binary one: the binary one must not be counted as its continuation.
        await p1.Socket.SendAsync(Enumerable.Repeat((byte)'a', 300).ToArray(), WebSocketMessageType.Text, false, CancellationToken.None);
        await p1.Socket.SendAsync(Enumerable.Repeat((byte)'b', 300).ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);
        byte[] game = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        await p1.SendBinaryAsync(game);
        Assert.Equal(game, await p2.ReceiveBinaryAsync());
        Assert.Equal(WebSocketState.Open, p1.Socket.State);
    }

    [Fact]
    public async Task Flooding_past_the_rate_limit_closes_with_1008()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        RelayClient p1 = await host.JoinAsync("FLOOD", 0);
        var message = new byte[] { 1 };
        for (int i = 0; i < 3000; i++)
        {
            try
            {
                await p1.SendBinaryAsync(message);
            }
            catch (WebSocketException)
            {
                break; // Server already gave up on us; the close frame is still waiting to be read.
            }
        }
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, await p1.SkipToCloseAsync());
    }

    [Fact]
    public async Task Normal_game_traffic_is_not_rate_limited()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        (RelayClient p1, RelayClient p2, _) = await host.PairAsync("STEADY");
        byte[][] burst = ProtocolTests.Messages(seed: 3, count: 500); // Within the 600-message burst.
        await Task.WhenAll(ProtocolTests.SendAllAsync(p1, burst), ProtocolTests.ExpectAllAsync(p2, burst));
        Assert.Equal(WebSocketState.Open, p1.Socket.State);
    }

    [Fact]
    public async Task Idle_connection_is_closed_with_4003()
    {
        await using RelayHost host = await RelayHost.StartAsync(RelayHost.TestOptions with { IdleTimeout = TimeSpan.FromMilliseconds(400) });
        RelayClient p1 = await host.JoinAsync("IDLE", 0);
        Assert.Equal((WebSocketCloseStatus)4003, await p1.ReceiveCloseAsync());
        await RelayHost.WaitUntilAsync(() => host.Server.RoomCount == 0, "the idle room to be deleted");
    }

    [Fact]
    public async Task Text_keepalives_keep_a_waiting_player_connected()
    {
        TimeSpan idle = TimeSpan.FromSeconds(1);
        await using RelayHost host = await RelayHost.StartAsync(RelayHost.TestOptions with { IdleTimeout = idle });
        RelayClient p1 = await host.JoinAsync("WAIT", 0);

        // Wait for a partner for 2 idle timeouts, pinging every 100 ms (the cadence is what is under test;
        // ten pings per timeout leaves room for a busy test machine).
        DateTime until = DateTime.UtcNow + idle * 2;
        while (DateTime.UtcNow < until)
        {
            await p1.SendTextAsync("{\"type\":\"ping\"}");
            await Task.Delay(100);
        }

        RelayClient p2 = await host.JoinAsync("WAIT", 1);
        long seed = await p1.ReceiveStartAsync(0); // Still connected: the next message is start, not a close.
        Assert.Equal(seed, await p2.ReceiveStartAsync(1));
    }

    [Fact]
    public async Task A_partner_that_stops_reading_is_dropped_without_blocking_the_sender()
    {
        // No rate limit here: the sender must outrun every buffer between the relay and the stalled phone.
        RelayOptions options = RelayHost.TestOptions with
        {
            OutgoingQueueCapacity = 16,
            RateLimitBurst = 10_000_000,
            RateLimitPerSecond = 10_000_000,
        };
        await using RelayHost host = await RelayHost.StartAsync(options);
        RelayClient sender = await host.JoinAsync("STALL", 0);
        await host.ConnectStalledAsync("STALL"); // Never reads anything, not even its welcome.
        await sender.ReceiveStartAsync(0);

        Task<JsonElement> peerLeft = sender.ReceiveJsonAsync("peer_left");
        var payload = new byte[256];
        int sent = 0;
        while (!peerLeft.IsCompleted && sent < 400_000) // 100 MB at most; a few hundred KB fill the buffers
        {
            await sender.SendBinaryAsync(payload);
            sent++;
        }
        await peerLeft;
        Assert.Equal(1, host.Server.SendQueueOverflows); // Dropped for not reading, not for anything else.

        // The stalled connection was torn down completely (its request finished and released its slot)...
        await RelayHost.WaitUntilAsync(() => host.Server.ActiveConnections == 1, "the stalled connection to be released");
        // ...and the sender kept its slot and can be paired again.
        Assert.Equal(WebSocketState.Open, sender.Socket.State);
        RelayClient next = await host.JoinAsync("STALL", 1);
        long seed = await sender.ReceiveStartAsync(0);
        Assert.Equal(seed, await next.ReceiveStartAsync(1));
        await next.SendBinaryAsync(new byte[] { 3 });
        Assert.Equal(new byte[] { 3 }, await sender.ReceiveBinaryAsync());
    }

    [Fact]
    public async Task Room_limit_gives_busy_and_close_4002()
    {
        await using RelayHost host = await RelayHost.StartAsync(RelayHost.TestOptions with { MaxRooms = 1 });
        RelayClient p1 = await host.JoinAsync("ROOMA", 0);

        RelayClient other = await host.ConnectAsync("ROOMB");
        await other.ReceiveJsonAsync("busy");
        Assert.Equal((WebSocketCloseStatus)4002, await other.ReceiveCloseAsync());

        // Joining a room that already exists is still fine.
        RelayClient p2 = await host.JoinAsync("ROOMA", 1);
        await p1.ReceiveStartAsync(0);
        await p2.ReceiveStartAsync(1);
    }

    [Fact]
    public async Task Connection_limit_gives_busy_and_close_4002_until_a_player_leaves()
    {
        await using RelayHost host = await RelayHost.StartAsync(RelayHost.TestOptions with { MaxConnections = 2 });
        (RelayClient p1, RelayClient p2, _) = await host.PairAsync("HOUSE1");

        RelayClient third = await host.ConnectAsync("OTHER");
        await third.ReceiveJsonAsync("busy");
        Assert.Equal((WebSocketCloseStatus)4002, await third.ReceiveCloseAsync());

        await p2.CloseAsync();
        await p1.ReceiveJsonAsync("peer_left");
        await RelayHost.WaitUntilAsync(() => host.Server.ActiveConnections == 1, "the connection to be released");
        await host.JoinAsync("OTHER", 0);
    }
}
