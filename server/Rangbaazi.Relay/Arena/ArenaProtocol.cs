using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Rangbaazi.Sim;

namespace Rangbaazi.Relay.Arena;

/// <summary>Limits of arena rooms (the room design: 1 to 15 players, a 10 to 15 minute clock).</summary>
internal static class ArenaLimits
{
    public const int MaxPlayers = 15;

    /// <summary>
    /// The arena protocol version clients send as <c>v=</c>. 2 added weapons to STATE; 3 added each
    /// fighter's last hit and main weapon. Older clients are turned away with a message to update,
    /// instead of misreading the frames.
    /// </summary>
    public const int ProtocolVersion = 3;
    public const int MinMinutes = 10;
    public const int MaxMinutes = 15;
    public const int DefaultMinutes = 10;
    public const int MaxNameLength = 12;

    /// <summary>Quick-chat phrases are numbered 0 to this - 1; the client holds the words.</summary>
    public const int QuickChatPhrases = 16;

    /// <summary>One quick-chat message per player per this many milliseconds; faster ones are dropped.</summary>
    public const long ChatIntervalMs = 1000;

    /// <summary>After a match ends the room waits this long, then returns to the lobby on its own.</summary>
    public const int OverFrames = 15 * Simulation.TicksPerSecond;

    public const string DefaultStage = "rooftops";

    /// <summary>The 31 fighters (cosmetic: every fighter has the same moves and numbers).</summary>
    public static readonly string[] Characters =
    {
        "abbas", "abhishek", "akmal", "akshat", "aman", "ankit", "artha", "avipray", "ayush", "chandan",
        "devanshu", "faiz", "faizal", "harsh", "maaz", "manish", "nikhil", "rajat", "rishabh", "saklan",
        "salik", "sarthak", "shahn", "shraddha", "shubham", "simran", "siraj", "suhani", "tushar", "unnati",
        "vinayak",
    };

    public const string DefaultCharacter = "shahn";

    /// <summary>A fighter from the character creator: "mix.head.hands.feet.moves.name", the four each one of the 31.</summary>
    public const string MixPrefix = "mix.";

    /// <summary>One of the 31 ids, or a well-formed mixed fighter (its name cleaned like a player's); anything else is the default.</summary>
    public static string CleanCharacter(string? id)
    {
        if (id is null) return DefaultCharacter;
        if (Array.IndexOf(Characters, id) >= 0) return id;
        string[] bits = id.Split('.');
        if (!id.StartsWith(MixPrefix, StringComparison.Ordinal) || bits.Length != 6) return DefaultCharacter;
        for (int i = 1; i <= 4; i++)
        {
            if (Array.IndexOf(Characters, bits[i]) < 0) return DefaultCharacter;
        }
        return $"{MixPrefix}{bits[1]}.{bits[2]}.{bits[3]}.{bits[4]}.{PlayerName.Clean(bits[5], "")}";
    }

    /// <summary>The one of the 31 a fighter counts as (for its own weapon): itself, or a mixed fighter's head.</summary>
    public static string Look(string id) => id.StartsWith(MixPrefix, StringComparison.Ordinal) ? id.Split('.')[1] : id;

    public static bool IsStage(string? id) => id is not null && ArenaStages.ById(id) is not null;
}

/// <summary>Close codes beyond the relay's (README.md).</summary>
internal static class ArenaCloseCodes
{
    public const WebSocketCloseStatus RoomTaken = (WebSocketCloseStatus)4004;
    public const WebSocketCloseStatus NoRoom = (WebSocketCloseStatus)4005;
}

