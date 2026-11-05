namespace Rangbaazi.Social;

/// <summary>Platform-neutral account id (backend user id, not a display name).</summary>
public readonly record struct PlayerId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>How the local player relates to someone else. Drives chat/voice visibility rules.</summary>
public enum Relationship
{
    Stranger,
    Opponent,
    Teammate,
    PartyMember,
    Friend,
    Self,
}
