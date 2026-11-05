using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Rangbaazi.Social.Safety;

namespace Rangbaazi.Social.Voice;

/// <summary>
/// Game-facing voice logic on top of any <see cref="IVoiceService"/>: enforces the player's
/// <see cref="SocialPolicy"/> per channel kind, drives push-to-talk, and keeps blocked or
/// locally muted players silent (including people who join later).
/// </summary>
public sealed class VoiceController : IDisposable
{
    private readonly IVoiceService _service;
    private readonly BlockList _blocks;
    private readonly HashSet<VoiceChannelId> _joined = new();
    private readonly HashSet<PlayerId> _localMutes = new();
    private bool _pushToTalkHeld;

    public VoiceController(IVoiceService service, SocialPolicy policy, BlockList blocks, VoiceSettings? settings = null)
    {
        _service = service;
        _blocks = blocks;
        Policy = policy;
        Settings = (settings ?? new VoiceSettings()).Clamped();

        _blocks.Changed += OnBlockChanged;
        _service.ParticipantJoined += OnParticipantJoined;
        _service.SetInputVolume(Settings.InputVolume);
        _service.SetOutputVolume(Settings.OutputVolume);
    }

    public SocialPolicy Policy { get; private set; }
    public VoiceSettings Settings { get; private set; }
    public bool IsTransmitting { get; private set; }
    public IReadOnlyCollection<VoiceChannelId> JoinedChannels => _joined;

    public static bool Allows(SocialPolicy policy, VoiceChannelKind kind) => kind switch
    {
        VoiceChannelKind.Party => policy.Voice >= VoiceChatScope.PartyOnly,
        VoiceChannelKind.Team => policy.Voice >= VoiceChatScope.PartyAndTeam,
        _ => policy.Voice >= VoiceChatScope.Everyone,
    };

    public async Task<bool> TryJoinAsync(VoiceChannelId channel, CancellationToken ct = default)
    {
        if (!Allows(Policy, channel.Kind)) return false;
        if (_joined.Contains(channel)) return true;

        await _service.JoinAsync(channel, ct).ConfigureAwait(false);
        _joined.Add(channel);
        UpdateTransmit();
        return true;
    }

    public async Task LeaveAsync(VoiceChannelId channel, CancellationToken ct = default)
    {
        if (!_joined.Remove(channel)) return;
        await _service.LeaveAsync(channel, ct).ConfigureAwait(false);
        UpdateTransmit();
    }

    /// <summary>Apply new settings from the options menu or a parental control change. Leaves channels no longer allowed.</summary>
    public async Task SetPolicyAsync(SocialPolicy policy, CancellationToken ct = default)
    {
        Policy = policy;
        foreach (VoiceChannelId ch in _joined.Where(c => !Allows(policy, c.Kind)).ToList())
            await LeaveAsync(ch, ct).ConfigureAwait(false);
    }

    public void ApplySettings(VoiceSettings settings)
    {
        Settings = settings.Clamped();
        _service.SetInputVolume(Settings.InputVolume);
        _service.SetOutputVolume(Settings.OutputVolume);
        UpdateTransmit();
    }

    /// <summary>Call from input handling every time the push-to-talk key goes down or up.</summary>
    public void SetPushToTalk(bool held)
    {
        _pushToTalkHeld = held;
        UpdateTransmit();
    }

    /// <summary>Local mute: silence someone for me only (lighter than a block).</summary>
    public void SetPlayerMuted(PlayerId player, bool muted)
    {
        if (muted) _localMutes.Add(player); else _localMutes.Remove(player);
        _service.SetPlayerMuted(player, ShouldBeSilent(player));
    }

    public void SetPlayerVolume(PlayerId player, int percent) => _service.SetPlayerVolume(player, Math.Clamp(percent, 0, 200));

    private bool ShouldBeSilent(PlayerId player) => _blocks.IsBlocked(player) || _localMutes.Contains(player);

    private void UpdateTransmit()
    {
        bool on = _joined.Count > 0 && Settings.Mode switch
        {
            TransmitMode.PushToTalk => _pushToTalkHeld,
            TransmitMode.VoiceActivated => true,
            _ => false,
        };
        if (on == IsTransmitting) return;
        IsTransmitting = on;
        _service.SetTransmitting(on);
    }

    private void OnBlockChanged(PlayerId player, bool blocked) => _service.SetPlayerMuted(player, ShouldBeSilent(player));

    private void OnParticipantJoined(VoiceChannelId channel, PlayerId player)
    {
        if (ShouldBeSilent(player)) _service.SetPlayerMuted(player, true);
    }

    public void Dispose()
    {
        _blocks.Changed -= OnBlockChanged;
        _service.ParticipantJoined -= OnParticipantJoined;
    }
}