/// <summary>Player names: letters, digits, spaces, '-' and '_', at most 12 characters.</summary>
internal static class PlayerName
{
    public static string Clean(string? raw, string fallback)
    {
        if (raw is null) return fallback;
        var sb = new StringBuilder(ArenaLimits.MaxNameLength);
        foreach (char c in raw.Trim())
        {
            if (sb.Length == ArenaLimits.MaxNameLength) break;
            if (char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_') sb.Append(c);
        }
        string name = sb.ToString().Trim();
        return name.Length == 0 ? fallback : name;
    }
}

/// <summary>TEXT frames the arena sends: small JSON objects encoded as UTF-8.</summary>
internal static class ArenaMessages
{
    public static byte[] Lobby(ArenaRoom room, ArenaPlayer you) => Write(w =>
    {
        w.WriteString("type", "lobby");
        w.WriteString("room", room.Code);
        w.WriteString("phase", room.Phase.ToString().ToLowerInvariant());
        w.WriteNumber("minutes", room.Minutes);
        w.WriteString("stage", room.StageId);
        w.WriteNumber("you", you.Id);
        w.WriteNumber("host", room.Host?.Id ?? -1);
        w.WriteNumber("max", ArenaLimits.MaxPlayers);
        w.WriteStartArray("players");
        foreach (ArenaPlayer p in room.Members)
        {
            w.WriteStartObject();
            w.WriteNumber("id", p.Id);
            w.WriteString("name", p.Name);
            w.WriteBoolean("cpu", p.IsBot);
            w.WriteString("character", p.Character);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    });

    public static byte[] Start(ArenaRoom room, ArenaPlayer you) => Write(w =>
    {
        w.WriteString("type", "start");
        w.WriteString("stage", room.StageId);
        w.WriteNumber("minutes", room.Minutes);
        w.WriteNumber("time_limit_frames", room.Rules!.TimeLimitFrames);
        w.WriteNumber("you", you.Fighter);
        w.WriteStartArray("fighters");
        foreach (ArenaPlayer p in room.Fighters)
        {
            w.WriteStartObject();
            w.WriteNumber("id", p.Id);
            w.WriteString("name", p.Name);
            w.WriteBoolean("cpu", p.IsBot);
            w.WriteString("character", p.Character);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    });

    public static byte[] Chat(ArenaPlayer from, int phrase) => Write(w =>
    {
        w.WriteString("type", "chat");
        w.WriteNumber("from", from.Id);
        w.WriteString("name", from.Name);
        w.WriteNumber("fighter", from.Fighter);
        w.WriteNumber("phrase", phrase);
    });

    public static byte[] Error(string code, string message) => Write(w =>
    {
        w.WriteString("type", "error");
        w.WriteString("code", code);
        w.WriteString("message", message);
    });

    private static byte[] Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }
}

/// <summary>A request a player sends as TEXT JSON (see README.md, arena rooms).</summary>
internal readonly record struct ArenaRequest(string Type, int Number, string? Id = null)
{
    public static bool TryParse(ReadOnlySpan<byte> utf8, out ArenaRequest request)
    {
        request = default;
        try
        {
            var reader = new Utf8JsonReader(utf8);
            using JsonDocument doc = JsonDocument.ParseValue(ref reader);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String) return false;
            int number = 0;
            foreach (string key in NumberKeys)
            {
                if (root.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n))
                {
                    number = n;
                    break;
                }
            }
            string? id = root.TryGetProperty("id", out JsonElement idValue) && idValue.ValueKind == JsonValueKind.String ? idValue.GetString() : null;
            request = new ArenaRequest(type.GetString()!, number, id);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static readonly string[] NumberKeys = { "minutes", "count", "phrase" };
}

/// <summary>
/// The binary messages. A player sends INPUT (3 bytes: 1, then its buttons as a little-endian
/// 16-bit number). The server sends STATE every frame: the match as the players need it to draw,
/// without hidden values (the second wind) or anything the screen never shows.
/// </summary>
internal static class ArenaBinary
{
    public const byte Input = 1;
    public const byte State = 2;
    public const int HeaderBytes = 12;
    public const int FighterBytes = 31;
    public const int PropBytes = 12;

    /// <summary>Every defined button (Left to Taunt); anything else is masked off.</summary>
    public const ushort KnownButtons = (1 << 11) - 1;

    public static bool TryReadInput(ReadOnlySpan<byte> message, out ushort buttons)
    {
        buttons = 0;
        if (message.Length != 3 || message[0] != Input) return false;
        buttons = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(message[1..]) & KnownButtons);
        return true;
    }

    public static byte[] EncodeInput(Buttons buttons)
    {
        var bytes = new byte[3];
        bytes[0] = Input;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(1), (ushort)buttons);
        return bytes;
    }

