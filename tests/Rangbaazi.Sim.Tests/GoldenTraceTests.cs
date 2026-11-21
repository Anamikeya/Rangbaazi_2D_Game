using System.Text;
using Rangbaazi.Sim.Diagnostics;
using Rangbaazi.Sim.FixedMath;
using Xunit.Abstractions;

namespace Rangbaazi.Sim.Tests;

/// <summary>
/// Golden traces for the GDScript port of the sim (client/sim): scripted matches with every
/// frame's inputs and the checksum after it, written to client/tests/golden. The GDScript tests
/// replay them and must match bit for bit, which proves the port behaves exactly like this
/// reference implementation. Run with RANGBAAZI_WRITE_GOLDENS=1 to (re)write the files; otherwise
/// this checks the files still match the C# sim.
/// </summary>
public class GoldenTraceTests
{
    private readonly ITestOutputHelper output;

    public GoldenTraceTests(ITestOutputHelper output) => this.output = output;

    /// <summary>
    /// A scripted match. <c>TimeLimit</c> &gt; 0 plays it under room rules on Rooftops (timed, rage, second wind).
    /// <c>Stocks</c> is each fighter's stocks in a stock match (99 keeps everyone in it to the end).
    /// <c>ArmNewer</c> hands every fighter one of the five newer weapons every 90 frames (the
    /// boomerang on), so each of them is in the trace whatever the boxes hold.
    /// <c>ArmEvery</c> &gt; 0 does the same over every weapon a crate can hold: every so many frames
    /// <c>f</c>, fighter <c>p</c> is given <c>list[(f / ArmEvery + p) % n]</c>, the list being
    /// <c>CrateList</c>, or with <c>ArmLists</c> the list <c>ArmLists[p % ArmLists.Length]</c>.
    /// <c>HoldLight</c> drives the first so many fighters (random-input scenarios only): see
    /// <see cref="HoldLightInput"/>. <c>Teams</c> puts the fighters in teams (<see cref="MatchRules.Teams"/>).
    /// <c>Size</c> plays a room scenario on the big level (<see cref="StageScaling"/>).
    /// </summary>
    private sealed record Scenario(string Name, int Players, ulong Seed, int Frames, bool Guts, CpuLevel[]? Bots, ulong InputSeed, int TimeLimit = 0,
        bool Weapons = false, WeaponId[]? Signatures = null, bool ArmNewer = false, int ArmEvery = 0, int HoldLight = 0, int[]? Teams = null,
        int Stocks = 3, WeaponId[][]? ArmLists = null, int PressEvery = 30, StageSize Size = StageSize.Normal);

    /// <summary>The all-weapons traces give each weapon 90 frames: they grow as crates hold more.</summary>
    private static readonly int AllWeaponsFrames = 90 * Weapons.CrateList.Length;

    /// <summary>
    /// The big-room trace's weapons. Even fighters only get shots that leave nothing behind (an Uzi
    /// among them, for bullets); odd fighters take turns between placed props that stay out (pads,
    /// gates, a decoy) and such shots. So the prop table fills; odd fighters sit at the placed limit
    /// of a big room (2), and a shot one fires into the full table replaces their own oldest placed
    /// prop; an even fighter who fires into a full table with no bullet in it has no placed prop of
    /// its own to replace, and its shot fizzles.
    /// </summary>
    private static readonly WeaponId[][] BigRoomLists =
    {
        new[] { WeaponId.BlackHoleBomb, WeaponId.TiffinStack, WeaponId.Bow, WeaponId.RubberChickenLauncher, WeaponId.SteelTumbler, WeaponId.Uzi,
            WeaponId.Chakram, WeaponId.BlackHoleBomb },
        new[] { WeaponId.Trampoline, WeaponId.TiffinStack, WeaponId.PortalDisc, WeaponId.Bow, WeaponId.Trampoline, WeaponId.RubberChickenLauncher,
            WeaponId.DecoyCrate, WeaponId.SteelTumbler },
    };

    private static readonly WeaponId[] SomeSignatures =
    {
        WeaponId.Uzi, WeaponId.Bow, WeaponId.None, WeaponId.BowlingBall, WeaponId.BaseballBat, WeaponId.CherryBombs,
    };

