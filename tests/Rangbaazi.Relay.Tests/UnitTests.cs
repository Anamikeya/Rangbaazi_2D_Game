using System.Diagnostics;
using Rangbaazi.Relay.Arena;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace Rangbaazi.Relay.Tests;

/// <summary>The relay's building blocks in isolation.</summary>
public sealed class UnitTests
{
    [Theory]
    [InlineData("rajat", "rajat")]
    [InlineData("nobody", "shahn")]
    [InlineData(null, "shahn")]
    [InlineData("mix.rajat.shahn.akshat.suhani.Rocky", "mix.rajat.shahn.akshat.suhani.Rocky")]
    [InlineData("mix.rajat.shahn.akshat.suhani.", "mix.rajat.shahn.akshat.suhani.")]
    [InlineData("mix.rajat.shahn.akshat.suhani.<b>Ro!cky 12345678", "mix.rajat.shahn.akshat.suhani.bRocky 12345")]
    [InlineData("mix.rajat.shahn.akshat.nobody.Rocky", "shahn")]
    [InlineData("mix.rajat.shahn.akshat.Rocky", "shahn")]
    [InlineData("mix.rajat.shahn.akshat.suhani.Ro.cky", "shahn")]
    public void Characters_are_one_of_the_cast_or_a_well_formed_mix(string? raw, string expected)
    {
        Assert.Equal(expected, ArenaLimits.CleanCharacter(raw));
    }

    [Fact]
    public void A_mixed_fighter_counts_as_its_head()
    {
        Assert.Equal("rajat", ArenaLimits.Look("mix.rajat.shahn.akshat.suhani.Rocky"));
        Assert.Equal("salik", ArenaLimits.Look("salik"));
    }

