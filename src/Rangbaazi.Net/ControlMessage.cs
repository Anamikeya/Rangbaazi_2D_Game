using System;
using System.Buffers.Binary;

namespace Rangbaazi.Net;

/// <summary>Constants shared by both peers. Bump <see cref="Version"/> when the wire format changes.</summary>
public static class NetProtocol
{
    public const ushort Version = 1;

    /// <summary>"RBZN": first bytes of a hello, so stray traffic is never mistaken for one.</summary>
    internal const uint HelloMagic = 0x4E5A4252;

    /// <summary>First byte of every datagram: which layer it belongs to.</summary>
    internal const byte BackdashChannel = 0xB0;
    internal const byte ControlChannel = 0xC0;
}

internal enum ControlKind : byte
{
    Hello = 1,
    Checksum = 2,
    Bye = 3,
}

/// <summary>Why a peer is leaving, sent in a <see cref="ControlKind.Bye"/>.</summary>
public enum ByeReason : byte
{
    Left = 1,
    Rematch = 2,
    Desync = 3,
    Error = 4,
    GaveUpWaiting = 5,
    Rejected = 6,
}

/// <summary>
/// Our own messages, sent beside Backdash's on the same socket (Backdash has no way to carry
/// custom messages). Hello: agree on the match before Backdash starts. Checksum: compare the state
/// of a frame both peers have final inputs for, which detects a desync exactly. Bye: leave on purpose.
/// Little-endian, fixed size per kind; anything else is rejected by <see cref="TryRead"/>.
/// </summary>
internal readonly record struct ControlMessage(
    ControlKind Kind,
    ulong Nonce,
    ulong SeenNonce = 0,
    byte Slot = 0,
    ulong Fingerprint = 0,
    ushort Version = NetProtocol.Version,
    int Frame = 0,
    ulong Checksum = 0,
    ByeReason Reason = 0)
{
    public const int HelloSize = 1 + 4 + 2 + 8 + 8 + 1 + 8;
    public const int ChecksumSize = 1 + 8 + 4 + 8;
    public const int ByeSize = 1 + 8 + 8 + 1;
    public const int MaxSize = HelloSize;

    public static ControlMessage Hello(ulong nonce, ulong seenNonce, int slot, ulong fingerprint) =>
        new(ControlKind.Hello, nonce, seenNonce, (byte)slot, fingerprint);

    public static ControlMessage ChecksumOf(ulong nonce, int frame, ulong checksum) =>
        new(ControlKind.Checksum, nonce, Frame: frame, Checksum: checksum);

    /// <summary>A goodbye; <paramref name="peerNonce"/> lets the peer accept it even before it has our hello.</summary>
    public static ControlMessage Bye(ulong nonce, ulong peerNonce, ByeReason reason) =>
        new(ControlKind.Bye, nonce, SeenNonce: peerNonce, Reason: reason);

    /// <summary>Writes the message into <paramref name="buffer"/> and returns its length.</summary>
    public int Write(Span<byte> buffer)
    {
        buffer[0] = (byte)Kind;
        switch (Kind)
        {
            case ControlKind.Hello:
                BinaryPrimitives.WriteUInt32LittleEndian(buffer[1..], NetProtocol.HelloMagic);
                BinaryPrimitives.WriteUInt16LittleEndian(buffer[5..], Version);
                BinaryPrimitives.WriteUInt64LittleEndian(buffer[7..], Nonce);
                BinaryPrimitives.WriteUInt64LittleEndian(buffer[15..], SeenNonce);
                buffer[23] = Slot;
                BinaryPrimitives.WriteUInt64LittleEndian(buffer[24..], Fingerprint);
                return HelloSize;
            case ControlKind.Checksum:
                BinaryPrimitives.WriteUInt64LittleEndian(buffer[1..], Nonce);
                BinaryPrimitives.WriteInt32LittleEndian(buffer[9..], Frame);
                BinaryPrimitives.WriteUInt64LittleEndian(buffer[13..], Checksum);
                return ChecksumSize;
            case ControlKind.Bye:
                BinaryPrimitives.WriteUInt64LittleEndian(buffer[1..], Nonce);
                BinaryPrimitives.WriteUInt64LittleEndian(buffer[9..], SeenNonce);
                buffer[17] = (byte)Reason;
                return ByeSize;
            default:
                throw new InvalidOperationException($"Unknown control message {Kind}.");
        }
    }

    public static bool TryRead(ReadOnlySpan<byte> data, out ControlMessage message)
    {
        message = default;
        if (data.IsEmpty) return false;
        switch ((ControlKind)data[0])
        {
            case ControlKind.Hello when data.Length == HelloSize:
                if (BinaryPrimitives.ReadUInt32LittleEndian(data[1..]) != NetProtocol.HelloMagic) return false;
                message = new ControlMessage(ControlKind.Hello,
                    Nonce: BinaryPrimitives.ReadUInt64LittleEndian(data[7..]),
                    SeenNonce: BinaryPrimitives.ReadUInt64LittleEndian(data[15..]),
                    Slot: data[23],
                    Fingerprint: BinaryPrimitives.ReadUInt64LittleEndian(data[24..]),
                    Version: BinaryPrimitives.ReadUInt16LittleEndian(data[5..]));
                return message.Nonce != 0;
            case ControlKind.Checksum when data.Length == ChecksumSize:
                message = ChecksumOf(BinaryPrimitives.ReadUInt64LittleEndian(data[1..]),
                    BinaryPrimitives.ReadInt32LittleEndian(data[9..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(data[13..]));
                return message.Nonce != 0 && message.Frame >= 0;
            case ControlKind.Bye when data.Length == ByeSize:
                var reason = (ByeReason)data[17];
                if (!Enum.IsDefined(reason)) reason = ByeReason.Left;
                message = Bye(BinaryPrimitives.ReadUInt64LittleEndian(data[1..]), BinaryPrimitives.ReadUInt64LittleEndian(data[9..]), reason);
                return message.Nonce != 0;
            default:
                return false;
        }
    }
}
