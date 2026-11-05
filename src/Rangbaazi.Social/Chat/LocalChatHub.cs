using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Rangbaazi.Social.Safety;

namespace Rangbaazi.Social.Chat;

public sealed record ChatReport(PlayerId Reporter, PlayerId Target, ReportReason Reason, string? Note, DateTimeOffset At);

/// <summary>
/// In-process chat "server" for offline development, UI work before the backend exists, and tests.
/// It stands in for the real backend: routes by channel, applies the server-side word filter,
/// and records reports. Each connected client applies its own player's policy and block list.
/// </summary>
public sealed class LocalChatHub
{
    private readonly TimeProvider _time;
    private readonly ChatFilter _serverFilter;
    private readonly List<Client> _clients = new();
    private readonly List<ChatReport> _reports = new();
    private long _nextId;

    public LocalChatHub(ChatFilter? serverFilter = null, TimeProvider? time = null)
    {
        _serverFilter = serverFilter ?? ChatFilter.None;
        _time = time ?? TimeProvider.System;
    }

    public IReadOnlyList<ChatReport> Reports => _reports;

    public IChatService Connect(PlayerId player, string displayName, SocialPolicy policy,
        Func<PlayerId, Relationship>? relationshipTo = null)
    {
        var client = new Client(this, player, displayName, policy, relationshipTo ?? (_ => Relationship.Stranger));
        _clients.Add(client);
        return client;
    }

    public void BroadcastSystem(ChatChannelId channel, string text)
    {
        Route(new ChatMessage(NextId(), channel, new PlayerId("system"), "System", ChatContentKind.System,
            text, null, _time.GetUtcNow()));
    }

    private string NextId() => Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture);

    private void Route(ChatMessage message)
    {
        if (message.Kind == ChatContentKind.Text)
            message = message with { Text = _serverFilter.Mask(message.Text) };

        foreach (Client c in _clients.ToArray())
            if (c.IsIn(message.Channel)) c.Receive(message);
    }

    private sealed class Client : IChatService
    {
        private readonly LocalChatHub _hub;
        private readonly string _displayName;
        private readonly Func<PlayerId, Relationship> _relationshipTo;
        private readonly HashSet<ChatChannelId> _joined = new();
        private readonly ChatRateLimiter _limiter;

        public Client(LocalChatHub hub, PlayerId player, string displayName, SocialPolicy policy,
            Func<PlayerId, Relationship> relationshipTo)
        {
            _hub = hub;
            LocalPlayer = player;
            _displayName = displayName;
            Policy = policy;
            _relationshipTo = relationshipTo;
            _limiter = new ChatRateLimiter(hub._time);
            _joined.Add(ChatChannelId.System);
        }

        public PlayerId LocalPlayer { get; }
        public SocialPolicy Policy { get; set; }
        public BlockList Blocks { get; } = new();
        public IReadOnlyCollection<ChatChannelId> JoinedChannels => _joined;
        public event Action<ChatMessage>? MessageReceived;

        public bool IsIn(ChatChannelId channel) => _joined.Contains(channel);

        public Task JoinAsync(ChatChannelId channel, CancellationToken ct = default)
        {
            _joined.Add(channel);
            return Task.CompletedTask;
        }

        public Task LeaveAsync(ChatChannelId channel, CancellationToken ct = default)
        {
            if (channel != ChatChannelId.System) _joined.Remove(channel);
            return Task.CompletedTask;
        }

        public Task<ChatSendResult> SendTextAsync(ChatChannelId channel, string text, CancellationToken ct = default)
        {
            if (!_joined.Contains(channel) || channel.Kind == ChatChannelKind.System) return Result(ChatSendResult.NotInChannel);
            if (!Policy.CanSendFreeText) return Result(ChatSendResult.BlockedByPolicy);

            string clean = ChatFilter.Sanitize(text);
            if (clean.Length == 0) return Result(ChatSendResult.Empty);
            if (!_limiter.TryConsume()) return Result(ChatSendResult.RateLimited);

            _hub.Route(new ChatMessage(_hub.NextId(), channel, LocalPlayer, _displayName, ChatContentKind.Text,
                clean, null, _hub._time.GetUtcNow()));
            return Result(ChatSendResult.Sent);
        }

        public Task<ChatSendResult> SendQuickChatAsync(ChatChannelId channel, string quickChatId, CancellationToken ct = default)
        {
            if (!_joined.Contains(channel) || channel.Kind == ChatChannelKind.System) return Result(ChatSendResult.NotInChannel);
            if (!Policy.CanSendQuickChat) return Result(ChatSendResult.BlockedByPolicy);
            if (!QuickChatCatalog.TryGet(quickChatId, out QuickChatLine line)) return Result(ChatSendResult.UnknownQuickChat);
            if (!_limiter.TryConsume()) return Result(ChatSendResult.RateLimited);

            _hub.Route(new ChatMessage(_hub.NextId(), channel, LocalPlayer, _displayName, ChatContentKind.QuickChat,
                line.DefaultText, line.Id, _hub._time.GetUtcNow()));
            return Result(ChatSendResult.Sent);
        }

        public Task ReportAsync(PlayerId player, ReportReason reason, string? note = null, CancellationToken ct = default)
        {
            _hub._reports.Add(new ChatReport(LocalPlayer, player, reason, note, _hub._time.GetUtcNow()));
            return Task.CompletedTask;
        }

        public void Receive(ChatMessage m)
        {
            if (m.Kind != ChatContentKind.System && m.Sender != LocalPlayer)
            {
                if (Blocks.IsBlocked(m.Sender)) return;
                bool visible = m.Kind == ChatContentKind.QuickChat
                    ? Policy.ShowsQuickChat
                    : Policy.ShowsFreeTextFrom(_relationshipTo(m.Sender));
                if (!visible) return;
            }
            MessageReceived?.Invoke(m);
        }

        private static Task<ChatSendResult> Result(ChatSendResult r) => Task.FromResult(r);
    }
}
