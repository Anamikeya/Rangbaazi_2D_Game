using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;

namespace Rangbaazi.Relay.Tests;

/// <summary>Races between connection tasks, driven through in-memory sockets so they can run hot.</summary>
public sealed class ConcurrencyTests
{
    [Fact]
    public async Task No_binary_reaches_the_partner_after_peer_left_when_the_sender_is_closed_from_another_thread()
    {
        // The idle sweeper and server shutdown close a player from outside its receive loop, possibly while
        // that loop is forwarding a message. The forward must land before peer_left or not at all: a late
        // one would reach the partner's next match, after its next start, as if the new player had sent it.
        RelayOptions options = RelayHost.TestOptions with
        {
            OutgoingQueueCapacity = 1 << 22,
            RateLimitBurst = int.MaxValue,
            RateLimitPerSecond = int.MaxValue,
        };
        using var server = new RelayServer(options, NullLogger<RelayServer>.Instance);

        for (int round = 0; round < 300; round++)
        {
            string code = "RACE" + round;
            var partnerSocket = new FakeWebSocket();
            var partner = new RelayConnection(partnerSocket, server);
            Task partnerRun = Task.Run(() => partner.RunAsync(code));
            await FakeWebSocket.WaitUntilAsync(() => partnerSocket.TextCount == 1, "the partner's welcome");

            int receives = 0;
            var senderSocket = new FakeWebSocket
            {
                ReceiveFrom = async buffer =>
                {
                    if (++receives % 64 == 0) await Task.Yield();
                    buffer.Span[0] = 1;
                    return new ValueWebSocketReceiveResult(1, WebSocketMessageType.Binary, true);
                },
            };
            var sender = new RelayConnection(senderSocket, server);
            Task senderRun = Task.Run(() => sender.RunAsync(code));
            await FakeWebSocket.WaitUntilAsync(() => partnerSocket.BinaryCount >= 20, "forwarded messages");

            sender.RequestClose(WebSocketCloseStatus.NormalClosure, "closed from outside"); // What the sweeper does.
            await senderRun.WaitAsync(RelayClient.DefaultTimeout);

            // A new player pairs with the partner. Its start is queued after anything the old sender forwarded.
            var nextSocket = new FakeWebSocket();
            var next = new RelayConnection(nextSocket, server);
            Task nextRun = Task.Run(() => next.RunAsync(code));
            // welcome, start, peer_left, start
            await FakeWebSocket.WaitUntilAsync(() => partnerSocket.TextCount == 4, "the partner's second start");

            SentMessage[] messages = partnerSocket.Sent.ToArray();
            Assert.True(messages[^1].IsJson("start"), $"round {round}: last message is not the second start");
            int peerLeft = Array.FindIndex(messages, m => m.IsJson("peer_left"));
            Assert.True(peerLeft > 0, $"round {round}: no peer_left");
            int lateBinary = Array.FindIndex(messages, peerLeft, m => m.Type == WebSocketMessageType.Binary);
            Assert.True(lateBinary < 0, $"round {round}: binary at {lateBinary}, after peer_left at {peerLeft}");
            Assert.False(partnerSocket.WasAborted);

            partnerSocket.ClientClose();
            nextSocket.ClientClose();
            await Task.WhenAll(partnerRun, nextRun).WaitAsync(RelayClient.DefaultTimeout);
        }
        Assert.Equal(0, server.RoomCount);
    }
}
