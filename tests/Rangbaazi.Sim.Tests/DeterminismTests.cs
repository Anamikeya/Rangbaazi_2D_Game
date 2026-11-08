using System.Reflection;
using Rangbaazi.Sim.Diagnostics;
using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim.Tests;

/// <summary>
/// The guarantees rollback netcode needs: same inputs => same state, and save/load/re-simulate
/// reproduces the original timeline exactly.
/// </summary>
public class DeterminismTests
{
    private static readonly MatchRules Rules = new() { Stage = Stages.Graybox };
    private const int Players = 4;
    private const int Frames = 60 * 60; // one minute of play

    /// <summary>Pseudo-random but reproducible inputs (mash buttons, change direction often).</summary>
    private static Buttons[] InputsFor(int frame, ulong seed)
    {
        var rng = new SimRandom(seed ^ ((ulong)frame * 0x9E3779B97F4A7C15UL));
        var inputs = new Buttons[Players];
        for (int p = 0; p < Players; p++) inputs[p] = (Buttons)(rng.NextUInt() & 0x7FF);
        return inputs;
    }

    [Fact]
    public void SameInputsProduceIdenticalChecksumsEveryFrame()
    {
        var a = Simulation.CreateInitialState(Rules, Players, 42);
        var b = Simulation.CreateInitialState(Rules, Players, 42);

        for (int f = 0; f < Frames; f++)
        {
            var inputs = InputsFor(f, 1);
            Simulation.Advance(a, Rules, inputs);
            Simulation.Advance(b, Rules, inputs);
            Assert.Equal(a.Checksum(), b.Checksum());
        }
    }

    [Fact]
    public void DifferentInputsDiverge()
    {
        var a = Simulation.CreateInitialState(Rules, Players, 42);
        var b = Simulation.CreateInitialState(Rules, Players, 42);

        for (int f = 0; f < 600; f++)
        {
            Simulation.Advance(a, Rules, InputsFor(f, 1));
            Simulation.Advance(b, Rules, InputsFor(f, 2));
        }
        Assert.NotEqual(a.Checksum(), b.Checksum());
    }

    [Fact]
    public void RollbackResimulationMatchesOriginalTimeline()
    {
        // Timeline A: play straight through.
        var reference = Simulation.CreateInitialState(Rules, Players, 7);
        var checksums = new ulong[Frames];
        for (int f = 0; f < Frames; f++)
        {
            Simulation.Advance(reference, Rules, InputsFor(f, 3));
            checksums[f] = reference.Checksum();
        }

        // Timeline B: every 10 frames, mispredict 8 frames ahead, then roll back and correct.
        var live = Simulation.CreateInitialState(Rules, Players, 7);
        const int rollback = 8;
        for (int f = 0; f < Frames; f++)
        {
            if (f % 10 == 0 && f + rollback < Frames)
            {
                GameState saved = live.Clone();
                for (int k = 0; k < rollback; k++) Simulation.Advance(live, Rules, InputsFor(f + k, 999)); // wrong guess
                live.CopyFrom(saved);
            }
            Simulation.Advance(live, Rules, InputsFor(f, 3));
            Assert.Equal(checksums[f], live.Checksum());
        }
    }

    [Fact]
    public void CloneIsIndependent()
    {
        var a = Simulation.CreateInitialState(Rules, 2, 1);
        var b = a.Clone();
        Simulation.Advance(a, Rules, new[] { Buttons.Right, Buttons.Left });
        Assert.NotEqual(a.Checksum(), b.Checksum());
        Assert.Equal(0, b.Frame);
    }
    [Fact]
    public void ChecksumAndCloneCoverEveryFighterField()
    {
        // A new Fighter field that the checksum misses would let peers desync without noticing.
        const BindingFlags instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        Assert.False(System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences<Fighter>(),
            "Fighter must hold no references at any depth, so copying a Fighter copies all of it");
        FieldInfo[] fields = typeof(Fighter).GetFields(instance);
        Assert.True(fields.Length >= 20, $"only {fields.Length} fields found");
        foreach (FieldInfo field in fields)
        {
            Assert.True(field.FieldType.IsValueType, $"Fighter.{field.Name} must be a value type, so copying a Fighter copies it");
            var s = Simulation.CreateInitialState(Rules, 2, 1);
            foreach (object changed in Variants(field.FieldType, field.GetValue(s.Fighters[1])!))
            {
                GameState copy = s.Clone();
                object boxed = copy.Fighters[1];
                field.SetValue(boxed, changed);
                copy.Fighters[1] = (Fighter)boxed;
                Assert.True(s.Checksum() != copy.Checksum(), $"the checksum ignores Fighter.{field.Name}");
                Assert.Equal(copy.Checksum(), copy.Clone().Checksum());
            }
        }
    }