    private static readonly Scenario[] Scenarios =
    {
        new("random_2p", 2, 42, 1800, true, null, 3),
        new("random_4p", 4, 7, 1200, true, null, 11),
        new("bots_normal_vs_hard", 2, 1, 3000, true, new[] { CpuLevel.Normal, CpuLevel.Hard }, 0),
        new("bots_hard_vs_hard_no_guts", 2, 5, 3000, false, new[] { CpuLevel.Hard, CpuLevel.Hard }, 0),
        new("bots_easy_vs_normal", 2, 9, 2400, true, new[] { CpuLevel.Easy, CpuLevel.Normal }, 0),
        new("room_random_15p", 15, 3, 2460, true, null, 17, TimeLimit: 2400, Weapons: true, Signatures: SomeSignatures),
        new("room_bots_6p", 6, 21, 2760, true, new[] { CpuLevel.Hard, CpuLevel.Hard, CpuLevel.Normal, CpuLevel.Normal, CpuLevel.Easy, CpuLevel.Hard }, 0, TimeLimit: 2700,
            Weapons: true, Signatures: SomeSignatures),
        new("weapons_random_4p", 4, 31, 2400, true, null, 23, Weapons: true, Signatures: SomeSignatures),
        new("weapons_bots_4p", 4, 8, 3600, true, new[] { CpuLevel.Hard, CpuLevel.Normal, CpuLevel.Hard, CpuLevel.Easy }, 0,
            Weapons: true, Signatures: SomeSignatures),
        new("newer_weapons_random_4p", 4, 13, 2400, true, null, 29, Weapons: true, Signatures: SomeSignatures, ArmNewer: true),
        new("newer_weapons_bots_4p", 4, 17, 3600, true, new[] { CpuLevel.Hard, CpuLevel.Normal, CpuLevel.Hard, CpuLevel.Easy }, 0,
            Weapons: true, Signatures: SomeSignatures, ArmNewer: true),
        // 99 stocks: nobody is out before the end, so every weapon is played (Coverage checks it).
        new("all_weapons_random_4p", 4, 41, AllWeaponsFrames, true, null, 37, Weapons: true, Signatures: SomeSignatures, ArmEvery: 90, HoldLight: 3,
            Stocks: 99),
        new("all_weapons_bots_4p", 4, 43, AllWeaponsFrames, true, new[] { CpuLevel.Hard, CpuLevel.Normal, CpuLevel.Hard, CpuLevel.Easy }, 0,
            Weapons: true, Signatures: SomeSignatures, ArmEvery: 90, Stocks: 99),
        // Fifteen fighters under room rules, all firing: the placed limit of a big room (2), a full
        // prop table, shots that replace a bullet or their firer's own placed prop, and shots that fizzle.
        new("big_room_props_15p", 15, 59, 1800, true, null, 61, TimeLimit: 1800, Weapons: true, ArmEvery: 30, HoldLight: 15,
            ArmLists: BigRoomLists, PressEvery: 6),
        new("all_weapons_room_6p", 6, 47, AllWeaponsFrames + 60, true,
            new[] { CpuLevel.Hard, CpuLevel.Hard, CpuLevel.Normal, CpuLevel.Normal, CpuLevel.Easy, CpuLevel.Hard }, 0, TimeLimit: AllWeaponsFrames,
            Weapons: true, Signatures: SomeSignatures, ArmEvery: 45),
        // Two teams of three with every weapon: the only trace in which Allies is more than "the same fighter".
        new("all_weapons_teams_6p", 6, 53, AllWeaponsFrames + 60, true,
            new[] { CpuLevel.Hard, CpuLevel.Normal, CpuLevel.Hard, CpuLevel.Hard, CpuLevel.Easy, CpuLevel.Normal }, 0, TimeLimit: AllWeaponsFrames,
            Weapons: true, Signatures: SomeSignatures, ArmEvery: 45, Teams: new[] { 1, 2, 1, 2, 1, 2 }),
        // A full room of four teams on the big level (rooms v1, 6.2): 15 CPUs, weapons and hazards on.
        new("room_big_teams_15p", 15, 67, 2460, true,
            new[] { CpuLevel.Hard, CpuLevel.Normal, CpuLevel.Hard, CpuLevel.Easy, CpuLevel.Normal, CpuLevel.Hard, CpuLevel.Hard, CpuLevel.Normal,
                CpuLevel.Easy, CpuLevel.Hard, CpuLevel.Normal, CpuLevel.Hard, CpuLevel.Normal, CpuLevel.Hard, CpuLevel.Easy }, 0, TimeLimit: 2400,
            Weapons: true, Signatures: SomeSignatures, Teams: new[] { 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4 }, Size: StageSize.Big),
    };

