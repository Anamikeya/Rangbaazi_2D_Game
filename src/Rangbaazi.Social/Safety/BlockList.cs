using System;
using System.Collections.Generic;

namespace Rangbaazi.Social.Safety;

/// <summary>
/// Players the local user has blocked. Shared by chat and voice so one "Block" hides their text,
/// mutes their voice and (via the backend) stops invites. Persisted by the backend adapter.
/// </summary>
public sealed class BlockList
{
    private readonly HashSet<PlayerId> _blocked = new();

    public event Action<PlayerId, bool>? Changed;

    public IReadOnlyCollection<PlayerId> Blocked => _blocked;

    public bool IsBlocked(PlayerId player) => _blocked.Contains(player);

    public void Block(PlayerId player)
    {
        if (_blocked.Add(player)) Changed?.Invoke(player, true);
    }

    public void Unblock(PlayerId player)
    {
        if (_blocked.Remove(player)) Changed?.Invoke(player, false);
    }
}

public enum ReportReason
{
    Harassment,
    HateSpeech,
    Cheating,
    Spam,
    InappropriateName,
    ChildSafety,
    Other,
}
