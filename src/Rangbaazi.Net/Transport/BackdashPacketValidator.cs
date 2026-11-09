using System;
using System.Buffers.Binary;

namespace Rangbaazi.Net.Transport;

/// <summary>
/// Checks that a datagram is a well-formed Backdash 0.7.8 message before Backdash parses it.
///
/// Backdash trusts packets: an Input message whose peer count is over 4 makes it write past a
/// 4-entry buffer (memory corruption), and a short or unknown packet throws inside its receive
/// loop, which then stops for good and the match dies. So anything that doesn't match the 0.7.8
/// wire format is dropped here. Layout (big-endian): a 6-byte header (u16 type, u16 sync number,
/// u16 sequence number), then a body whose size depends on the type; see docs/tech/netcode.md.
/// Re-check this table when upgrading Backdash.
/// </summary>
internal static class BackdashPacketValidator
{
    private const int HeaderSize = 6;
    private const int MaxPeers = 4;              // Backdash Max.NumberOfPlayers
    private const int MaxInputBits = 512 * 8;    // Backdash Max.CompressedBytes
    private const int ConnectStatusSize = 1 + 4; // bool disconnected, i32 last frame

    /// <summary>Size in bytes of one input on the wire (<see cref="Sim.Buttons"/> is a ushort).</summary>
    internal const int InputSize = sizeof(ushort);

    public static bool IsValid(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < HeaderSize) return false;
        ushort type = BinaryPrimitives.ReadUInt16BigEndian(packet);
        int body = packet.Length - HeaderSize;
        return type switch
        {
            1 => body >= 2 + 8,      // SyncRequest: u16 random, i64 ping
            2 => body >= 4 + 8,      // SyncReply: u32 random, i64 pong
            3 => IsValidInput(packet[HeaderSize..]),
            4 => body >= 4 + 8,      // QualityReport: i32 frame advantage, i64 ping
            5 => body >= 8,          // QualityReply: i64 pong
            6 => true,               // KeepAlive: no body
            7 => body >= 4,          // InputAck: i32 frame
            8 => body >= 4,          // ConsistencyCheckRequest: i32 frame
            9 => body >= 4 + 4,      // ConsistencyCheckReply: i32 frame, u32 checksum
            10 => body >= 4 + 4 + 4, // ConsistencyCheckFail: i32 frame, u32 local, u32 remote
            _ => false,
        };
    }

    private static bool IsValidInput(ReadOnlySpan<byte> body)
    {
        // i32 start frame, i32 ack frame, bool disconnect requested, u8 peer count,
        // peer count x connect status, u8 input size, u16 bit count, ceil(bits / 8) bytes.
        const int fixedPart = 4 + 4 + 1 + 1;
        if (body.Length < fixedPart + 1 + 2) return false;
        int peers = body[9];
        if (peers > MaxPeers) return false;
        int offset = fixedPart + peers * ConnectStatusSize;
        if (body.Length < offset + 1 + 2) return false;
        if (body[offset] != InputSize) return false;
        int bits = BinaryPrimitives.ReadUInt16BigEndian(body[(offset + 1)..]);
        if (bits > MaxInputBits) return false;
        return body.Length >= offset + 1 + 2 + (bits + 7) / 8;
    }
}
