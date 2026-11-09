using System;
using System.Globalization;
using System.Net;

namespace Rangbaazi.Relay;

/// <summary>
/// Relay settings. Only <see cref="Port"/>, <see cref="OriginSecret"/>, <see cref="MaxRooms"/> and
/// <see cref="MaxConnections"/> come from the environment; the rest are protocol constants that tests
/// shorten (timeouts) or that the address the tests bind to (loopback).
/// </summary>
public sealed record RelayOptions
{
    public const string PortVariable = "PORT";
    public const string OriginSecretVariable = "RELAY_ORIGIN_SECRET";
    public const string MaxRoomsVariable = "MAX_ROOMS";
    public const string MaxConnectionsVariable = "MAX_CONNECTIONS";
    public const string WebRootVariable = "WEB_ROOT";

    /// <summary>HTTP header CloudFront adds to every origin request when <see cref="OriginSecret"/> is set.</summary>
    public const string OriginSecretHeader = "X-Origin-Secret";

    /// <summary>TCP port to listen on (0 picks a free one, used by tests).</summary>
    public int Port { get; init; } = 8080;

    public IPAddress ListenAddress { get; init; } = IPAddress.Any;

    /// <summary>When set, every request must carry <see cref="OriginSecretHeader"/> with exactly this value.</summary>
    public string? OriginSecret { get; init; }

    /// <summary>Folder with the game's web build to serve at / (see GameFiles), or null to serve no files.</summary>
    public string? WebRoot { get; init; }

    public int MaxRooms { get; init; } = 1000;

    public int MaxConnections { get; init; } = 2000;

    /// <summary>Largest message a player may send, after reassembling fragments.</summary>
    public int MaxMessageBytes { get; init; } = 256;

    /// <summary>Token bucket size: how many messages a player may send in a burst.</summary>
    public int RateLimitBurst { get; init; } = 600;

    /// <summary>Token bucket refill rate in messages per second.</summary>
    public int RateLimitPerSecond { get; init; } = 300;

    /// <summary>Messages waiting to be sent to one player; past this the player is too slow and is dropped.</summary>
    public int OutgoingQueueCapacity { get; init; } = 256;

    /// <summary>A connection that sends nothing for this long is closed.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long a close handshake may take before the connection is aborted.</summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Reads PORT, RELAY_ORIGIN_SECRET, MAX_ROOMS and MAX_CONNECTIONS. Throws <see cref="FormatException"/> on bad values.</summary>
    public static RelayOptions FromEnvironment(Func<string, string?> getVariable)
    {
        ArgumentNullException.ThrowIfNull(getVariable);
        var defaults = new RelayOptions();
        string? secret = getVariable(OriginSecretVariable);
        string? webRoot = getVariable(WebRootVariable);
        return defaults with
        {
            Port = ReadInt(getVariable, PortVariable, defaults.Port, min: 1, max: 65535),
            OriginSecret = string.IsNullOrEmpty(secret) ? null : secret,
            WebRoot = string.IsNullOrWhiteSpace(webRoot) ? null : webRoot.Trim(),
            MaxRooms = ReadInt(getVariable, MaxRoomsVariable, defaults.MaxRooms, min: 1, max: 1_000_000),
            MaxConnections = ReadInt(getVariable, MaxConnectionsVariable, defaults.MaxConnections, min: 1, max: 1_000_000),
        };
    }

    private static int ReadInt(Func<string, string?> getVariable, string name, int fallback, int min, int max)
    {
        string? raw = getVariable(name);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < min || value > max)
        {
            throw new FormatException($"{name} must be an integer from {min} to {max}.");
        }
        return value;
    }
}