    /// <summary>
    /// STATE: type, frame (u32), frames left on the clock (u32), flags (1 = match over), winner
    /// (i8, -1 none), fighter count (u8), then per fighter: x, y (i32 raw fixed point, metres x 65536),
    /// facing (i8), action, move, flags (1 grounded, 2 frozen by a hit, 4 attack connected, 8 Guts
    /// used, 16 air dodge used), action frame (u16), Josh, KOs, falls (u16 each), hit-stop, armor
    /// (u8), Guts timer (u16), hitstun, dodge cooldown, weapon, weapon supply (u8), the last
    /// hit taken (u8: its weapon, +128 for a sweet spot), the hit count (u8, wrapping), and the main
    /// weapon so far (u8: 0-7, or 15 for an all-rounder, +128 for a master). Then the props
    /// in use: count (u8), and per prop: kind, weapon, shot kind (u8), x, y (i32 raw), facing of its
    /// flight (i8: -1, 0, 1).
    /// Little-endian.
    /// </summary>
    public static byte[] EncodeState(GameState state, MatchRules rules)
    {
        Fighter[] fighters = state.Fighters;
        int props = 0;
        foreach (Prop p in state.Props)
            if (p.Kind is PropKind.Box or PropKind.Item or PropKind.Shot) props++;
        var bytes = new byte[HeaderBytes + FighterBytes * fighters.Length + 1 + PropBytes * props];
        Span<byte> s = bytes;
        s[0] = State;
        BinaryPrimitives.WriteUInt32LittleEndian(s[1..], (uint)state.Frame);
        int left = rules.TimeLimitFrames > 0 ? Math.Max(0, rules.TimeLimitFrames - state.Frame) : 0;
        BinaryPrimitives.WriteUInt32LittleEndian(s[5..], (uint)left);
        s[9] = (byte)(state.MatchOver ? 1 : 0);
        s[10] = (byte)(sbyte)state.WinnerIndex;
        s[11] = (byte)fighters.Length;
        int o = HeaderBytes;
        for (int i = 0; i < fighters.Length; i++, o += FighterBytes)
        {
            ref Fighter f = ref fighters[i];
            BinaryPrimitives.WriteInt32LittleEndian(s[o..], ClampRaw(f.Position.X.Raw));
            BinaryPrimitives.WriteInt32LittleEndian(s[(o + 4)..], ClampRaw(f.Position.Y.Raw));
            s[o + 8] = (byte)(sbyte)f.Facing;
            s[o + 9] = (byte)f.Action;
            s[o + 10] = (byte)f.Move;
            s[o + 11] = (byte)((f.Grounded ? 1 : 0) | (f.FrozenByHit ? 2 : 0) | (f.AttackConnected ? 4 : 0) | (f.GutsUsed ? 8 : 0) | (f.AirDodgeUsed ? 16 : 0));
            BinaryPrimitives.WriteUInt16LittleEndian(s[(o + 12)..], U16(f.ActionFrame));
            BinaryPrimitives.WriteUInt16LittleEndian(s[(o + 14)..], U16(f.Josh));
            BinaryPrimitives.WriteUInt16LittleEndian(s[(o + 16)..], U16(f.Kos));
            BinaryPrimitives.WriteUInt16LittleEndian(s[(o + 18)..], U16(f.Falls));
            s[o + 20] = U8(f.HitStop);
            s[o + 21] = U8(f.ArmorFrames);
            BinaryPrimitives.WriteUInt16LittleEndian(s[(o + 22)..], U16(f.GutsTimer));
            s[o + 24] = U8(f.HitstunFrames);
            s[o + 25] = U8(f.DodgeCooldown);
            s[o + 26] = (byte)f.Weapon;
            s[o + 27] = U8(f.WeaponSupply);
            s[o + 28] = (byte)((int)f.LastHitWeapon | (f.LastHitSweet ? 128 : 0));
            s[o + 29] = (byte)f.HitCount;
            int main = Simulation.MainWeapon(state, i, out bool master);
            s[o + 30] = (byte)((main < 0 ? 15 : main) | (master ? 128 : 0));
        }
        s[o++] = (byte)props;
        foreach (Prop p in state.Props)
        {
            if (p.Kind is not (PropKind.Box or PropKind.Item or PropKind.Shot)) continue;
            s[o] = (byte)p.Kind;
            s[o + 1] = (byte)p.Weapon;
            s[o + 2] = (byte)p.Shot;
            BinaryPrimitives.WriteInt32LittleEndian(s[(o + 3)..], ClampRaw(p.Position.X.Raw));
            BinaryPrimitives.WriteInt32LittleEndian(s[(o + 7)..], ClampRaw(p.Position.Y.Raw));
            s[o + 11] = (byte)(sbyte)(p.Velocity.X.Raw > 0 ? 1 : p.Velocity.X.Raw < 0 ? -1 : 0);
            o += PropBytes;
        }
        return bytes;
    }

    private static int ClampRaw(long raw) => raw > int.MaxValue ? int.MaxValue : raw < int.MinValue ? int.MinValue : (int)raw;

    private static ushort U16(int v) => (ushort)Math.Clamp(v, 0, ushort.MaxValue);

    private static byte U8(int v) => (byte)Math.Clamp(v, 0, byte.MaxValue);
}
