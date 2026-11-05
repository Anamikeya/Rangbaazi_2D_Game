using System;

namespace Rangbaazi.Social.Voice;

public enum VoiceChannelKind
{
    Party,      // invited friends, follows you between matches
    Team,       // your side in Gang War / squads
    Match,      // everyone in the room (custom rooms only by default)
    Proximity,  // Box Office Royale: hear fighters near you (Season 1+)
}

public readonly record struct VoiceChannelId(VoiceChannelKind Kind, string Key)
{
    public override string ToString() => $"{Kind}:{Key}";
}

public enum TransmitMode
{
    Off,
    PushToTalk,      // default on PC and console
    VoiceActivated,  // default on mobile (no spare button mid-fight)
}

public sealed record VoiceSettings
{
    public TransmitMode Mode { get; init; } = TransmitMode.PushToTalk;
    public int InputVolume { get; init; } = 100;   // percent, 0..200
    public int OutputVolume { get; init; } = 100;  // percent, 0..200

    public VoiceSettings Clamped() => this with
    {
        InputVolume = Math.Clamp(InputVolume, 0, 200),
        OutputVolume = Math.Clamp(OutputVolume, 0, 200),
    };
}
