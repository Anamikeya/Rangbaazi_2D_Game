using System;
using System.Net;
using System.Net.Sockets;
using Backdash.Options;
using Rangbaazi.Sim;

namespace Rangbaazi.Net;

/// <summary>Settings for one online 1v1. Both players must use equal <see cref="Rules"/> and opposite slots.</summary>
public sealed record OnlineMatchOptions
{
    /// <summary>The other player's IPv4 address and port, exactly as their packets will arrive.</summary>
    public required IPEndPoint Peer { get; init; }

    /// <summary>0 = this player is P1, 1 = P2. The other player must pick the other one.</summary>
    public required int LocalSlot { get; init; }

    public MatchRules Rules { get; init; } = new() { Stage = Stages.Graybox };

    /// <summary>Frames between pressing a button and the game using it (GDD: 1-2 online).</summary>
    public int InputDelayFrames { get; init; } = 2;

    /// <summary>How far ahead the game may run on predicted input; rollbacks go at most one less back.</summary>
    public int PredictionFrames { get; init; } = 8;

    /// <summary>Silence after which the opponent is shown as "connection interrupted".</summary>
    public TimeSpan DisconnectNotifyStart { get; init; } = TimeSpan.FromMilliseconds(750);

    /// <summary>Silence after which the opponent counts as disconnected and the match ends.</summary>
    public TimeSpan DisconnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Ticks to wait for Backdash to synchronize after both players said hello.</summary>
    public int SyncTimeoutTicks { get; init; } = 20 * Simulation.TicksPerSecond;

    /// <summary>
    /// Ticks the match may stay frozen waiting for the opponent's input while their connection is
    /// still alive (a frozen or paused game), before giving up.
    /// </summary>
    public int StallTimeoutTicks { get; init; } = 15 * Simulation.TicksPerSecond;

    /// <summary>Ticks between checksum exchanges (desync detection).</summary>
    public int ChecksumIntervalTicks { get; init; } = 30;

    /// <summary>Ticks between hellos while connecting.</summary>
    public int HelloIntervalTicks { get; init; } = 10;

    /// <summary>Test hook: runs after every simulated frame, re-simulations included.</summary>
    internal Action<GameState>? AfterAdvance { get; init; }

    /// <summary>Test hook: last word on Backdash's options (e.g. shorter retry intervals).</summary>
    internal Action<NetcodeOptions>? ConfigureBackdash { get; init; }

    public void Validate()
    {
        if (Peer.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("The opponent's address must be IPv4.");
        if (Peer.Port <= 0) throw new ArgumentException("The opponent's port must be 1-65535.");
        if (LocalSlot is not (0 or 1)) throw new ArgumentException("LocalSlot must be 0 (P1) or 1 (P2).");
        if (PredictionFrames is < 2 or > 16) throw new ArgumentException("PredictionFrames must be 2-16.");
        if (InputDelayFrames < 0 || InputDelayFrames >= PredictionFrames)
            throw new ArgumentException($"InputDelayFrames must be 0-{PredictionFrames - 1}.");
        if (DisconnectNotifyStart <= TimeSpan.Zero || DisconnectTimeout <= DisconnectNotifyStart)
            throw new ArgumentException("DisconnectTimeout must be longer than DisconnectNotifyStart, and both positive.");
        if (SyncTimeoutTicks <= 0 || StallTimeoutTicks <= 0 || ChecksumIntervalTicks <= 0 || HelloIntervalTicks <= 0)
            throw new ArgumentException("Timeouts and intervals must be positive.");
    }
}

public enum OnlinePhase
{
    /// <summary>Saying hello: waiting for the opponent's game, and checking it plays by the same rules.</summary>
    Handshaking,

    /// <summary>Backdash is measuring the connection before the first frame.</summary>
    Synchronizing,

    Running,

    /// <summary>Over; see <see cref="OnlineMatch.EndReason"/>. The network is closed.</summary>
    Ended,
}

public enum OnlineEndReason
{
    None,
    LocalLeft,
    LocalRematch,
    OpponentLeft,
    OpponentRematch,
    OpponentNotResponding,
    OpponentRejected,
    Desync,
    RulesMismatch,
    VersionMismatch,
    SameSide,
    SyncFailed,
    Error,
}