    /// <summary>The rules a scenario plays under; room scenarios copy <see cref="MatchRules.Room"/> with a short clock.</summary>
    private static MatchRules RulesFor(Scenario s)
    {
        if (s.TimeLimit <= 0)
            return new MatchRules { Stage = Stages.Graybox, Guts = s.Guts, Weapons = s.Weapons, Flurry = s.Weapons, Signatures = s.Signatures, Teams = s.Teams, Stocks = s.Stocks };
        MatchRules room = MatchRules.Room(Stages.Rooftops, 1);
        return new MatchRules
        {
            Stage = s.Size == StageSize.Big ? Stages.ById(room.Stage.Id, StageSize.Big)! : room.Stage,
            Guts = s.Guts,
            TimeLimitFrames = s.TimeLimit,
            RagePercent = room.RagePercent,
            RespawnShield = room.RespawnShield,
            Weapons = s.Weapons,
            Flurry = s.Weapons,
            Hazards = true,
            Signatures = s.Signatures,
            Teams = s.Teams,
        };
    }

    /// <summary>
    /// What a trace played, counted from the state frame by frame (the sim keeps no counters): the
    /// frames each row's move was out or a prop of that row was on the stage, Teleporter blinks
    /// (and upward ones), Charpai pads, Chimta disarms, and for the prop table: a fighter placing
    /// a prop at the placed limit, a shot put in a full table over a bullet or over its firer's own
    /// placed prop, and a shot that fizzled for want of a slot.
    /// </summary>
    private sealed class Coverage
    {
        public readonly int[] RowFrames = new int[WeaponTable.Count];
        public int Blinks, UpBlinks, CharpaiPads, Disarms, AtPlacedLimit, OverBullet, OverOwnPlaced, Fizzles, MostShots, FullFrames;
        public bool MatchOverEarly;
    }

