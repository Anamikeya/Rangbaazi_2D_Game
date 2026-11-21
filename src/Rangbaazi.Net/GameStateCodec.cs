using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.CompilerServices;
using Backdash.Serialization;
using Rangbaazi.Sim;

namespace Rangbaazi.Net;

/// <summary>
/// Turns a <see cref="GameState"/> into the bytes Backdash keeps for rollback, and back.
///
/// Layout: our FNV-1a <see cref="GameState.Checksum"/> (8 bytes), Frame, Random.State, MatchOver,
/// WinnerIndex, the fighter count, the <see cref="Fighter"/> array as raw memory, then PropTimer,
/// LastBoxWeapon, the <see cref="Prop"/> array as raw memory, and the WeaponStats ints.
/// The bytes never leave this machine (only the folded checksum is compared with the other
/// peer), so the raw struct copy is safe, and a new <see cref="Fighter"/> field is saved without
/// touching this file. A new <see cref="GameState"/> field must be added here, as well as to
/// <see cref="GameState.Checksum"/> and <see cref="GameState.CopyFrom"/>;
/// <c>GameStateCodecTests</c> fails until it is.
/// </summary>
public static class GameStateCodec
{
    /// <summary>Bytes before the fighter array.</summary>
    private const int HeaderSize = 8 + 4 + 8 + 1 + 4 + 4;

    /// <summary>Bytes after the fighter array: the prop timer, the last box's weapon and the props (a table sized by the fighter count).</summary>
    private static int PropsSize(int fighters) => 4 + 4 + Weapons.PropSlots(fighters) * Unsafe.SizeOf<Prop>();

    /// <summary>Bytes of the match record after the props.</summary>
    private static int StatsSize(int fighters) => fighters * Weapons.RecordInts * sizeof(int);

    public static int SizeFor(int fighters) => HeaderSize + fighters * Unsafe.SizeOf<Fighter>() + PropsSize(fighters) + StatsSize(fighters);

    /// <summary>Writes the state and returns its checksum.</summary>
    public static ulong Write(GameState state, in BinaryBufferWriter writer)
    {
        ulong checksum = state.Checksum();
        writer.Write(checksum);
        writer.Write(state.Frame);
        writer.Write(state.Random.State);
        writer.Write(state.MatchOver);
        writer.Write(state.WinnerIndex);
        writer.Write(state.Fighters.Length);
        writer.WriteStruct<Fighter>(state.Fighters);
        writer.Write(state.PropTimer);
        writer.Write((int)state.LastBoxWeapon);
        writer.WriteStruct<Prop>(state.Props);
        writer.WriteStruct<int>(state.WeaponStats);
        return checksum;
    }

    /// <summary>
    /// Restores <paramref name="state"/> from bytes written by <see cref="Write"/>, and checks that
    /// the result has the checksum that was saved with it.
    /// </summary>
    public static void Read(GameState state, in BinaryBufferReader reader)
    {
        ulong saved = reader.ReadUInt64();
        state.Frame = reader.ReadInt32();
        state.Random.State = reader.ReadUInt64();
        state.MatchOver = reader.ReadBoolean();
        state.WinnerIndex = reader.ReadInt32();
        int fighters = reader.ReadInt32();

        // ReadStruct copies whatever is left without complaint, so check the size first.
        if (fighters != state.Fighters.Length || state.Props.Length != Weapons.PropSlots(fighters) || state.WeaponStats.Length != fighters * Weapons.RecordInts
            || reader.FreeCapacity != fighters * Unsafe.SizeOf<Fighter>() + PropsSize(fighters) + StatsSize(fighters))
            throw new InvalidDataException(
                $"Saved state has {fighters} fighters and {reader.FreeCapacity} bytes left; expected {state.Fighters.Length}.");
        reader.ReadStruct<Fighter>(state.Fighters);
        state.PropTimer = reader.ReadInt32();
        state.LastBoxWeapon = (WeaponId)reader.ReadInt32();
        reader.ReadStruct<Prop>(state.Props);
        reader.ReadStruct<int>(state.WeaponStats);

        if (state.Checksum() != saved)
            throw new InvalidDataException($"Loaded state for frame {state.Frame} doesn't match its saved checksum.");
    }

    /// <summary>The checksum <see cref="Write"/> stored at the start of the bytes.</summary>
    public static ulong StoredChecksum(ReadOnlySpan<byte> saved) => BinaryPrimitives.ReadUInt64LittleEndian(saved);

    /// <summary>
    /// Backdash's checksum of saved state (it only keeps 32 bits): our 64-bit checksum folded in
    /// half. Never 0, which Backdash reads as "no checksum". Replaces its default Fletcher-32, which
    /// can't tell some byte patterns apart (0x0000 from 0xFFFF).
    /// </summary>
    public static uint Fold(ReadOnlySpan<byte> saved) => Fold(StoredChecksum(saved));

    public static uint Fold(ulong checksum)
    {
        uint folded = (uint)(checksum ^ (checksum >> 32));
        return folded == 0 ? 1u : folded;
    }
}
