namespace Rangbaazi.Social.Safety;

/// <summary>Age band from the platform account / age gate. Unknown is treated like Under13.</summary>
public enum AgeBand
{
    Unknown,
    Under13,
    Teen13To17,
    Adult,
}

/// <summary>Who can send the local player free text. Ordered least to most permissive.</summary>
public enum TextChatScope
{
    Off,
    QuickChatOnly,
    FriendsOnly,
    Everyone,
}

/// <summary>Which voice channels the local player may join. Ordered least to most permissive.</summary>
public enum VoiceChatScope
{
    Off,
    PartyOnly,
    PartyAndTeam,
    Everyone,
}

/// <summary>
/// Effective chat/voice permissions for the local player. Defaults and ceilings by age band are a
/// design proposal (B10 in docs/design/B10-chat-and-voice.md) and must be reviewed against store,
/// console and regional child-safety rules before launch.
/// </summary>
public sealed record SocialPolicy(TextChatScope Text, VoiceChatScope Voice)
{
    public static SocialPolicy Default(AgeBand band) => band switch
    {
        AgeBand.Adult => new(TextChatScope.Everyone, VoiceChatScope.PartyAndTeam),
        AgeBand.Teen13To17 => new(TextChatScope.FriendsOnly, VoiceChatScope.PartyOnly),
        _ => new(TextChatScope.QuickChatOnly, VoiceChatScope.Off),
    };

    /// <summary>The most a player in this band may opt into.</summary>
    public static SocialPolicy Ceiling(AgeBand band) => band switch
    {
        AgeBand.Adult => new(TextChatScope.Everyone, VoiceChatScope.Everyone),
        AgeBand.Teen13To17 => new(TextChatScope.Everyone, VoiceChatScope.PartyAndTeam),
        _ => new(TextChatScope.QuickChatOnly, VoiceChatScope.Off),
    };

    /// <summary>Player's requested settings, clamped to what their age band allows.</summary>
    public static SocialPolicy For(AgeBand band, TextChatScope? requestedText = null, VoiceChatScope? requestedVoice = null)
    {
        SocialPolicy d = Default(band);
        SocialPolicy c = Ceiling(band);
        TextChatScope text = requestedText ?? d.Text;
        VoiceChatScope voice = requestedVoice ?? d.Voice;
        return new(text > c.Text ? c.Text : text, voice > c.Voice ? c.Voice : voice);
    }

    public bool CanSendFreeText => Text >= TextChatScope.FriendsOnly;
    public bool CanSendQuickChat => Text >= TextChatScope.QuickChatOnly;

    /// <summary>Should a free-text message from someone with this relationship be shown?</summary>
    public bool ShowsFreeTextFrom(Relationship from) => Text switch
    {
        TextChatScope.Everyone => true,
        TextChatScope.FriendsOnly => from is Relationship.Friend or Relationship.Self,
        _ => from == Relationship.Self,
    };

    public bool ShowsQuickChat => Text >= TextChatScope.QuickChatOnly;
}
