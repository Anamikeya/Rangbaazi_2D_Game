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
                Type t when t == typeof(Prop[]) => ChangedProps(state),
                Type t when t == typeof(int[]) => ((int[])field.GetValue(state)!).Select((v, i) => v + i + 1).ToArray(),
                Type t when t == typeof(WeaponId) => WeaponId.Bow,
                _ => throw new InvalidOperationException($"GameState.{field.Name} ({field.FieldType.Name}) has no case in this test; add it here and to GameStateCodec."),
            };
            field.SetValue(state, changed);
            GameState loaded = Load(Save(state));
            Assert.Equal(state.Checksum(), loaded.Checksum());
            object? expected = field.GetValue(state), actual = field.GetValue(loaded);
            if (expected is Fighter[] a) Assert.Equal(a, (Fighter[])actual!);
            else if (expected is Prop[] p) Assert.Equal(p, (Prop[])actual!);
            else if (expected is int[] n) Assert.Equal(n, (int[])actual!);
            else Assert.Equal(expected, actual);
        }
    }

    private static Prop[] ChangedProps(GameState state)
    {
        var props = (Prop[])state.Props.Clone();
        props[5] = new Prop { Kind = PropKind.Shot, Shot = ShotKind.Arrow, Weapon = WeaponId.Bow, Owner = 1, Age = 9, HitMask = 3,
            Position = new FixVec2(Fix.FromInt(2), Fix.One) };
        return props;
    }

    private static Fighter[] ChangedFighters(GameState state)
    {
        var fighters = (Fighter[])state.Fighters.Clone();
        fighters[1].Josh = 123;
        fighters[1].Position = new FixVec2(Fix.FromInt(3), Fix.FromRatio(1, 3));
        fighters[1].GutsUsed = true;
        return fighters;
    }

    /// <summary>
    /// The state combat spec v0.4 added: the prop table sized by the fighter count, the six-slot
    /// record, and the statuses. A state with every status set, a full prop table and a value in
    /// every Prop field (the reused ones too: a shot's wear and its edge bounces) round-trips.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(15)]
    public void AFullPropTableTheStatusesAndTheRecordSurviveARoundTrip(int fighters)
    {
        GameState state = Simulation.CreateInitialState(Rules, fighters, 7);
        Assert.Equal(Weapons.PropSlots(fighters), state.Props.Length);
        Assert.Equal(fighters * Weapons.RecordInts, state.WeaponStats.Length);
        for (int i = 0; i < state.Props.Length; i++)
            state.Props[i] = new Prop
            {
                Kind = PropKind.Shot, Shot = (ShotKind)(1 + i % 17), Weapon = (WeaponId)(1 + i % 107), Supply = i % 2 == 0 ? -1 : i, Owner = i % fighters,
                OwnerClear = i % 3 == 0, Position = new FixVec2(Fix.FromInt(i), Fix.FromRatio(i, 7)), Velocity = new FixVec2(Fix.FromRatio(-i, 9), Fix.Half),
                Grounded = i % 2 == 1, Timer = 300 + i, Age = 1000 + i, HitMask = 1 << (i % 16), Hits = i, IgnoreFighter = 1 + i % 3, IgnoreTimer = 2 + i % 5,
            };
        // Every flight is in the table (17 of them, from 24 slots up), the batch 5 ones with the values
        // they reuse fields for: a speaker's facing and a decoy's pretended weapon in Supply, a gate's
        // marks in HitMask, an anvil's hang in Timer.
        for (int k = 1; k <= 17; k++) Assert.Contains(state.Props, p => p.Shot == (ShotKind)k);
        int speaker = Array.FindIndex(state.Props, p => p.Shot == ShotKind.Speaker);
        state.Props[speaker].Supply = -1;
        int decoy = Array.FindIndex(state.Props, p => p.Shot == ShotKind.Decoy);
        state.Props[decoy].Supply = (int)WeaponId.SwapRay;
        int gate = Array.FindIndex(state.Props, p => p.Shot == ShotKind.Gate);
        state.Props[gate].HitMask = (1 << 14) | 5;
        int drop = Array.FindIndex(state.Props, p => p.Shot == ShotKind.Drop);
        state.Props[drop].Timer = 44;
        for (int i = 0; i < fighters; i++)
        {
            ref Fighter f = ref state.Fighters[i];
            f.SlowFrames = 10 + i;
            f.RootFrames = 20 + i;
            f.DotFrames = 30 + i;
            f.DotBy = 1 + (i + 1) % fighters;
            f.DotWeapon = 73;
            f.MarkFrames = 300 - i;
            f.StunImmune = 150;
            f.StunWeapon = 78 + i;
            f.BurstShots = i;
            f.Streak = 2 * i;
            f.StreakTarget = 1 + i;
        }
        for (int i = 0; i < state.WeaponStats.Length; i++) state.WeaponStats[i] = i * 3 + 1;

        byte[] bytes = Save(state);
        Assert.Equal(GameStateCodec.SizeFor(fighters), bytes.Length);
        GameState loaded = Load(bytes, fighters);
        AssertSame(state, loaded);
        Assert.Equal(state.Props, loaded.Props);
        Assert.Equal(state.WeaponStats, loaded.WeaponStats);
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
