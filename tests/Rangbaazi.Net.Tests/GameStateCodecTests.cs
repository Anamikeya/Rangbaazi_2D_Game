using System.Buffers;
using System.Reflection;
using Backdash.Network;
using Backdash.Serialization;
using Rangbaazi.Sim;
using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Net.Tests;

public class GameStateCodecTests
{
    private static readonly MatchRules Rules = new() { Stage = Stages.Graybox };

    private static byte[] Save(GameState state)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryBufferWriter(buffer, Endianness.LittleEndian);
        GameStateCodec.Write(state, in writer);
        return buffer.WrittenSpan.ToArray();
    }

    private static GameState Load(byte[] bytes, int fighters = 2)
    {
        var state = new GameState(fighters, 0);
        int offset = 0;
        var reader = new BinaryBufferReader(bytes, ref offset, Endianness.LittleEndian);
        GameStateCodec.Read(state, in reader);
        return state;
    }

    private static void AssertSame(GameState expected, GameState actual)
    {
        Assert.Equal(expected.Checksum(), actual.Checksum());
        Assert.Equal(expected.Frame, actual.Frame);
        Assert.Equal(expected.Random.State, actual.Random.State);
        Assert.Equal(expected.MatchOver, actual.MatchOver);
        Assert.Equal(expected.WinnerIndex, actual.WinnerIndex);
        Assert.Equal(expected.Fighters, actual.Fighters);
    }

    [Fact]
    public void RoundTripRestoresTheWholeStateThroughAFight()
    {
        GameState state = Simulation.CreateInitialState(Rules, 2, 99);
        var rng = new SimRandom(5);
        var inputs = new Buttons[2];
        for (int f = 0; f < 1800; f++)
        {
            for (int p = 0; p < 2; p++) inputs[p] = (Buttons)(rng.NextUInt() & 0x7FF);
            Simulation.Advance(state, Rules, inputs);
            if (f % 97 == 0) AssertSame(state, Load(Save(state)));
        }
        Assert.Equal(GameStateCodec.SizeFor(2), Save(state).Length);
    }

    [Fact]
    public void EveryGameStateFieldSurvivesARoundTrip()
    {
        // When GameState gets a new field, give it a case here and save it in GameStateCodec.
        foreach (FieldInfo field in typeof(GameState).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            GameState state = Simulation.CreateInitialState(Rules, 2, 3);
            object changed = field.FieldType switch
            {
                Type t when t == typeof(int) => (int)field.GetValue(state)! + 17,
                Type t when t == typeof(bool) => !(bool)field.GetValue(state)!,
                Type t when t == typeof(SimRandom) => new SimRandom(0xDEADBEEF),
                Type t when t == typeof(Fighter[]) => ChangedFighters(state),
                _ => throw new InvalidOperationException($"GameState.{field.Name} ({field.FieldType.Name}) has no case in this test; add it here and to GameStateCodec."),
            };
            field.SetValue(state, changed);
            GameState loaded = Load(Save(state));
            Assert.Equal(state.Checksum(), loaded.Checksum());
            object? expected = field.GetValue(state), actual = field.GetValue(loaded);
            if (expected is Fighter[] a) Assert.Equal(a, (Fighter[])actual!);
            else Assert.Equal(expected, actual);
        }
    }

    private static Fighter[] ChangedFighters(GameState state)
    {
        var fighters = (Fighter[])state.Fighters.Clone();
        fighters[1].Josh = 123;
        fighters[1].Position = new FixVec2(Fix.FromInt(3), Fix.FromRatio(1, 3));
        fighters[1].GutsUsed = true;
        return fighters;
    }

    [Fact]
    public void LoadingTruncatedOrMismatchedBytesFails()
    {
        GameState state = Simulation.CreateInitialState(Rules, 2, 1);
        byte[] bytes = Save(state);
        Assert.Throws<InvalidDataException>(() => Load(bytes[..^4]));
        Assert.Throws<InvalidDataException>(() => Load(bytes, fighters: 3));

        bytes[8] ^= 0x01; // the frame number changed after saving
        Assert.Throws<InvalidDataException>(() => Load(bytes));
    }

    [Fact]
    public void BackdashChecksumIsOurChecksumFoldedAndNeverZero()
    {
        GameState state = Simulation.CreateInitialState(Rules, 2, 1);
        byte[] bytes = Save(state);
        Assert.Equal(state.Checksum(), GameStateCodec.StoredChecksum(bytes));
        Assert.Equal(GameStateCodec.Fold(state.Checksum()), GameStateCodec.Fold(bytes));
        Assert.Equal(1u, GameStateCodec.Fold(0x12345678_12345678UL)); // halves cancel out: 0 means "none" to Backdash
        Assert.NotEqual(GameStateCodec.Fold(1UL), GameStateCodec.Fold(2UL));
    }
}
