using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Rangbaazi.Social.Safety;

namespace Rangbaazi.Social.Chat;

/// <summary>
/// Game-facing text chat. Implementations: <see cref="LocalChatHub"/> clients (offline/dev/tests),
/// and later a backend adapter (e.g. Nakama realtime chat). UI code only ever talks to this.
/// </summary>
public interface IChatService
{
    PlayerId LocalPlayer { get; }
    SocialPolicy Policy { get; set; }
    BlockList Blocks { get; }
    IReadOnlyCollection<ChatChannelId> JoinedChannels { get; }

    /// <summary>Raised for every message the local player should see (already filtered and policy-checked).</summary>
    event Action<ChatMessage>? MessageReceived;

    Task JoinAsync(ChatChannelId channel, CancellationToken ct = default);
    Task LeaveAsync(ChatChannelId channel, CancellationToken ct = default);
    Task<ChatSendResult> SendTextAsync(ChatChannelId channel, string text, CancellationToken ct = default);
    Task<ChatSendResult> SendQuickChatAsync(ChatChannelId channel, string quickChatId, CancellationToken ct = default);
    Task ReportAsync(PlayerId player, ReportReason reason, string? note = null, CancellationToken ct = default);
}
