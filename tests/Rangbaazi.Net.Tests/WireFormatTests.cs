using System.Buffers.Binary;
using Rangbaazi.Net.Transport;

namespace Rangbaazi.Net.Tests;

/// <summary>Our control messages, and the check that keeps malformed packets away from Backdash.</summary>
public class WireFormatTests
{
    private static byte[] Bytes(in ControlMessage m)
    {
        var buffer = new byte[ControlMessage.MaxSize];
        return buffer[..m.Write(buffer)];
    }

    [Fact]
    public void ControlMessagesRoundTrip()
    {
        ControlMessage[] messages =
        {
            ControlMessage.Hello(0x1122334455667788, 42, 1, 0xCAFEBABE12345678),
            ControlMessage.ChecksumOf(7, 1234, 0xFEDCBA9876543210),
            ControlMessage.Bye(9, 10, ByeReason.Rematch),
        };
        foreach (ControlMessage m in messages)
        {
            Assert.True(ControlMessage.TryRead(Bytes(m), out ControlMessage read));
            Assert.Equal(m, read);
        }
    }

    [Fact]
    public void GarbageIsNotAControlMessage()
    {
        byte[] hello = Bytes(ControlMessage.Hello(5, 0, 0, 1));
        Assert.False(ControlMessage.TryRead(hello[..^1], out _));          // truncated
        Assert.False(ControlMessage.TryRead([.. hello, 0], out _));        // too long
        Assert.False(ControlMessage.TryRead(Array.Empty<byte>(), out _));
        Assert.False(ControlMessage.TryRead(new byte[] { 99, 1, 2, 3 }, out _));

        byte[] badMagic = (byte[])hello.Clone();
        badMagic[1] ^= 0xFF;
        Assert.False(ControlMessage.TryRead(badMagic, out _));

        Assert.False(ControlMessage.TryRead(Bytes(ControlMessage.Hello(0, 0, 0, 1)), out _)); // a zero match id is never valid
        Assert.False(ControlMessage.TryRead(Bytes(ControlMessage.ChecksumOf(5, -1, 1)), out _));
    }

    private static byte[] Header(ushort type, int body)
    {
        var p = new byte[6 + body];
        BinaryPrimitives.WriteUInt16BigEndian(p, type);
        return p;
    }

    private static byte[] Input(int peers, int inputSize = 2, int bits = 16, int extraBytes = 0)
    {
        int offset = 10 + 5 * peers;
        var body = new byte[offset + 3 + (bits + 7) / 8 + extraBytes];
        body[9] = (byte)peers;
        body[offset] = (byte)inputSize;
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(offset + 1), (ushort)bits);
        byte[] packet = Header(3, 0);
        return [.. packet, .. body];
    }

    [Fact]
    public void WellFormedBackdashMessagesPass()
    {
        Assert.True(BackdashPacketValidator.IsValid(Header(1, 10)));
        Assert.True(BackdashPacketValidator.IsValid(Header(2, 12)));
        Assert.True(BackdashPacketValidator.IsValid(Header(4, 12)));
        Assert.True(BackdashPacketValidator.IsValid(Header(5, 8)));
        Assert.True(BackdashPacketValidator.IsValid(Header(6, 0)));
        Assert.True(BackdashPacketValidator.IsValid(Header(7, 4)));
        Assert.True(BackdashPacketValidator.IsValid(Header(8, 4)));
        Assert.True(BackdashPacketValidator.IsValid(Header(9, 8)));
        Assert.True(BackdashPacketValidator.IsValid(Header(10, 12)));
        Assert.True(BackdashPacketValidator.IsValid(Input(peers: 2)));
        Assert.True(BackdashPacketValidator.IsValid(Input(peers: 4, bits: 4096)));
        Assert.True(BackdashPacketValidator.IsValid(Input(peers: 0, bits: 0)));
    }

    [Fact]
    public void MalformedBackdashMessagesAreDropped()
    {
        Assert.False(BackdashPacketValidator.IsValid(new byte[5]));       // shorter than a header
        Assert.False(BackdashPacketValidator.IsValid(Header(0, 20)));     // Unknown
        Assert.False(BackdashPacketValidator.IsValid(Header(11, 20)));    // not a 0.7.8 type
        Assert.False(BackdashPacketValidator.IsValid(Header(1, 9)));      // SyncRequest one byte short
        Assert.False(BackdashPacketValidator.IsValid(Header(10, 11)));
        Assert.False(BackdashPacketValidator.IsValid(Input(peers: 5)));   // would write past Backdash's 4-peer buffer
        Assert.False(BackdashPacketValidator.IsValid(Input(peers: 255)));
        Assert.False(BackdashPacketValidator.IsValid(Input(peers: 2, inputSize: 4)));
        Assert.False(BackdashPacketValidator.IsValid(Input(peers: 2, bits: 4097)));
        byte[] truncated = Input(peers: 2, bits: 64);
        Assert.False(BackdashPacketValidator.IsValid(truncated[..^1]));
    }
}
