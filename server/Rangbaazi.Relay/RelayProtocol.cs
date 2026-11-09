using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace Rangbaazi.Relay;

/// <summary>Close codes of the relay protocol (README.md).</summary>
internal static class RelayCloseCodes
{
    public const WebSocketCloseStatus BadCode = (WebSocketCloseStatus)4000;
    public const WebSocketCloseStatus RoomFull = (WebSocketCloseStatus)4001;
    public const WebSocketCloseStatus Busy = (WebSocketCloseStatus)4002;
    public const WebSocketCloseStatus Idle = (WebSocketCloseStatus)4003;
}

/// <summary>Room codes: 4 to 8 ASCII letters or digits, case-insensitive (stored upper case).</summary>
internal static class RoomCode
{
    public const int MinLength = 4;
    public const int MaxLength = 8;

    public static bool TryNormalize(string? raw, out string code)
    {
        code = string.Empty;
        if (raw is null || raw.Length < MinLength || raw.Length > MaxLength) return false;
        foreach (char c in raw)
        {
            if (!char.IsAsciiLetterOrDigit(c)) return false;
        }
        code = raw.ToUpperInvariant();
        return true;
    }
}

/// <summary>Match seeds: uniform in [1, 2^53 - 1] so they survive a JSON / GDScript double round trip.</summary>
internal static class SeedGenerator
{
    public const long MaxSeed = (1L << 53) - 1;

    /// <summary>A fresh seed, never equal to <paramref name="previous"/> (0 = no previous seed).</summary>
    public static long Next(long previous)
    {
        Span<byte> bytes = stackalloc byte[8];
        while (true)
        {
            RandomNumberGenerator.Fill(bytes);
            long seed = (long)(BinaryPrimitives.ReadUInt64LittleEndian(bytes) & (ulong)MaxSeed);
            if (seed != 0 && seed != previous) return seed;
        }
    }
}

/// <summary>The TEXT frames the relay sends. All are small JSON objects encoded as UTF-8.</summary>
internal static class RelayMessages
{
    public static readonly byte[] PeerLeft = TypeOnly("peer_left");
    public static readonly byte[] Full = TypeOnly("full");
    public static readonly byte[] Busy = TypeOnly("busy");

    public static byte[] Welcome(int slot, string room) => Write(w =>
    {
        w.WriteString("type", "welcome");
        w.WriteNumber("slot", slot);
        w.WriteString("room", room);
    });

    public static byte[] Start(long seed, int slot) => Write(w =>
    {
        w.WriteString("type", "start");
        w.WriteString("seed", seed.ToString(CultureInfo.InvariantCulture));
        w.WriteNumber("slot", slot);
    });

    public static byte[] Error(string message) => Write(w =>
    {
        w.WriteString("type", "error");
        w.WriteString("message", message);
    });

    private static byte[] TypeOnly(string type) => Write(w => w.WriteString("type", type));

    private static byte[] Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }
}

/// <summary>
/// Per-connection rate limit: holds up to <c>capacity</c> tokens, refills <c>refillPerSecond</c> a second,
/// one token per received frame. Not thread-safe: only the connection's receive loop uses it.
/// </summary>
internal sealed class TokenBucket
{
    private readonly double _capacity;
    private readonly double _tokensPerTick;
    private double _tokens;
    private long _lastTimestamp;

    public TokenBucket(int capacity, int refillPerSecond, long nowTimestamp, long timestampsPerSecond)
    {
        _capacity = capacity;
        _tokens = capacity;
        _tokensPerTick = refillPerSecond / (double)timestampsPerSecond;
        _lastTimestamp = nowTimestamp;
    }

    public bool TryTake(long nowTimestamp)
    {
        long elapsed = nowTimestamp - _lastTimestamp;
        if (elapsed > 0)
        {
            _tokens = Math.Min(_capacity, _tokens + elapsed * _tokensPerTick);
            _lastTimestamp = nowTimestamp;
        }
        if (_tokens < 1) return false;
        _tokens -= 1;
        return true;
    }
}

/// <summary>Constant-time check of the CloudFront origin secret header.</summary>
internal sealed class OriginSecretCheck
{
    private readonly byte[] _expectedHash;

    public OriginSecretCheck(string secret)
    {
        _expectedHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret));
    }

    public bool Matches(string? provided)
    {
        if (provided is null) return false;
        // Hashing first makes the comparison length-independent; FixedTimeEquals makes it constant-time.
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(provided), hash);
        return CryptographicOperations.FixedTimeEquals(hash, _expectedHash);
    }
}