    [Theory]
    [InlineData("abcd", "ABCD")]
    [InlineData("Ab12", "AB12")]
    [InlineData("ZZZZ9999", "ZZZZ9999")]
    public void Valid_room_codes_are_upper_cased(string raw, string expected)
    {
        Assert.True(RoomCode.TryNormalize(raw, out string code));
        Assert.Equal(expected, code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("abcdefghi")]
    [InlineData("ab_c")]
    [InlineData("abçd")]
    [InlineData("abc١")] // Arabic-Indic digit: a digit, but not ASCII.
    public void Invalid_room_codes_are_rejected(string? raw)
    {
        Assert.False(RoomCode.TryNormalize(raw, out _));
    }

    [Fact]
    public void Token_bucket_allows_the_burst_then_refills_at_the_rate()
    {
        const long perSecond = 1000; // timestamps are milliseconds here
        var bucket = new TokenBucket(capacity: 600, refillPerSecond: 300, nowTimestamp: 0, timestampsPerSecond: perSecond);
        for (int i = 0; i < 600; i++) Assert.True(bucket.TryTake(0), $"token {i}");
        Assert.False(bucket.TryTake(0));

        Assert.False(bucket.TryTake(3)); // 0.9 tokens after 3 ms
        Assert.True(bucket.TryTake(4)); // 1.2 tokens after 4 ms
        Assert.False(bucket.TryTake(4));

        // Refill is capped at the burst size, however long the pause.
        int taken = 0;
        while (bucket.TryTake(1_000_000)) taken++;
        Assert.Equal(600, taken);
    }

    [Fact]
    public void Seeds_are_in_range_and_never_repeat_the_previous_one()
    {
        long previous = 0;
        for (int i = 0; i < 1000; i++)
        {
            long seed = SeedGenerator.Next(previous);
            Assert.InRange(seed, 1L, (1L << 53) - 1);
            Assert.NotEqual(previous, seed);
            previous = seed;
        }
    }

    [Fact]
    public void Messages_have_the_protocol_shapes()
    {
        Assert.Equal("{\"type\":\"welcome\",\"slot\":1,\"room\":\"ABCD\"}", Utf8(RelayMessages.Welcome(1, "ABCD")));
        Assert.Equal("{\"type\":\"start\",\"seed\":\"9007199254740991\",\"slot\":0}", Utf8(RelayMessages.Start(SeedGenerator.MaxSeed, 0)));
        Assert.Equal("{\"type\":\"peer_left\"}", Utf8(RelayMessages.PeerLeft));
        Assert.Equal("{\"type\":\"full\"}", Utf8(RelayMessages.Full));
        Assert.Equal("{\"type\":\"busy\"}", Utf8(RelayMessages.Busy));
        JsonElement error = JsonDocument.Parse(RelayMessages.Error("bad \"code\"")).RootElement;
        Assert.Equal("error", error.GetProperty("type").GetString());
        Assert.Equal("bad \"code\"", error.GetProperty("message").GetString());
    }

    [Fact]
    public void Options_come_from_the_environment_with_defaults()
    {
        RelayOptions defaults = RelayOptions.FromEnvironment(_ => null);
        Assert.Equal(8080, defaults.Port);
        Assert.Null(defaults.OriginSecret);
        Assert.Equal(1000, defaults.MaxRooms);
        Assert.Equal(2000, defaults.MaxConnections);
        Assert.Equal(256, defaults.MaxMessageBytes);
        Assert.Equal(TimeSpan.FromSeconds(60), defaults.IdleTimeout);
        Assert.Equal(TimeSpan.FromSeconds(15), defaults.KeepAliveInterval);

        var env = new Dictionary<string, string>
        {
            ["PORT"] = "9000",
            ["RELAY_ORIGIN_SECRET"] = "s3cret",
            ["MAX_ROOMS"] = " 10 ",
            ["MAX_CONNECTIONS"] = "20",
        };
        RelayOptions set = RelayOptions.FromEnvironment(name => env.GetValueOrDefault(name));
        Assert.Equal(9000, set.Port);
        Assert.Equal("s3cret", set.OriginSecret);
        Assert.Equal(10, set.MaxRooms);
        Assert.Equal(20, set.MaxConnections);

        Assert.Null(RelayOptions.FromEnvironment(name => name == "RELAY_ORIGIN_SECRET" ? "" : null).OriginSecret);
    }

    [Theory]
    [InlineData("PORT", "http")]
    [InlineData("PORT", "70000")]
    [InlineData("MAX_ROOMS", "0")]
    [InlineData("MAX_CONNECTIONS", "-5")]
    public void Bad_environment_values_are_rejected(string name, string value)
    {
        Assert.Throws<FormatException>(() => RelayOptions.FromEnvironment(n => n == name ? value : null));
    }

    [Fact]
    public void Origin_secret_check_matches_only_the_exact_value()
    {
        var check = new OriginSecretCheck("s3cret");
        Assert.True(check.Matches("s3cret"));
        Assert.False(check.Matches(null));
        Assert.False(check.Matches(""));
        Assert.False(check.Matches("s3cre"));
        Assert.False(check.Matches("s3cret "));
        Assert.False(check.Matches("S3CRET"));
    }

    [Fact]
    public async Task A_receiver_that_stops_reading_is_dropped_when_its_queue_overflows()
    {
        var options = RelayHost.TestOptions with { OutgoingQueueCapacity = 4 };
        using var server = new RelayServer(options, NullLogger<RelayServer>.Instance);
        var socket = new StuckWebSocket();
        var connection = new RelayConnection(socket, server);

        // The send loop is not running, as if every send were stuck: the queue fills and nothing waits.
        var enqueueTime = Stopwatch.StartNew();
        for (int i = 0; i < 4; i++) connection.Enqueue(new byte[] { (byte)i }, WebSocketMessageType.Binary);
        Assert.False(socket.Aborted.Task.IsCompleted);

        connection.Enqueue(new byte[] { 4 }, WebSocketMessageType.Binary);
        await socket.Aborted.Task.WaitAsync(RelayClient.DefaultTimeout);
        Assert.True(enqueueTime.Elapsed < TimeSpan.FromSeconds(5), "Enqueue must never wait on the receiver");

        // Later messages to the dead connection are dropped quietly.
        connection.Enqueue(new byte[] { 5 }, WebSocketMessageType.Binary);
    }

    [Fact]
    public async Task Queueing_for_a_stuck_partner_returns_at_once_and_aborts_it_on_overflow()
    {
        var options = RelayHost.TestOptions with { OutgoingQueueCapacity = 8 };
        using var server = new RelayServer(options, NullLogger<RelayServer>.Instance);
        var stuck = new StuckWebSocket();
        var slow = new RelayConnection(stuck, server);
        Assert.Equal(JoinOutcome.Joined, server.Join("SLOW", slow));
        var fast = new RelayConnection(new StuckWebSocket(), server);
        Assert.Equal(JoinOutcome.Joined, server.Join("SLOW", fast));

        // What fast's receive loop does per binary message: enqueue on the partner. 100 of them return at once.
        // (LimitsTests has the end-to-end version with a real stalled client.)
        for (int i = 0; i < 100; i++) slow.Enqueue(new byte[] { 1 }, WebSocketMessageType.Binary);
        await stuck.Aborted.Task.WaitAsync(RelayClient.DefaultTimeout);
        Assert.Equal(1, server.SendQueueOverflows);
    }

    private static string Utf8(byte[] bytes) => System.Text.Encoding.UTF8.GetString(bytes);

    /// <summary>A socket whose sends and receives never complete, standing in for a frozen client.</summary>
    private sealed class StuckWebSocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;

        public TaskCompletionSource Aborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => _state;

        public override string? SubProtocol => null;

        public override void Abort()
        {
            _state = WebSocketState.Aborted;
            Aborted.TrySetResult();
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            new TaskCompletionSource<WebSocketReceiveResult>().Task;

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            new TaskCompletionSource().Task;
    }
}
