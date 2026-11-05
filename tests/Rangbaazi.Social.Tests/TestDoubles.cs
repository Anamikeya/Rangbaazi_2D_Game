using Rangbaazi.Social.Voice;

namespace Rangbaazi.Social.Tests;

internal sealed class ManualTimeProvider : TimeProvider
{
    private long _ticks;
    private DateTimeOffset _utc = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _ticks;
    public override DateTimeOffset GetUtcNow() => _utc;

    public void Advance(TimeSpan by)
    {
        _ticks += by.Ticks;
        _utc += by;
    }
}

internal sealed class FakeVoiceService : IVoiceService
{
    public HashSet<VoiceChannelId> Joined { get; } = new();
    public bool Transmitting { get; private set; }
    public int TransmitChanges { get; private set; }
    public Dictionary<PlayerId, bool> Muted { get; } = new();
    public int InputVolume { get; private set; }

    public Task JoinAsync(VoiceChannelId channel, CancellationToken ct = default) { Joined.Add(channel); return Task.CompletedTask; }
    public Task LeaveAsync(VoiceChannelId channel, CancellationToken ct = default) { Joined.Remove(channel); return Task.CompletedTask; }
    public void SetTransmitting(bool transmitting) { Transmitting = transmitting; TransmitChanges++; }
    public void SetInputVolume(int percent) => InputVolume = percent;
    public void SetOutputVolume(int percent) { }
    public void SetPlayerMuted(PlayerId player, bool muted) => Muted[player] = muted;
    public void SetPlayerVolume(PlayerId player, int percent) { }

    public event Action<VoiceChannelId, PlayerId>? ParticipantJoined;
    public event Action<VoiceChannelId, PlayerId>? ParticipantLeft;
    public event Action<PlayerId, bool>? SpeakingChanged;

    public void SimulateJoin(VoiceChannelId ch, PlayerId p) => ParticipantJoined?.Invoke(ch, p);
    public void SimulateLeave(VoiceChannelId ch, PlayerId p) => ParticipantLeft?.Invoke(ch, p);
    public void SimulateSpeaking(PlayerId p, bool speaking) => SpeakingChanged?.Invoke(p, speaking);
}
