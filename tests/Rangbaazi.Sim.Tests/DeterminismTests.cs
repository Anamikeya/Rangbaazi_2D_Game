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
    public void A_four_team_room_of_fifteen_on_a_big_stage_is_deterministic_and_rolls_back()
    {
        // Rooms v1, section 6.1: 15 CPUs in four teams, on the big level a room of 15 plays.
        int[] teams = { 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4 };
        var rules = MatchRules.Room(Stages.ById("rooftops", StageScaling.SizeFor(15))!, 1, null, teams);
        Assert.Equal(StageSize.Big, rules.Stage.Size);
        const int players = 15, copyAt = 600, frames = 1500;
        var a = Simulation.CreateInitialState(rules, players, 21);
        var b = Simulation.CreateInitialState(rules, players, 21);
        var inputs = new Buttons[players];
        GameState? saved = null;
        for (int f = 0; f < frames; f++)
        {
            if (f == copyAt) saved = a.Clone();
            for (int p = 0; p < players; p++) inputs[p] = SparringBot.Think(a, rules, p, CpuLevel.Hard);
            Simulation.Advance(a, rules, inputs);
            Simulation.Advance(b, rules, inputs);
            Assert.Equal(a.Checksum(), b.Checksum());
        }

        // Back to frame 600 and on again, from the copy: the same checksum at the end.
        var c = Simulation.CreateInitialState(rules, players, 1);
        c.CopyFrom(saved!);
        var d = Simulation.CreateInitialState(rules, players, 21);
        for (int f = 0; f < frames; f++)
        {
            if (f == copyAt) Assert.Equal(d.Checksum(), c.Checksum());
            if (f >= copyAt)
            {
                for (int p = 0; p < players; p++) inputs[p] = SparringBot.Think(c, rules, p, CpuLevel.Hard);
                Simulation.Advance(c, rules, inputs);
            }
            for (int p = 0; p < players; p++) inputs[p] = SparringBot.Think(d, rules, p, CpuLevel.Hard);
            Simulation.Advance(d, rules, inputs);
        }
        Assert.Equal(a.Checksum(), c.Checksum());
        Assert.Equal(a.Checksum(), d.Checksum());
        Assert.True(a.Fighters.Sum(f => f.Kos) > 0, "the CPUs should score");
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
            if (field.Name is nameof(GameState.Fighters) or nameof(GameState.Props) or nameof(GameState.WeaponStats)) continue; // covered on their own
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
    public void ChecksumAndCloneCoverEveryPropField()
    {
        // Props are rollback state too: a field the checksum or the copy misses would desync peers.
        const BindingFlags instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        Assert.False(System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences<Prop>(),
            "Prop must hold no references, so copying the props array copies all of it");
        FieldInfo[] fields = typeof(Prop).GetFields(instance);
        Assert.True(fields.Length >= 15, $"only {fields.Length} fields found");
        foreach (FieldInfo field in fields)
        {
            var s = Simulation.CreateInitialState(Rules, 2, 1);
            foreach (object changed in Variants(field.FieldType, field.GetValue(s.Props[3])!))
            {
                GameState copy = s.Clone();
                object boxed = copy.Props[3];
                field.SetValue(boxed, changed);
                copy.Props[3] = (Prop)boxed;
                Assert.True(s.Checksum() != copy.Checksum(), $"the checksum ignores Prop.{field.Name}");
                Assert.Equal(copy.Checksum(), copy.Clone().Checksum());
                GameState clone = copy.Clone();
                clone.Props[3].Timer++;
                Assert.NotEqual(clone.Checksum(), copy.Checksum()); // the clone has its own array
            }
        }
    }

    [Fact]
    public void ChecksumAndCloneCoverEveryWeaponStat()
    {
        var s = Simulation.CreateInitialState(Rules, 2, 1);
        Assert.Equal(2 * Weapons.RecordInts, s.WeaponStats.Length);
        for (int i = 0; i < s.WeaponStats.Length; i++)
        {
            GameState copy = s.Clone();
            copy.WeaponStats[i]++;
            Assert.True(s.Checksum() != copy.Checksum(), $"the checksum ignores WeaponStats[{i}]");
            Assert.Equal(copy.Checksum(), copy.Clone().Checksum());
            Assert.Equal(0, s.WeaponStats[i]); // the clone has its own array
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

    /// <summary>
    /// Combat spec v0.4, batch 3: a match with every status running (slow, root, poison, burn, mark,
    /// stun immunity, a freeze) and two streams spraying is saved in the middle of a spray, advanced
    /// 120 frames, loaded into a state that has moved on and advanced again: the checksums match
    /// frame by frame. The same start played twice gives the same checksums too.
    /// </summary>
    [Fact]
    public void RollbackWithEveryStatusAndAStreamMidSprayMatchesTheOriginalTimeline()
    {
        var rules = new MatchRules { Stage = Stages.Graybox, Weapons = true, Flurry = true };
        GameState Start()
        {
            var s = Simulation.CreateInitialState(rules, Players, 31);
            int[] xs = { -3, 3, -7, -1 };
            for (int p = 0; p < Players; p++)
            {
                ref Fighter f = ref s.Fighters[p];
                f.Position = new FixVec2(Fix.FromInt(xs[p]), Fix.Zero);
                f.Velocity = FixVec2.Zero;
                f.Grounded = true;
                f.Facing = xs[p] < 0 ? 1 : -1;
            }
            s.Fighters[0].Weapon = WeaponId.Flamethrower;
            s.Fighters[0].WeaponSupply = 24;
            s.Fighters[1].Weapon = WeaponId.FreezeRay;
            s.Fighters[1].WeaponSupply = 30;
            ref Fighter rooted = ref s.Fighters[2];
            rooted.RootFrames = 90;
            rooted.StunWeapon = (int)WeaponId.Net;
            rooted.StunImmune = 180;
            rooted.SlowFrames = 60;
            rooted.MarkFrames = 300;
            rooted.DotFrames = 240;
            rooted.DotBy = 2;
            rooted.DotWeapon = (int)WeaponId.VenomDartLauncher;
            ref Fighter frozen = ref s.Fighters[3];
            frozen.Action = FighterAction.Stunned;
            frozen.HitstunFrames = 60;
            frozen.StunWeapon = (int)WeaponId.FreezeRay;
            frozen.StunImmune = 210;
            frozen.DotFrames = 90;
            frozen.DotBy = 1;
            frozen.DotWeapon = (int)WeaponId.Flamethrower;
            return s;
        }
        void Fill(GameState s, Buttons[] inputs)
        {
            inputs[0] = Buttons.Light;
            inputs[1] = s.Frame % 50 < 40 ? Buttons.Light : Buttons.None;
            inputs[2] = s.Frame % 30 == 0 ? Buttons.Light | Buttons.Left : Buttons.Right;
            inputs[3] = SparringBot.Think(s, rules, 3, CpuLevel.Hard);
        }
        ulong[] Run(out GameState state)
        {
            state = Start();
            var sums = new ulong[300];
            var inputs = new Buttons[Players];
            for (int f = 0; f < sums.Length; f++)
            {
                Fill(state, inputs);
                Simulation.Advance(state, rules, inputs);
                sums[f] = state.Checksum();
            }
            return sums;
        }
        Assert.Equal(Run(out _), Run(out _));

        GameState live = Start();
        var buttons = new Buttons[Players];
        for (int f = 0; f < 20; f++)
        {
            Fill(live, buttons);
            Simulation.Advance(live, rules, buttons);
        }
        Assert.Equal(FighterAction.Attack, live.Fighters[0].Action); // mid-spray
        Assert.True(Simulation.TryGetActiveHitbox(in live.Fighters[0], rules.Tuning, out _));
        GameState saved = live.Clone();
        var expected = new ulong[120];
        int[] seen = new int[8];
        for (int i = 0; i < expected.Length; i++)
        {
            Fill(live, buttons);
            Simulation.Advance(live, rules, buttons);
            expected[i] = live.Checksum();
            foreach (Fighter f in live.Fighters)
            {
                if (f.SlowFrames > 0) seen[0]++;
                if (f.RootFrames > 0) seen[1]++;
                if (f.DotFrames > 0) seen[2]++;
                if (f.MarkFrames > 0) seen[3]++;
                if (f.StunImmune > 0) seen[4]++;
                if (f.StunWeapon != 0) seen[5]++;
            }
        }
        Assert.All(seen[..6], n => Assert.True(n > 0, "every status was running"));
        GameState replay = live.Clone();
        replay.CopyFrom(saved);
        for (int i = 0; i < expected.Length; i++)
        {
            Fill(replay, buttons);
            Simulation.Advance(replay, rules, buttons);
            Assert.True(expected[i] == replay.Checksum(), $"the replay differs {i + 1} frames after the save");
        }
    }

    /// <summary>
    /// Combat spec v0.4, batch 4: a match with a held guard, traps, a puddle, a cloud, bombs that
    /// leave a wall and a patch, and the Gauntlet cycling is saved in the middle of the guard,
    /// advanced 150 frames (across the frames where a burst becomes a wall or a zone), loaded into a
    /// state that has moved on and advanced again: the checksums match frame by frame. The same
    /// start played twice gives the same checksums too.
    /// </summary>
    [Fact]
    public void RollbackAcrossGuardsTrapsAndPropsThatChangeRoleMatchesTheOriginalTimeline()
    {
        var rules = new MatchRules { Stage = Stages.Graybox, Weapons = true, Flurry = true };
        GameState Start()
        {
            var s = Simulation.CreateInitialState(rules, Players, 41);
            int[] xs = { -1, 3, -6, 6 };
            for (int p = 0; p < Players; p++)
            {
                ref Fighter f = ref s.Fighters[p];
                f.Position = new FixVec2(Fix.FromInt(xs[p]), Fix.Zero);
                f.Velocity = FixVec2.Zero;
                f.Grounded = true;
                f.Facing = xs[p] < 0 ? 1 : -1;
            }
            (WeaponId, int)[] arms = { (WeaponId.Parat, 8), (WeaponId.Matka, 3), (WeaponId.GlueGun, 12), (WeaponId.JugaadGauntlet, 9) };
            for (int p = 0; p < Players; p++)
            {
                s.Fighters[p].Weapon = arms[p].Item1;
                s.Fighters[p].WeaponSupply = arms[p].Item2;
            }
            int slot = s.Props.Length - 1;
            s.Props[slot--] = new Prop { Kind = PropKind.Shot, Shot = ShotKind.Mine, Weapon = WeaponId.MouseTrap, Owner = 2, Position = new FixVec2(Fix.FromInt(1), Fix.Zero), Grounded = true, Timer = 10 };
            s.Props[slot--] = new Prop { Kind = PropKind.Shot, Shot = ShotKind.Mine, Weapon = WeaponId.BananaPeel, Owner = 3, Position = new FixVec2(Fix.FromInt(-3), Fix.Zero), Grounded = true };
            s.Props[slot--] = new Prop { Kind = PropKind.Shot, Shot = ShotKind.Zone, Weapon = WeaponId.Lota, Owner = 1, Position = new FixVec2(Fix.FromInt(4), Fix.Zero), Grounded = true, Timer = 300 };
            s.Props[slot--] = new Prop { Kind = PropKind.Shot, Shot = ShotKind.Zone, Weapon = WeaponId.StinkCloudCapsule, Owner = 0, Position = new FixVec2(Fix.FromInt(-5), Fix.Zero), Grounded = true, Timer = 360 };
            s.Props[slot] = new Prop { Kind = PropKind.Shot, Shot = ShotKind.Mine, Weapon = WeaponId.VenomTrap, Owner = 1, Position = new FixVec2(Fix.FromInt(5), Fix.Zero), Grounded = true, Timer = 5 };
            return s;
        }
        void Fill(GameState s, Buttons[] inputs)
        {
            inputs[0] = s.Frame % 70 < 50 ? Buttons.Light : Buttons.None; // holds the Parat's guard
            inputs[1] = s.Frame % 40 == 0 ? Buttons.Light : s.Frame % 40 < 20 ? Buttons.Left : Buttons.Right;
            inputs[2] = s.Frame % 30 < 15 ? Buttons.Light | Buttons.Right : Buttons.Left;
            inputs[3] = SparringBot.Think(s, rules, 3, CpuLevel.Hard);
        }
        ulong[] Run()
        {
            GameState state = Start();
            var sums = new ulong[300];
            var inputs = new Buttons[Players];
            for (int f = 0; f < sums.Length; f++)
            {
                Fill(state, inputs);
                Simulation.Advance(state, rules, inputs);
                sums[f] = state.Checksum();
            }
            return sums;
        }
        Assert.Equal(Run(), Run());

        GameState live = Start();
        var buttons = new Buttons[Players];
        for (int f = 0; f < 6; f++)
        {
            Fill(live, buttons);
            Simulation.Advance(live, rules, buttons);
        }
        Assert.True(Simulation.Guarding(in live.Fighters[0])); // mid-guard
        GameState saved = live.Clone();
        var expected = new ulong[150];
        bool wall = false, patch = false, guarded = false;
        for (int i = 0; i < expected.Length; i++)
        {
            Fill(live, buttons);
            Simulation.Advance(live, rules, buttons);
            expected[i] = live.Checksum();
            wall |= live.Props.Any(p => p.Kind == PropKind.Shot && p.Shot == ShotKind.Wall);
            patch |= live.Props.Any(p => p.Kind == PropKind.Shot && p.Shot == ShotKind.Zone && p.Weapon == WeaponId.GlueGun);
            guarded |= Simulation.Guarding(in live.Fighters[0]);
        }
        Assert.True(wall, "a Matka left a wall");
        Assert.True(patch, "a glue blob left a patch");
        Assert.True(guarded);
        GameState replay = live.Clone();
        replay.CopyFrom(saved);
        for (int i = 0; i < expected.Length; i++)
        {
            Fill(replay, buttons);
            Simulation.Advance(replay, rules, buttons);
            Assert.True(expected[i] == replay.Checksum(), $"the replay differs {i + 1} frames after the save");
        }
    }

    /// <summary>
    /// Combat spec v0.4, batch 5: a match with every status running and one prop of each of the 17
    /// flights on the stage (a speaker, a pair of gates, a hanging anvil, a Decoy Crate and a rigged
    /// crate among them), a box for a Hockey Stick to fling, a Matka and a Lota that land and leave
    /// a wall and a puddle, and a Bowling Ball about to come to rest, with the batch's weapons in
    /// hand (a Teleporter blinking, a Detonator placing and bursting, a CPU with the Swap Ray). It is
    /// saved every 10 frames over its first 160 (so across the frame of each Become), advanced 120
    /// frames, loaded into a state that has moved on and advanced again: the checksums match frame
    /// by frame. The same start played twice gives the same checksums too.
    /// </summary>
    [Fact]
    public void RollbackWithEveryStatusAndOneOfEachPropMatchesTheOriginalTimeline()
    {
        var rules = new MatchRules { Stage = Stages.Graybox, Weapons = true, Flurry = true };
        GameState Start()
        {
            var s = Simulation.CreateInitialState(rules, Players, 51);
            s.PropTimer = 40;
            s.LastBoxWeapon = WeaponId.Uzi;
            int[] xs = { -3, 3, -7, 7 };
            (WeaponId, int)[] arms = { (WeaponId.Teleporter, 3), (WeaponId.CrateDetonator, 6), (WeaponId.HockeyStick, 8), (WeaponId.SwapRay, 3) };
            for (int p = 0; p < Players; p++)
            {
                ref Fighter f = ref s.Fighters[p];
                f.Position = new FixVec2(Fix.FromInt(xs[p]), Fix.Zero);
                f.Velocity = FixVec2.Zero;
                f.Grounded = true;
                f.Facing = xs[p] < 0 ? 1 : -1;
                f.Weapon = arms[p].Item1;
                f.WeaponSupply = arms[p].Item2;
            }
            s.Fighters[0].SlowFrames = 100;
            s.Fighters[0].MarkFrames = 200;
            s.Fighters[1].RootFrames = 60;
            s.Fighters[1].StunWeapon = (int)WeaponId.Net;
            s.Fighters[1].StunImmune = 150;
            s.Fighters[2].DotFrames = 240;
            s.Fighters[2].DotBy = 2;
            s.Fighters[2].DotWeapon = (int)WeaponId.VenomDartLauncher;
            s.Fighters[3].DotFrames = 90;
            s.Fighters[3].DotBy = 1;
            s.Fighters[3].DotWeapon = (int)WeaponId.Flamethrower;
            s.Fighters[3].Action = FighterAction.Stunned;
            s.Fighters[3].HitstunFrames = 40;
            s.Fighters[3].StunWeapon = (int)WeaponId.FreezeRay;

            // One of each flight, and the props that are about to change role. (x, y in cm.)
            (ShotKind, WeaponId, int, int, int, int, int, int)[] props =
            {
                // flight, weapon, x, y, vx, timer, owner, supply
                (ShotKind.Roll, WeaponId.Chicken, 100, 0, 16, 0, 0, 0),
                (ShotKind.Bullet, WeaponId.Uzi, 600, 80, -70, 0, 3, 0),
                (ShotKind.Bomb, WeaponId.Matka, 100, 300, 0, 90, 2, 0),
                (ShotKind.Arrow, WeaponId.Bow, 800, 120, -55, 0, 0, 0),
                (ShotKind.Thrown, WeaponId.Belan, -560, 320, 32, 0, 3, 0),
                (ShotKind.Boomerang, WeaponId.Boomerang, 500, 100, -50, 0, 3, 3),
                (ShotKind.Pad, WeaponId.Trampoline, 850, 0, 0, 600, 1, 0),
                (ShotKind.Mine, WeaponId.Mines, -500, 0, 0, 0, 3, 0),
                (ShotKind.Missile, WeaponId.HomingMissile, 0, 600, 20, 0, 1, 0),
                (ShotKind.Hole, WeaponId.BlackHoleBomb, 400, 0, 0, 100, 0, 0),
                (ShotKind.Lob, WeaponId.Snowballs, -200, 200, 28, 0, 2, 0),
                (ShotKind.Zone, WeaponId.StinkCloudCapsule, -100, 0, 0, 300, 2, 0),
                (ShotKind.Wall, WeaponId.Matka, 200, 0, 0, 240, 3, 0),
                (ShotKind.Speaker, WeaponId.SonicSpeaker, -150, 0, 0, 480, 2, 1),
                (ShotKind.Gate, WeaponId.PortalDisc, -850, 0, 0, 600, 3, 0),
                (ShotKind.Gate, WeaponId.PortalDisc, 50, 300, 0, 600, 3, 0),
                (ShotKind.Drop, WeaponId.Anvil, 300, 900, 0, 30, 0, 0),
                (ShotKind.Decoy, WeaponId.DecoyCrate, -250, 0, 0, 1200, 1, (int)WeaponId.Uzi),
                (ShotKind.Decoy, WeaponId.CrateDetonator, 450, 0, 0, 1200, 1, (int)WeaponId.Uzi),
                (ShotKind.Bomb, WeaponId.Lota, -350, 250, 0, 60, 0, 0),
                (ShotKind.Roll, WeaponId.BowlingBall, 600, 0, -22, 0, 2, 0),
            };
            int slot = s.Props.Length - 1;
            foreach (var (shot, w, x, y, vx, timer, owner, supply) in props)
                s.Props[slot--] = new Prop
                {
                    Kind = PropKind.Shot, Shot = shot, Weapon = w, Owner = owner, OwnerClear = true, Supply = supply,
                    Position = new FixVec2(Fix.FromRatio(x, 100), Fix.FromRatio(y, 100)), Velocity = new FixVec2(Fix.FromRatio(vx, 100), Fix.Zero),
                    Grounded = y == 0, Timer = timer, Age = w == WeaponId.BowlingBall ? Weapons.Get(w).Life - 30 : 0,
                };
            s.Props[0] = new Prop { Kind = PropKind.Box, Weapon = WeaponId.Bow, Supply = 6, Owner = -1, Position = new FixVec2(Fix.FromInt(-6), Fix.Zero), Grounded = true };
            return s;
        }
        void Fill(GameState s, Buttons[] inputs)
        {
            inputs[0] = s.Frame % 40 == 0 ? Buttons.Light | (s.Frame % 80 == 0 ? Buttons.Up : Buttons.None) : s.Frame % 40 < 20 ? Buttons.Right : Buttons.Left;
            inputs[1] = s.Frame % 45 == 5 ? Buttons.Light : s.Frame % 60 < 30 ? Buttons.Left : Buttons.Right;
            inputs[2] = s.Frame % 30 == 0 ? Buttons.Light : Buttons.Right;
            inputs[3] = SparringBot.Think(s, rules, 3, CpuLevel.Hard);
        }
        ulong[] Run()
        {
            GameState state = Start();
            var sums = new ulong[300];
            var inputs = new Buttons[Players];
            for (int f = 0; f < sums.Length; f++)
            {
                Fill(state, inputs);
                Simulation.Advance(state, rules, inputs);
                sums[f] = state.Checksum();
            }
            return sums;
        }
        Assert.Equal(Run(), Run());

        GameState live = Start();
        var buttons = new Buttons[Players];
        var flights = new HashSet<ShotKind>();
        bool wall = false, puddle = false, rested = false, flung = false, blinked = false;
        int[] seen = new int[6];
        for (int from = 0; from <= 160; from += 10)
        {
            while (live.Frame < from)
            {
                Fill(live, buttons);
                Simulation.Advance(live, rules, buttons);
            }
            GameState saved = live.Clone();
            GameState ahead = live.Clone();
            var expected = new ulong[120];
            for (int i = 0; i < expected.Length; i++)
            {
                Fill(ahead, buttons);
                Simulation.Advance(ahead, rules, buttons);
                expected[i] = ahead.Checksum();
                foreach (Prop p in ahead.Props)
                {
                    if (p.Kind == PropKind.Shot) flights.Add(p.Shot);
                    wall |= p.Kind == PropKind.Shot && p.Shot == ShotKind.Wall && p.Weapon == WeaponId.Matka && p.Timer < 240 - 30;
                    puddle |= p.Kind == PropKind.Shot && p.Shot == ShotKind.Zone && p.Weapon == WeaponId.Lota;
                    rested |= p.Kind == PropKind.Item && p.Weapon == WeaponId.BowlingBall;
                    flung |= p.Kind == PropKind.Shot && p.Shot == ShotKind.Thrown && p.Supply > 0;
                }
                blinked |= ahead.Fighters[0].WeaponSupply < 3 && ahead.Fighters[0].Weapon == WeaponId.Teleporter;
                foreach (Fighter f in ahead.Fighters)
                {
                    if (f.SlowFrames > 0) seen[0]++;
                    if (f.RootFrames > 0) seen[1]++;
                    if (f.DotFrames > 0) seen[2]++;
                    if (f.MarkFrames > 0) seen[3]++;
                    if (f.StunImmune > 0) seen[4]++;
                    if (f.StunWeapon != 0) seen[5]++;
                }
            }
            GameState replay = ahead.Clone();
            replay.CopyFrom(saved);
            for (int i = 0; i < expected.Length; i++)
            {
                Fill(replay, buttons);
                Simulation.Advance(replay, rules, buttons);
                Assert.True(expected[i] == replay.Checksum(), $"the replay from frame {from} differs {i + 1} frames after the save");
            }
        }
        for (int k = 1; k <= 17; k++) Assert.Contains((ShotKind)k, flights);
        Assert.All(seen, n => Assert.True(n > 0, "every status was running"));
        Assert.True(wall, "a Matka left a wall");
        Assert.True(puddle, "a Lota left a puddle");
        Assert.True(rested, "the Bowling Ball came to rest");
        Assert.True(blinked, "the Teleporter blinked");
        Assert.True(flung, "the Hockey Stick flung the box");
    }
}