    [Fact]
    public void ChecksumAndCloneCoverEveryGameStateField()
    {
        const BindingFlags instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (FieldInfo field in typeof(GameState).GetFields(instance))
        {
            if (field.Name == nameof(GameState.Fighters)) continue; // covered field by field above
            bool holdsReferences = (bool)typeof(System.Runtime.CompilerServices.RuntimeHelpers)
                .GetMethod(nameof(System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences))!
                .MakeGenericMethod(field.FieldType).Invoke(null, null)!;
            Assert.False(holdsReferences, $"GameState.{field.Name} holds a reference, so CopyFrom could share it; copy it deeply and extend these tests");
            var s = Simulation.CreateInitialState(Rules, 2, 1);
            GameState copy = s.Clone();
            object current = field.GetValue(copy)!;
            object changed = field.FieldType == typeof(SimRandom)
                ? new SimRandom(((SimRandom)current).State + 1)
                : Variants(field.FieldType, current).First();
            field.SetValue(copy, changed);
            Assert.True(s.Checksum() != copy.Checksum(), $"the checksum ignores GameState.{field.Name}");
            Assert.Equal(copy.Checksum(), copy.Clone().Checksum());
        }
    }

    [Fact]
    public void CloneSharesNoMutableReferences()
    {
        // A field copied by reference would let a rollback's saved state change along with the
        // live one. Every reference-type field of GameState must be a distinct object in a clone.
        const BindingFlags instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var s = Simulation.CreateInitialState(Rules, 2, 1);
        GameState copy = s.Clone();
        foreach (FieldInfo field in typeof(GameState).GetFields(instance))
        {
            if (field.FieldType.IsValueType) continue;
            Assert.False(ReferenceEquals(field.GetValue(s), field.GetValue(copy)), $"Clone shares GameState.{field.Name}");
        }

        ulong before = s.Checksum();
        copy.Fighters[0].Josh++;
        copy.Fighters[1].GutsTimer = 5;
        Assert.Equal(before, s.Checksum());
    }

    private static IEnumerable<object> Variants(Type type, object current)
    {
        if (type == typeof(int)) yield return (int)current + 1;
        else if (type == typeof(bool)) yield return !(bool)current;
        else if (type.IsEnum) yield return Enum.ToObject(type, Convert.ToInt64(current) + 1);
        else if (type == typeof(FixVec2))
        {
            var v = (FixVec2)current;
            yield return new FixVec2(v.X + Fix.FromRaw(1), v.Y);
            yield return new FixVec2(v.X, v.Y + Fix.FromRaw(1));
        }
        else throw new InvalidOperationException($"Add a variant for {type} so the checksum test covers it.");
    }

    [Fact]
    public void RollbackThroughParriesHitStopAndGutsMatchesTheOriginalTimeline()
    {
        // Two Hard CPUs (they parry and punish) and two mashers, everyone starting near Guts
        // range on a floor too wide to ring out on, with an 8-frame misprediction every 7 frames.
        // The counts check that the run really went through the new combat state, so the
        // comparison means something.
        const int frames = 60 * 60;
        var rules = new MatchRules { Stage = TestStages.Wide };
        GameState Start()
        {
            var s = Simulation.CreateInitialState(rules, Players, 13);
            for (int p = 0; p < Players; p++) s.Fighters[p].Josh = 120;
            return s;
        }

        void Fill(GameState s, int frame, ulong seed, Buttons[] inputs)
        {
            var rng = new SimRandom(seed ^ ((ulong)frame * 0x9E3779B97F4A7C15UL));
            inputs[0] = SparringBot.Think(s, rules, 0);
            inputs[1] = SparringBot.Think(s, rules, 1);
            inputs[2] = (Buttons)(rng.NextUInt() & 0x7FF);
            inputs[3] = (Buttons)(rng.NextUInt() & 0x7FF);
        }

        GameState reference = Start();
        var checksums = new ulong[frames];
        var inputs = new Buttons[Players];
        int parries = 0, frozen = 0, guts = 0, dodges = 0, heavies = 0;
        for (int f = 0; f < frames; f++)
        {
            Fill(reference, f, 5, inputs);
            Fighter[] before = (Fighter[])reference.Fighters.Clone();
            Simulation.Advance(reference, rules, inputs);
            checksums[f] = reference.Checksum();
            for (int p = 0; p < Players; p++)
            {
                Fighter x = reference.Fighters[p];
                if (x.Action == FighterAction.Stunned && before[p].Action != FighterAction.Stunned) parries++;
                if (x.HitStop > 0) frozen++;
                if (x.ArmorFrames > 0 && before[p].ArmorFrames == 0) guts++;
                if (x.Action == FighterAction.Dodge && x.ActionFrame == 0) dodges++;
                if (x.Action == FighterAction.Attack && x.ActionFrame == 0 && x.Move != MoveId.Jab) heavies++;
            }
        }
        Assert.True(parries > 0 && frozen > 0 && guts > 0 && dodges > 0 && heavies > 0,
            $"parries {parries}, hit-stop frames {frozen}, Guts {guts}, dodges {dodges}, heavies {heavies}");

        GameState live = Start();
        const int rollback = 8;
        for (int f = 0; f < frames; f++)
        {
            if (f % 7 == 0 && f + rollback < frames)
            {
                GameState saved = live.Clone();
                for (int k = 0; k < rollback; k++)
                {
                    Fill(live, f + k, 777, inputs); // wrong guess for the mashers
                    Simulation.Advance(live, rules, inputs);
                }
                live.CopyFrom(saved);
            }
            Fill(live, f, 5, inputs);
            Simulation.Advance(live, rules, inputs);
            Assert.Equal(checksums[f], live.Checksum());
        }
    }
}
