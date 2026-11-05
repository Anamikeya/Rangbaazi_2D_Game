using System;
using System.Threading;
using System.Threading.Tasks;

namespace Rangbaazi.Social.Voice;

/// <summary>
/// Thin adapter over a voice provider (planned: Epic Online Services RTC voice; see
/// docs/design/B10-chat-and-voice.md). The provider owns capture, Opus encoding, echo
/// cancellation, noise suppression and relay servers. Voice never shares the rollback P2P socket.
/// Game code should use <see cref="VoiceController"/> rather than calling this directly.
/// </summary>
public interface IVoiceService
{
    Task JoinAsync(VoiceChannelId channel, CancellationToken ct = default);
    Task LeaveAsync(VoiceChannelId channel, CancellationToken ct = default);

    void SetTransmitting(bool transmitting);
    void SetInputVolume(int percent);
    void SetOutputVolume(int percent);
    void SetPlayerMuted(PlayerId player, bool muted);
    void SetPlayerVolume(PlayerId player, int percent);

    event Action<VoiceChannelId, PlayerId>? ParticipantJoined;
    event Action<VoiceChannelId, PlayerId>? ParticipantLeft;
    event Action<PlayerId, bool>? SpeakingChanged;
}

/// <summary>No-op provider for local couch play, offline builds and platforms without voice.</summary>
public sealed class NullVoiceService : IVoiceService
{
    public Task JoinAsync(VoiceChannelId channel, CancellationToken ct = default) => Task.CompletedTask;
    public Task LeaveAsync(VoiceChannelId channel, CancellationToken ct = default) => Task.CompletedTask;
    public void SetTransmitting(bool transmitting) { }
    public void SetInputVolume(int percent) { }
    public void SetOutputVolume(int percent) { }
    public void SetPlayerMuted(PlayerId player, bool muted) { }
    public void SetPlayerVolume(PlayerId player, int percent) { }

#pragma warning disable CS0067 // events never raised by the null provider
    public event Action<VoiceChannelId, PlayerId>? ParticipantJoined;
    public event Action<VoiceChannelId, PlayerId>? ParticipantLeft;
    public event Action<PlayerId, bool>? SpeakingChanged;
#pragma warning restore CS0067
}
