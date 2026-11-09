using System.Net;
using System.Net.WebSockets;

namespace Rangbaazi.Relay.Tests;

/// <summary>The relay protocol end to end: real app on a loopback port, real WebSocket clients.</summary>
public sealed class ProtocolTests
{
    [Fact]
    public async Task Health_returns_ok()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        using HttpResponseMessage response = await host.Http.GetAsync("/relay/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Plain_http_request_to_a_room_is_400_and_unknown_paths_are_404()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        using HttpResponseMessage room = await host.Http.GetAsync("/relay/room/TEST");
        Assert.Equal(HttpStatusCode.BadRequest, room.StatusCode);
        using HttpResponseMessage other = await host.Http.GetAsync("/somewhere-else");
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
    }

    [Fact]
    public async Task Players_get_welcome_with_slot_0_then_1()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        RelayClient p1 = await host.JoinAsync("ROOM1", 0);
        RelayClient p2 = await host.JoinAsync("ROOM1", 1);
        Assert.Equal(WebSocketState.Open, p1.Socket.State);
        Assert.Equal(WebSocketState.Open, p2.Socket.State);
    }

    [Fact]
    public async Task Pairing_sends_both_players_start_with_one_seed_and_their_own_slots()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        RelayClient p1 = await host.JoinAsync("SEED", 0);
        RelayClient p2 = await host.JoinAsync("SEED", 1);
        long seed1 = await p1.ReceiveStartAsync(0);
        long seed2 = await p2.ReceiveStartAsync(1);
        Assert.Equal(seed1, seed2);
    }

    [Fact]
    public async Task Binary_messages_are_forwarded_both_ways_in_order_and_byte_exact()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        (RelayClient p1, RelayClient p2, _) = await host.PairAsync("FWD1");

        byte[][] fromP1 = Messages(seed: 1, count: 200);
        byte[][] fromP2 = Messages(seed: 2, count: 200);

        await Task.WhenAll(
            SendAllAsync(p1, fromP1),
            SendAllAsync(p2, fromP2),
            ExpectAllAsync(p2, fromP1),
            ExpectAllAsync(p1, fromP2));
    }

    [Fact]
    public async Task Binary_without_a_partner_is_dropped()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        RelayClient p1 = await host.JoinAsync("ALONE", 0);
        await p1.SendBinaryAsync(new byte[] { 1, 2, 3 });
        await RelayHost.WaitUntilAsync(() => host.Server.DroppedWithoutPeer == 1, "the lone message to be dropped");

        RelayClient p2 = await host.JoinAsync("ALONE", 1);
        await p1.ReceiveStartAsync(0);
        await p2.ReceiveStartAsync(1);
        await p1.SendBinaryAsync(new byte[] { 9 });
        Assert.Equal(new byte[] { 9 }, await p2.ReceiveBinaryAsync()); // Not the dropped { 1, 2, 3 }.
    }

    [Fact]
    public async Task Text_frames_from_players_are_ignored()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        (RelayClient p1, RelayClient p2, _) = await host.PairAsync("TEXT");
        await p1.SendTextAsync("{\"type\":\"hello\"}");
        await p1.SendBinaryAsync(new byte[] { 7 });
        Assert.Equal(new byte[] { 7 }, await p2.ReceiveBinaryAsync());
    }

    [Fact]
    public async Task Fragmented_message_is_reassembled_and_forwarded_as_one()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        (RelayClient p1, RelayClient p2, _) = await host.PairAsync("FRAG");
        byte[] first = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        byte[] second = Enumerable.Range(100, 100).Select(i => (byte)i).ToArray();
        await p1.SendBinaryAsync(first, endOfMessage: false);
        await p1.SendBinaryAsync(second, endOfMessage: true);
        Assert.Equal(first.Concat(second).ToArray(), await p2.ReceiveBinaryAsync());
    }

    [Fact]
    public async Task Third_player_gets_full_and_close_4001()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        (RelayClient p1, RelayClient p2, _) = await host.PairAsync("BUSYROOM");

        RelayClient p3 = await host.ConnectAsync("BUSYROOM");
        await p3.ReceiveJsonAsync("full");
        Assert.Equal((WebSocketCloseStatus)4001, await p3.ReceiveCloseAsync());

        // The pair is unaffected.
        await p1.SendBinaryAsync(new byte[] { 42 });
        Assert.Equal(new byte[] { 42 }, await p2.ReceiveBinaryAsync());
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("ABCDEFGHI")]
    [InlineData("ab-cd")]
    [InlineData("ab cd")]
    [InlineData("abcd/efgh")]
    [InlineData("")]
    public async Task Bad_code_gets_error_and_close_4000(string code)
    {
        await using RelayHost host = await RelayHost.StartAsync();
        RelayClient client = await host.ConnectAsync(code);
        var error = await client.ReceiveJsonAsync("error");
        Assert.False(string.IsNullOrEmpty(error.GetProperty("message").GetString()));
        Assert.Equal((WebSocketCloseStatus)4000, await client.ReceiveCloseAsync());
        Assert.Equal(0, host.Server.RoomCount);
    }

    [Fact]
    public async Task Closing_player_sends_peer_left_and_a_new_pairing_gets_a_new_seed()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        (RelayClient p1, RelayClient p2, long firstSeed) = await host.PairAsync("AGAIN");

        await p2.CloseAsync();
        await p1.ReceiveJsonAsync("peer_left");
        Assert.Equal(WebSocketState.Open, p1.Socket.State);

        RelayClient p3 = await host.JoinAsync("AGAIN", 1);
        long secondSeed = await p1.ReceiveStartAsync(0);
        Assert.Equal(secondSeed, await p3.ReceiveStartAsync(1));
        Assert.NotEqual(firstSeed, secondSeed);

        await p3.SendBinaryAsync(new byte[] { 5, 6 });
        Assert.Equal(new byte[] { 5, 6 }, await p1.ReceiveBinaryAsync());
    }

    [Fact]
    public async Task Aborted_player_sends_peer_left()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        (RelayClient p1, RelayClient p2, _) = await host.PairAsync("ABORT");
        p2.Socket.Abort();
        await p1.ReceiveJsonAsync("peer_left");
    }

    [Fact]
    public async Task Rejoining_player_takes_the_lowest_free_slot()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        (RelayClient p1, RelayClient p2, _) = await host.PairAsync("SLOTS");

        await p1.CloseAsync();
        await p2.ReceiveJsonAsync("peer_left");

        RelayClient p3 = await host.JoinAsync("SLOTS", 0);
        long seed = await p3.ReceiveStartAsync(0);
        Assert.Equal(seed, await p2.ReceiveStartAsync(1));
    }

    [Fact]
    public async Task Empty_room_is_deleted()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        (RelayClient p1, RelayClient p2, _) = await host.PairAsync("GONE");
        Assert.Equal(1, host.Server.RoomCount);
        await p1.CloseAsync();
        await p2.CloseAsync();
        await RelayHost.WaitUntilAsync(() => host.Server.RoomCount == 0 && host.Server.ActiveConnections == 0, "the room to be deleted");
    }

    [Fact]
    public async Task Room_codes_are_case_insensitive()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        RelayClient p1 = await host.JoinAsync("abcd", 0); // welcome says "ABCD"
        RelayClient p2 = await host.JoinAsync("AbCd", 1);
        long seed = await p1.ReceiveStartAsync(0);
        Assert.Equal(seed, await p2.ReceiveStartAsync(1));
        Assert.Equal(1, host.Server.RoomCount);
    }

    [Fact]
    public async Task Many_rooms_relay_in_parallel()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        await Task.WhenAll(Enumerable.Range(0, 24).Select(async i =>
        {
            (RelayClient p1, RelayClient p2, _) = await host.PairAsync("PAR" + i);
            byte[][] a = Messages(seed: 100 + i, count: 40);
            byte[][] b = Messages(seed: 200 + i, count: 40);
            await Task.WhenAll(SendAllAsync(p1, a), SendAllAsync(p2, b), ExpectAllAsync(p2, a), ExpectAllAsync(p1, b));
            await p1.CloseAsync();
            await p2.ReceiveJsonAsync("peer_left");
        }));
        Assert.Equal(24, host.Server.RoomCount);
    }

    [Fact]
    public async Task Server_shutdown_closes_players_with_1001()
    {
        RelayHost host = await RelayHost.StartAsync();
        await using (host)
        {
            RelayClient p1 = await host.JoinAsync("BYEBYE", 0);
            Task stopping = host.StopAsync();
            Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, await p1.ReceiveCloseAsync());
            await stopping.WaitAsync(RelayClient.DefaultTimeout);
        }
    }

    [Fact]
    public async Task A_player_arriving_after_shutdown_began_is_closed_with_1001()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        RelayClient early = await host.JoinAsync("LATE", 0);
        host.Server.CloseAll(); // What ApplicationStopping does, before the server stops accepting.
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, await early.SkipToCloseAsync());

        RelayClient late = await host.ConnectAsync("LATE");
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, await late.SkipToCloseAsync());
        await RelayHost.WaitUntilAsync(() => host.Server.RoomCount == 0 && host.Server.ActiveConnections == 0, "everyone to be gone");
    }

    /// <summary>Deterministic test payloads covering the edge sizes 0, 1, 255 and 256 bytes.</summary>
    internal static byte[][] Messages(int seed, int count)
    {
        var random = new Random(seed);
        int[] sizes = { 0, 1, 255, 256 };
        return Enumerable.Range(0, count).Select(i =>
        {
            int size = i < sizes.Length ? sizes[i] : random.Next(1, 257);
            var data = new byte[size];
            random.NextBytes(data);
            return data;
        }).ToArray();
    }

    internal static async Task SendAllAsync(RelayClient client, byte[][] messages)
    {
        foreach (byte[] message in messages) await client.SendBinaryAsync(message);
    }

    internal static async Task ExpectAllAsync(RelayClient client, byte[][] messages)
    {
        for (int i = 0; i < messages.Length; i++)
        {
            byte[] received = await client.ReceiveBinaryAsync();
            Assert.True(messages[i].AsSpan().SequenceEqual(received), $"Message {i} differs (sent {messages[i].Length} bytes, got {received.Length})");
        }
    }
}
