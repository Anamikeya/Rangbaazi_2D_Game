using System;

namespace Rangbaazi.Social.Chat;

public enum ChatChannelKind
{
    Lobby,     // custom room / pre-match lobby
    Party,     // your invited party, persists across matches
    Team,      // your side in Gang War / squads
    Match,     // everyone in the match ("all chat"); off during ranked play by default
    Whisper,   // 1:1 direct message (friends)
    System,    // game/server notices, never filtered by player settings
}

public readonly record struct ChatChannelId(ChatChannelKind Kind, string Key)
{
    public static ChatChannelId System { get; } = new(ChatChannelKind.System, "system");
    public override string ToString() => $"{Kind}:{Key}";
}

public enum ChatContentKind
{
    Text,
    QuickChat,
    System,
}

public sealed record ChatMessage(
    string Id,
    ChatChannelId Channel,
    PlayerId Sender,
    string SenderName,
    ChatContentKind Kind,
    string Text,
    string? QuickChatId,
    DateTimeOffset SentAt);

public enum ChatSendResult
{
    Sent,
    Empty,
    NotInChannel,
    BlockedByPolicy,
    RateLimited,
    UnknownQuickChat,
}