    private static string Trace(Scenario s, out Coverage seen)
    {
        if (s.HoldLight > 0 && s.Bots != null) throw new InvalidOperationException("HoldLight drives random-input fighters only");
        MatchRules rules = RulesFor(s);
        GameState state = Simulation.CreateInitialState(rules, s.Players, s.Seed);
        var inputs = new Buttons[s.Players];
        seen = new Coverage();
        var json = new StringBuilder();
        json.Append("{\n");
        json.Append($"  \"name\": \"{s.Name}\",\n  \"players\": {s.Players},\n  \"seed\": {s.Seed},\n  \"frames\": {s.Frames},\n");
        json.Append($"  \"guts\": {(s.Guts ? "true" : "false")},\n");
        json.Append($"  \"stage\": \"{rules.Stage.Id}\",\n");
        if (rules.Stage.Size == StageSize.Big) json.Append("  \"stage_size\": \"big\",\n"); // only here, so the other traces keep their bytes
        json.Append($"  \"time_limit_frames\": {rules.TimeLimitFrames},\n");
        json.Append($"  \"stocks\": {rules.Stocks},\n");
        json.Append($"  \"weapons\": {(rules.Weapons ? "true" : "false")},\n");
        json.Append($"  \"hazards\": {(rules.Hazards ? "true" : "false")},\n");
        json.Append($"  \"flurry\": {(rules.Flurry ? "true" : "false")},\n");
        json.Append($"  \"signatures\": [{string.Join(", ", (rules.Signatures ?? Array.Empty<WeaponId>()).Select(w => ((int)w).ToString()))}],\n");
        json.Append($"  \"arm_newer\": {(s.ArmNewer ? "true" : "false")},\n");
        json.Append($"  \"arm_every\": {s.ArmEvery},\n");
        json.Append($"  \"arm_lists\": [{string.Join(", ", (s.ArmLists ?? Array.Empty<WeaponId[]>()).Select(l => $"[{string.Join(", ", l.Select(w => ((int)w).ToString()))}]"))}],\n");
        json.Append($"  \"teams\": [{string.Join(", ", rules.Teams ?? Array.Empty<int>())}],\n");
        json.Append(s.Bots == null ? "  \"bots\": null,\n" : $"  \"bots\": [{string.Join(", ", s.Bots.Select(b => $"\"{b.ToString().ToLowerInvariant()}\""))}],\n");
        json.Append($"  \"initial\": \"{state.Checksum():X16}\",\n");
        json.Append("  \"steps\": [\n");
        for (int f = 0; f < s.Frames; f++)
        {
            if (s.ArmNewer && f % 90 == 0)
                for (int p = 0; p < s.Players; p++)
                {
                    if (!state.Fighters[p].IsActive) continue;
                    var w = (WeaponId)((int)WeaponId.Boomerang + (f / 90 + p) % 5);
                    state.Fighters[p].Weapon = w;
                    state.Fighters[p].WeaponSupply = Weapons.Get(w).Supply;
                }
            if (s.ArmEvery > 0 && f % s.ArmEvery == 0)
                for (int p = 0; p < s.Players; p++)
                {
                    if (!state.Fighters[p].IsActive) continue;
                    WeaponId[] list = s.ArmLists != null ? s.ArmLists[p % s.ArmLists.Length] : Weapons.CrateList;
                    WeaponId w = list[(f / s.ArmEvery + p) % list.Length];
                    state.Fighters[p].Weapon = w;
                    state.Fighters[p].WeaponSupply = Weapons.Get(w).Supply;
                }
            for (int p = 0; p < s.Players; p++)
            {
                inputs[p] = s.Bots != null
                    ? SparringBot.Think(state, rules, p, s.Bots[p])
                    : RandomInput(s.InputSeed, f, p);
                if (p < s.HoldLight) inputs[p] = HoldLightInput(s, f, p, inputs[p]);
            }
            GameState before = state.Clone();
            Simulation.Advance(state, rules, inputs);
            Count(seen, before, state, inputs);
            json.Append($"    [[{string.Join(",", inputs.Select(b => ((int)b).ToString()))}], \"{state.Checksum():X16}\"]");
            json.Append(f + 1 < s.Frames ? ",\n" : "\n");
        }
        seen.MatchOverEarly = state.MatchOver && (rules.TimeLimitFrames <= 0 || state.Frame < rules.TimeLimitFrames);
        json.Append("  ]\n}\n");
        return json.ToString();
    }

    /// <summary>
    /// A driven fighter's buttons: it keeps its random steering and, through each weapon's window
    /// of <c>ArmEvery</c> frames, holds Light, letting go for one frame every <c>PressEvery</c> so
    /// that each is a fresh press (the Gauntlet's three rows come out in turn). Every other press is
    /// Up + Light (the Teleporter's upward blink). In a window of 90 frames or more, one of each
    /// three neighbouring fighters (who are handed the same weapon one window apart) makes only two
    /// presses and then throws the weapon, pressing Drop weapon afresh every other frame from frame
    /// 60 (a thrown Gas Cylinder, Charpai or Spear becomes its hidden row).
    /// </summary>
    private static Buttons HoldLightInput(Scenario s, int f, int p, Buttons random)
    {
        int since = f % s.ArmEvery;
        Buttons b = random & (Buttons.Left | Buttons.Right);
        // One of each three neighbouring fighters throws the weapon late in its window: no third
        // press, then Drop weapon pressed afresh every other frame.
        bool drops = s.ArmEvery >= 90 && (f / s.ArmEvery + 2 * p) % 3 == 2;
        if (drops && since >= 60) return since % 2 == 0 && since < 80 ? b | Buttons.DropWeapon : b;
        // Up is held through every other press, starting with the first for odd fighters.
        if ((since / s.PressEvery + p) % 2 == 1) b |= Buttons.Up;
        return since % s.PressEvery == 0 ? b : b | Buttons.Light;
    }

    private static void Count(Coverage seen, GameState before, GameState after, Buttons[] inputs)
    {
        Fighter[] fighters = after.Fighters;
        Prop[] was = before.Props, now = after.Props;
        int boxSlots = Weapons.BoxSlots(fighters.Length);
        int limit = Weapons.PlacedLimit(fighters.Length);
        bool full = true, bullets = false;
        for (int i = boxSlots; i < was.Length; i++)
        {
            full &= was[i].Kind != PropKind.None;
            bullets |= was[i].Kind == PropKind.Shot && was[i].Shot == ShotKind.Bullet;
        }
        for (int i = 0; i < fighters.Length; i++)
        {
            ref Fighter a = ref fighters[i];
            ref Fighter b = ref before.Fighters[i];
            WeaponRow? row = a.Action == FighterAction.Attack ? Weapons.RowOfMove(a.Move) : null;
            if (row != null) seen.RowFrames[(int)row.Id]++;
            if (row != null && row.Use == WeaponUse.Blink && b.Weapon == WeaponId.Teleporter && a.WeaponSupply < b.WeaponSupply)
            {
                seen.Blinks++;
                if ((inputs[i] & Buttons.Up) != 0) seen.UpBlinks++;
            }
            // A disarm: the weapon left a hand that wasn't throwing, firing or swapping it, while a Chimta's move was out.
            if (b.Weapon != WeaponId.None && a.Weapon == WeaponId.None && a.Action == FighterAction.Hitstun
                && fighters.Any(o => o.Action == FighterAction.Attack && Weapons.RowOfMove(o.Move)?.Effect == WeaponEffect.Disarm))
                seen.Disarms++;
            // A shot of this fighter's that fizzled: its row's startup frame passed this frame with
            // nothing spent, nothing let go, and no new shot of its own.
            if (row != null && row.Use == WeaponUse.Shoot && a.ActionFrame == row.Startup && b.ActionFrame == row.Startup - 1 && b.Move == a.Move
                && a.Weapon == b.Weapon && a.WeaponSupply == b.WeaponSupply && (row.Spend == WeaponSpend.Use || row.Leave) && full && !bullets
                && !Enumerable.Range(0, now.Length).Any(k => IsNew(was[k], now[k]) && now[k].Owner == i))
                seen.Fizzles++;
        }
        if (full) seen.FullFrames++;
        seen.MostShots = Math.Max(seen.MostShots, now.Skip(boxSlots).Count(p => p.Kind != PropKind.None));
        for (int k = 0; k < now.Length; k++)
        {
            ref Prop p = ref now[k];
            if (p.Kind != PropKind.Shot) continue;
            seen.RowFrames[(int)p.Weapon]++;
            if (p.Weapon == WeaponId.ThrownCharpai && p.Shot == ShotKind.Pad) seen.CharpaiPads++;
            if (!IsNew(was[k], p)) continue;
            if (WeaponTable.Placed(p.Shot) && p.Owner >= 0 && PlacedOf(was, p.Owner) >= limit) seen.AtPlacedLimit++;
            if (k >= boxSlots && full && was[k].Kind == PropKind.Shot && p.Age == 1)
            {
                if (was[k].Shot == ShotKind.Bullet) seen.OverBullet++;
                else if (WeaponTable.Placed(was[k].Shot) && was[k].Owner == p.Owner) seen.OverOwnPlaced++;
            }
        }
    }

    /// <summary>A shot that wasn't in this slot a frame ago: put there this frame (and moved once), or left behind by a burst.</summary>
    private static bool IsNew(in Prop was, in Prop now) =>
        now.Kind == PropKind.Shot && now.Age <= 1 && (was.Kind != PropKind.Shot || was.Age >= now.Age || was.Shot != now.Shot || was.Owner != now.Owner);

    private static int WeaponsSlots(Scenario s) => Weapons.PropSlots(s.Players) - Weapons.BoxSlots(s.Players);

    private static int PlacedOf(Prop[] props, int owner) => props.Count(p => p.Kind == PropKind.Shot && p.Owner == owner && WeaponTable.Placed(p.Shot));

    /// <summary>
    /// The traces really play what they are there for, checked while they are written and every
    /// time they are compared: no stock trace ends before its last frame; the all-weapons random
    /// trace has every built row's move out or its props on the stage (the Gauntlet's three rows,
    /// the Thunder wave and the three thrown-as rows included), blinks ahead and upwards, and a
    /// thrown Charpai that became a pad (disarms are counted but too rare in random play to ask
    /// for: client/tests/test_weapon_rules.gd tests one); the big-room trace places props at the
    /// limit of 2, puts shots into a full table over a bullet and over the firer's own placed prop,
    /// and has shots that fizzle.
    /// </summary>
    private void CheckCoverage(Scenario s, Coverage c, List<string> failures)
    {
        string unplayed = string.Join(", ", Enumerable.Range(1, WeaponTable.Count - 1)
            .Where(i => WeaponTable.Rows[i].Built && c.RowFrames[i] == 0).Select(i => $"{i} {(WeaponId)i}"));
        output.WriteLine($"{s.Name}: unplayed [{unplayed}], blinks {c.Blinks} (up {c.UpBlinks}), charpai pads {c.CharpaiPads}, disarms {c.Disarms}, " +
            $"at the placed limit {c.AtPlacedLimit}, over a bullet {c.OverBullet}, over own placed {c.OverOwnPlaced}, fizzles {c.Fizzles}, most shot slots in use {c.MostShots} of {WeaponsSlots(s)} (full on {c.FullFrames} frames), over early {c.MatchOverEarly}");
        void Need(bool ok, string what)
        {
            if (!ok) failures.Add($"{s.Name}: {what}");
        }
        if (s.TimeLimit <= 0 && s.Stocks > 3) Need(!c.MatchOverEarly, "ended before its last frame");
        if (s.Name == "all_weapons_random_4p")
        {
            Need(unplayed == "", $"rows never played: {unplayed}");
            Need(c.Blinks > c.UpBlinks && c.UpBlinks > 0, $"blinks {c.Blinks}, upward {c.UpBlinks}");
            Need(c.CharpaiPads > 0, "no thrown Charpai became a pad");
        }
        if (s.Name == "big_room_props_15p")
        {
            Need(c.AtPlacedLimit > 0, "nobody placed a prop at the big room's limit of 2");
            Need(c.OverBullet > 0, "no shot went into a full table over a bullet");
            Need(c.OverOwnPlaced > 0, "no shot went into a full table over its firer's own placed prop");
            Need(c.Fizzles > 0, "no shot fizzled");
        }
    }


    /// <summary>Button mashing that changes often, like DeterminismTests.</summary>
    private static Buttons RandomInput(ulong seed, int frame, int player)
    {
        var rng = new SimRandom(seed ^ ((ulong)frame * 0x9E3779B97F4A7C15UL) ^ ((ulong)player << 56));
        return (Buttons)(rng.NextUInt() & 0x7FF);
    }

    [Fact]
    public void GoldenTracesMatchTheSim()
    {
        string dir = Path.Combine(RepoRoot(), "client", "tests", "golden");
        bool write = Environment.GetEnvironmentVariable("RANGBAAZI_WRITE_GOLDENS") == "1";
        if (write) Directory.CreateDirectory(dir);
        var failures = new List<string>();
        foreach (Scenario s in Scenarios)
        {
            string path = Path.Combine(dir, s.Name + ".json");
            string trace = Trace(s, out Coverage seen);
            CheckCoverage(s, seen, failures);
            if (write)
            {
                File.WriteAllText(path, trace);
                continue;
            }
            Assert.True(File.Exists(path), $"{path} is missing; run the tests with RANGBAAZI_WRITE_GOLDENS=1");
            Assert.True(File.ReadAllText(path).Replace("\r\n", "\n") == trace,
                $"{s.Name}.json no longer matches the C# sim; if the change is intended, rewrite the goldens (RANGBAAZI_WRITE_GOLDENS=1) and port it to client/sim");
        }
        Assert.True(failures.Count == 0, "a trace no longer plays what it is for:\n" + string.Join("\n", failures));
    }

    /// <summary>
    /// A hash of every arena stage, in the order StageDefinition.add_to hashes one in GDScript:
    /// each platform's min x, min y, max x, max y, then the spawn points, then the blast zone; then
    /// the same for every big level (StageScaling.BigAll), keyed "&lt;id&gt;@big".
    /// </summary>
    private static string ArenaStageHashes()
    {
        var json = new StringBuilder("{\n");
        var stages = ArenaStages.All.Concat(StageScaling.BigAll).ToList();
        for (int i = 0; i < stages.Count; i++)
        {
            StageDefinition s = stages[i];
            var h = new StateHasher();
            foreach (var p in s.Platforms)
            {
                h.Add(p.MinX.Raw);
                h.Add(p.MinY.Raw);
                h.Add(p.MaxX.Raw);
                h.Add(p.MaxY.Raw);
            }
            foreach (var sp in s.SpawnPoints)
            {
                h.Add(sp.X.Raw);
                h.Add(sp.Y.Raw);
            }
            h.Add(s.BlastZone.MinX.Raw);
            h.Add(s.BlastZone.MinY.Raw);
            h.Add(s.BlastZone.MaxX.Raw);
            h.Add(s.BlastZone.MaxY.Raw);
            foreach (Hazard z in s.Hazards)
            {
                h.Add((int)z.Kind);
                h.Add(z.Zone.MinX.Raw);
                h.Add(z.Zone.MinY.Raw);
                h.Add(z.Zone.MaxX.Raw);
                h.Add(z.Zone.MaxY.Raw);
                h.Add(z.Direction);
            }
            string key = s.Size == StageSize.Big ? s.Id + "@big" : s.Id;
            json.Append($"  \"{key}\": \"{h.Value:X16}\"{(i + 1 < stages.Count ? "," : "")}\n");
        }
        return json.Append("}\n").ToString();
    }

    [Fact]
    public void ArenaStageGoldenMatchesTheSim()
    {
        string path = Path.Combine(RepoRoot(), "client", "tests", "golden", "arena_stages.json");
        string hashes = ArenaStageHashes();
        if (Environment.GetEnvironmentVariable("RANGBAAZI_WRITE_GOLDENS") == "1")
        {
            File.WriteAllText(path, hashes);
            return;
        }
        Assert.True(File.Exists(path) && File.ReadAllText(path).Replace("\r\n", "\n") == hashes,
            "arena_stages.json doesn't match ArenaStages; rerun tools/gen_arena_stages.py and rewrite the goldens");
    }

    /// <summary>
    /// A hash of every row of the weapon table over its columns in order, and one of the shared
    /// spread list: what client/tests/test_weapon_table.gd computes from the GDScript table. A
    /// generated file that is stale in either sim fails its own side of this golden.
    /// </summary>
    private static string WeaponTableHashes()
    {
        var json = new StringBuilder("{\n");
        json.Append($"  \"columns\": {WeaponTable.ColumnCount},\n  \"rows\": [\n");
        for (int i = 0; i < WeaponTable.Rows.Length; i++)
        {
            var h = new StateHasher();
            foreach (long v in WeaponTable.Rows[i].Columns) h.Add(v);
            json.Append($"    \"{h.Value:X16}\"{(i + 1 < WeaponTable.Rows.Length ? "," : "")}\n");
        }
        var spreads = new StateHasher();
        foreach (Fix v in WeaponTable.Spreads) spreads.Add(v.Raw);
        json.Append($"  ],\n  \"spreads\": \"{spreads.Value:X16}\",\n");
        json.Append($"  \"crate_list\": [{string.Join(", ", Weapons.CrateList.Select(w => ((int)w).ToString()))}]\n");
        return json.Append("}\n").ToString();
    }

    [Fact]
    public void WeaponTableGoldenMatchesTheSim()
    {
        string path = Path.Combine(RepoRoot(), "client", "tests", "golden", "weapon_table.json");
        string hashes = WeaponTableHashes();
        if (Environment.GetEnvironmentVariable("RANGBAAZI_WRITE_GOLDENS") == "1")
        {
            File.WriteAllText(path, hashes);
            return;
        }
        Assert.True(File.Exists(path) && File.ReadAllText(path).Replace("\r\n", "\n") == hashes,
            "weapon_table.json doesn't match WeaponTable; rerun tools/gen_weapons.py and rewrite the goldens");
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Rangbaazi.sln"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Rangbaazi.sln not found above the test directory.");
    }
}
