using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Rangbaazi.Relay.Arena;
using Rangbaazi.Sim;
using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Relay.Tests;

/// <summary>Arena rooms end to end: real app on a loopback port, real WebSocket clients.</summary>
public sealed class ArenaTests
{
    private static async Task<RelayClient> Connect(RelayHost host, string code, bool asHost, string name, int minutes = 10)
    {
        var client = new RelayClient();
        using var timeout = new CancellationTokenSource(RelayClient.DefaultTimeout);
        var uri = new Uri(host.WsBase, $"relay/arena/{code}?host={(asHost ? 1 : 0)}&minutes={minutes}&name={Uri.EscapeDataString(name)}&v={ArenaLimits.ProtocolVersion}");
        await client.Socket.ConnectAsync(uri, timeout.Token);
        return client;
    }

    /// <summary>Skips STATE frames and other messages until a TEXT message of this type arrives.</summary>
    private static async Task<JsonElement> NextJson(RelayClient client, string type)
    {
        for (int i = 0; i < 2000; i++)
        {
            Received r = await client.ReceiveAsync();
            Assert.NotEqual(WebSocketMessageType.Close, r.Type);
            if (r.Type != WebSocketMessageType.Text) continue;
            JsonElement json = JsonDocument.Parse(r.Data).RootElement.Clone();
            if (json.GetProperty("type").GetString() == type) return json;
        }
        throw new InvalidOperationException("no " + type);
    }

    private static async Task<byte[]> NextState(RelayClient client)
    {
        for (int i = 0; i < 2000; i++)
        {
            Received r = await client.ReceiveAsync();
            Assert.NotEqual(WebSocketMessageType.Close, r.Type);
            if (r.Type == WebSocketMessageType.Binary && r.Data[0] == ArenaBinary.State) return r.Data;
        }
        throw new InvalidOperationException("no state");
    }

    private static Task Send(RelayClient client, string json) =>
        client.Socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    private static Task SendInput(RelayClient client, Buttons buttons) =>
        client.Socket.SendAsync(ArenaBinary.EncodeInput(buttons), WebSocketMessageType.Binary, true, CancellationToken.None);

    private static int FighterCount(byte[] state) => state[11];

    private static int Frame(byte[] state) => (int)BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan(1));

    private static int X(byte[] state, int fighter) =>
        BinaryPrimitives.ReadInt32LittleEndian(state.AsSpan(ArenaBinary.HeaderBytes + fighter * ArenaBinary.FighterBytes));

    private static async Task<(string Code, string Message)> ExpectRejection(RelayClient client, WebSocketCloseStatus status)
    {
        JsonElement error = await client.ReceiveJsonAsync("error");
        Assert.Equal(status, await client.ReceiveCloseAsync());
        return (error.GetProperty("code").GetString()!, error.GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task Host_and_guest_see_the_same_lobby()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        RelayClient a = await Connect(host, "ARENA1", true, "Ravi");
        JsonElement lobbyA = await NextJson(a, "lobby");
        Assert.Equal("ARENA1", lobbyA.GetProperty("room").GetString());
        Assert.Equal(lobbyA.GetProperty("you").GetInt32(), lobbyA.GetProperty("host").GetInt32());
        Assert.Equal(15, lobbyA.GetProperty("max").GetInt32());

        RelayClient b = await Connect(host, "arena1", false, "Simran<script>");
        JsonElement lobbyB = await NextJson(b, "lobby");
        JsonElement players = lobbyB.GetProperty("players");
        Assert.Equal(2, players.GetArrayLength());
        Assert.Equal("Ravi", players[0].GetProperty("name").GetString());
        Assert.Equal("Simranscript", players[1].GetProperty("name").GetString());
        Assert.NotEqual(lobbyB.GetProperty("you").GetInt32(), lobbyB.GetProperty("host").GetInt32());

        JsonElement again = await NextJson(a, "lobby");
        Assert.Equal(2, again.GetProperty("players").GetArrayLength());
    }

    [Fact]
    public async Task Joining_a_missing_room_or_hosting_a_taken_code_is_refused()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        RelayClient nobody = await Connect(host, "NOPE", false, "X");
        Assert.Equal("no_room", (await ExpectRejection(nobody, ArenaCloseCodes.NoRoom)).Code);

        RelayClient first = await Connect(host, "TAKEN", true, "A");
        await NextJson(first, "lobby");
        RelayClient second = await Connect(host, "TAKEN", true, "B");
        Assert.Equal("taken", (await ExpectRejection(second, ArenaCloseCodes.RoomTaken)).Code);
    }

    private static int Cpus(JsonElement lobby) =>
        lobby.GetProperty("players").EnumerateArray().Count(p => p.GetProperty("cpu").GetBoolean());

    /// <summary>A friend opening the host's link always gets in while a CPU holds a place (final
    /// round): each joiner takes the last-joined CPU's place, and only fifteen people fill a room.</summary>
    [Fact]
    public async Task Friends_take_the_cpus_places_and_fifteen_people_refuse_the_sixteenth()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        RelayClient owner = await Connect(host, "FULL", true, "Owner");
        await NextJson(owner, "lobby");
        await Send(owner, "{\"type\":\"bots\",\"count\":14}");
        JsonElement lobby = await NextJson(owner, "lobby");
        Assert.Equal(15, lobby.GetProperty("players").GetArrayLength());

        var people = new List<RelayClient> { owner };
        for (int i = 1; i < 15; i++)
        {
            RelayClient friend = await Connect(host, "FULL", false, "Friend" + i);
            JsonElement seen = await NextJson(friend, "lobby");
            Assert.Equal(15, seen.GetProperty("players").GetArrayLength());
            Assert.Equal(14 - i, Cpus(seen));
            people.Add(friend);
        }

        RelayClient late = await Connect(host, "FULL", false, "Late");
        Assert.Equal("full", (await ExpectRejection(late, RelayCloseCodes.RoomFull)).Code);

        // One friend leaves the lobby: a CPU takes the place back (the slider's 14 is "up to").
        await people[^1].Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        for (int n = 0; n < 60; n++)
        {
            lobby = await NextJson(owner, "lobby");
            if (Cpus(lobby) == 1) break;
        }
        Assert.Equal(1, Cpus(lobby));
        Assert.Equal(15, lobby.GetProperty("players").GetArrayLength());
    }

    [Fact]
    public async Task Only_the_host_changes_settings_and_minutes_stay_between_10_and_15()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        RelayClient owner = await Connect(host, "SETS", true, "Owner", minutes: 3);
        Assert.Equal(10, (await NextJson(owner, "lobby")).GetProperty("minutes").GetInt32());
        RelayClient guest = await Connect(host, "SETS", false, "Guest");
        await NextJson(guest, "lobby");

        await Send(guest, "{\"type\":\"settings\",\"minutes\":12}");
        Assert.Equal("not_allowed", (await NextJson(guest, "error")).GetProperty("code").GetString());

        await Send(owner, "{\"type\":\"settings\",\"minutes\":40}");
        Assert.Equal(15, (await NextJson(guest, "lobby")).GetProperty("minutes").GetInt32());
    }

    [Fact]
    public async Task The_host_role_passes_on_when_the_host_leaves()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        RelayClient owner = await Connect(host, "PASS", true, "Owner");
        await NextJson(owner, "lobby");
        RelayClient guest = await Connect(host, "PASS", false, "Guest");
        int guestId = (await NextJson(guest, "lobby")).GetProperty("you").GetInt32();

        await owner.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        JsonElement lobby = await NextJson(guest, "lobby");
        Assert.Equal(guestId, lobby.GetProperty("host").GetInt32());
        Assert.Equal(1, lobby.GetProperty("players").GetArrayLength());
    }

    [Fact]
    public async Task The_server_runs_the_match_and_inputs_move_your_fighter()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        RelayClient owner = await Connect(host, "PLAY", true, "Owner");
        await NextJson(owner, "lobby");
        RelayClient guest = await Connect(host, "PLAY", false, "Guest");
        await NextJson(guest, "lobby");
        await Send(owner, "{\"type\":\"bots\",\"count\":2}");
        await NextJson(guest, "lobby");

        await Send(owner, "{\"type\":\"start\"}");
        JsonElement start = await NextJson(guest, "start");
        Assert.Equal("rooftops", start.GetProperty("stage").GetString());
        Assert.Equal(4, start.GetProperty("fighters").GetArrayLength());
        Assert.Equal(1, start.GetProperty("you").GetInt32());
        Assert.Equal(10 * 60 * 60, start.GetProperty("time_limit_frames").GetInt32());
        Assert.True(start.GetProperty("fighters")[2].GetProperty("cpu").GetBoolean());

        byte[] first = await NextState(guest);
        Assert.Equal(4, FighterCount(first));
        int x0 = X(first, 1);

        // Hold right: the guest's fighter (index 1, which starts on the right) runs right. Kept short,
        // so it is checked before the first weapon crate drops (1.5 s in) and a CPU can shoot it back.
        for (int i = 0; i < 12; i++)
        {
            await SendInput(guest, Buttons.Right);
            await Task.Delay(5);
        }
        byte[] later = await NextState(guest);
        Assert.True(Frame(later) > Frame(first));
        Assert.True(X(later, 1) > x0, $"x {X(later, 1)} should be > {x0}");

        // The host sends the room back to the lobby.
        await Send(owner, "{\"type\":\"end\"}");
        Assert.Equal("lobby", (await NextJson(guest, "lobby")).GetProperty("phase").GetString());
    }

    [Fact]
    public async Task Quick_chat_reaches_everyone_and_a_bad_phrase_closes_the_connection()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        RelayClient owner = await Connect(host, "CHAT", true, "Owner");
        await NextJson(owner, "lobby");
        RelayClient guest = await Connect(host, "CHAT", false, "Guest");
        await NextJson(guest, "lobby");

        await Send(guest, "{\"type\":\"chat\",\"phrase\":3}");
        JsonElement chat = await NextJson(owner, "chat");
        Assert.Equal(3, chat.GetProperty("phrase").GetInt32());
        Assert.Equal("Guest", chat.GetProperty("name").GetString());

        await Send(guest, "{\"type\":\"chat\",\"phrase\":99}");
        for (int i = 0; i < 20; i++)
        {
            Received r = await guest.ReceiveAsync();
            if (r.Type == WebSocketMessageType.Close)
            {
                Assert.Equal(WebSocketCloseStatus.InvalidPayloadData, r.CloseStatus);
                return;
            }
        }
        Assert.Fail("expected the connection to close");
    }

    [Fact]
    public async Task The_host_picks_the_arena_and_everyone_picks_a_fighter()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        var client = new RelayClient();
        await client.Socket.ConnectAsync(new Uri(host.WsBase, $"relay/arena/PICK?host=1&name=Owner&character=simran&v={ArenaLimits.ProtocolVersion}"), CancellationToken.None);
        JsonElement lobby = await NextJson(client, "lobby");
        Assert.Equal("rooftops", lobby.GetProperty("stage").GetString());
        Assert.Equal("simran", lobby.GetProperty("players")[0].GetProperty("character").GetString());

        await Send(client, "{\"type\":\"stage\",\"id\":\"akshat\"}");
        Assert.Equal("akshat", (await NextJson(client, "lobby")).GetProperty("stage").GetString());
        await Send(client, "{\"type\":\"character\",\"id\":\"nobody\"}");
        Assert.Equal("shahn", (await NextJson(client, "lobby")).GetProperty("players")[0].GetProperty("character").GetString());
        await Send(client, "{\"type\":\"bots\",\"count\":2}");
        JsonElement withBots = await NextJson(client, "lobby");
        string bot1 = withBots.GetProperty("players")[1].GetProperty("character").GetString()!;
        string bot2 = withBots.GetProperty("players")[2].GetProperty("character").GetString()!;
        Assert.NotEqual(bot1, bot2);

        await Send(client, "{\"type\":\"start\"}");
        JsonElement start = await NextJson(client, "start");
        Assert.Equal("akshat", start.GetProperty("stage").GetString());
        Assert.Equal("shahn", start.GetProperty("fighters")[0].GetProperty("character").GetString());

        await Send(client, "{\"type\":\"end\"}");
        await NextJson(client, "lobby");
        await Send(client, "{\"type\":\"stage\",\"id\":\"../etc\"}");
        for (int i = 0; i < 50; i++)
        {
            Received r = await client.ReceiveAsync();
            if (r.Type == WebSocketMessageType.Close)
            {
                Assert.Equal(WebSocketCloseStatus.InvalidPayloadData, r.CloseStatus);
                return;
            }
        }
        Assert.Fail("an unknown arena should close the connection");
    }

    [Fact]
    public void State_frames_carry_every_fighter_and_hide_the_second_wind()
    {
        MatchRules rules = MatchRules.Room(Stages.Rooftops, 10);
        GameState state = Simulation.CreateInitialState(rules, 15, 1);
        state.Fighters[3].Shield = 20;
        state.Fighters[3].Kos = 7;
        byte[] bytes = ArenaBinary.EncodeState(state, rules);
        Assert.Equal(ArenaBinary.HeaderBytes + 15 * ArenaBinary.FighterBytes + 1, bytes.Length); // no props yet
        Assert.Equal(15, FighterCount(bytes));
        int o = ArenaBinary.HeaderBytes + 3 * ArenaBinary.FighterBytes;
        Assert.Equal(7, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(o + 16)));

        state.Fighters[3].Shield = 0;
        Assert.Equal(bytes, ArenaBinary.EncodeState(state, rules)); // the shield isn't in the frame
    }

    /// <summary>One slot of a fighter's match record, added to their total.</summary>
    private static void Record(GameState state, int fighter, int slot, WeaponId weapon, int damage, int kos, int sweets)
    {
        int from = fighter * Weapons.RecordInts, at = from + slot * Weapons.RecordSlotInts;
        state.WeaponStats[at] = (int)weapon + 1;
        state.WeaponStats[at + 1] = damage;
        state.WeaponStats[at + 2] = kos;
        state.WeaponStats[at + 3] = sweets;
        state.WeaponStats[from + Weapons.RecordSlots * Weapons.RecordSlotInts] += damage;
    }

    /// <summary>
    /// The state behind <see cref="SharedFrame"/>: two fighters and five props, with a value in
    /// every field protocol 5 added. client/tests/test_protocol.gd decodes the same bytes.
    /// </summary>
    private static GameState SharedState(MatchRules rules)
    {
        GameState state = Simulation.CreateInitialState(rules, 2, 1);
        state.Frame = 1234;
        ref Fighter a = ref state.Fighters[0];
        a.Position = new FixVec2(Fix.FromRatio(3, 2), Fix.Zero);
        a.Facing = 1;
        a.Grounded = true;
        Record(state, 0, 0, WeaponId.Bow, 40, 1, 3); // a bow master
        ref Fighter b = ref state.Fighters[1];
        b.Position = new FixVec2(Fix.FromRatio(-13, 4), Fix.FromInt(2));
        b.Facing = -1;
        b.Action = FighterAction.Stunned;
        b.HitstunFrames = 20;
        b.Josh = 87;
        b.Weapon = WeaponId.Katana;       // an id above 15, which used to mean "all-rounder"
        b.WeaponSupply = 7;
        b.LastHitWeapon = WeaponId.FreezeRay;
        b.LastHitSweet = true;
        b.HitCount = 3;
        b.SlowFrames = 10;
        b.DotFrames = 5;
        b.DotBy = 1;
        b.DotWeapon = (int)WeaponId.VenomDartLauncher;
        b.MarkFrames = 9;
        b.StunWeapon = (int)WeaponId.FreezeRay;
        state.Props[0] = new Prop { Kind = PropKind.Box, Weapon = WeaponId.Matka, Supply = 3, Owner = -1, Position = new FixVec2(Fix.FromInt(2), Fix.Zero), Grounded = true, Timer = 300, IgnoreFighter = 2, IgnoreTimer = 40 };
        state.Props[8] = new Prop { Kind = PropKind.Shot, Shot = ShotKind.Zone, Weapon = WeaponId.Lota, Owner = 1, Position = new FixVec2(Fix.FromInt(-4), Fix.Zero), Grounded = true, Timer = 300 };
        state.Props[9] = new Prop { Kind = PropKind.Shot, Shot = ShotKind.Arrow, Weapon = WeaponId.Bow, Owner = 0, Position = new FixVec2(Fix.FromInt(3), Fix.One), Velocity = new FixVec2(-Fix.Half, Fix.Zero) };
        state.Props[10] = new Prop { Kind = PropKind.Shot, Shot = ShotKind.Speaker, Weapon = WeaponId.SonicSpeaker, Owner = 1, Supply = -1, Position = new FixVec2(Fix.FromInt(6), Fix.Zero), Grounded = true, Timer = 600, IgnoreFighter = 1 };
        state.Props[11] = new Prop { Kind = PropKind.PendingBox, Weapon = WeaponId.Uzi }; // not on the stage yet: not sent
        state.Props[12] = new Prop { Kind = PropKind.Shot, Shot = ShotKind.Wall, Weapon = WeaponId.Matka, Owner = 0, Position = new FixVec2(Fix.FromInt(-8), Fix.Zero), Grounded = true, Timer = 240, IgnoreFighter = 2 };
        return state;
    }

    /// <summary>The STATE frame of <see cref="SharedState"/>, as hex. The same string is in client/tests/test_protocol.gd.</summary>
    private const string SharedFrame = "02D2040000CE87000000FF020080010000000000010000010000000000000000000000000000000000008700000000C0FCFF00000200FF04000000005700000000000000000014003407CE037F0D494E050120000000020000000000002C01FF0300041B0C0000FCFF00000000002C010100000407040000030000000100FF0000000000045E0E000006000000000000580201FF0104200D0000F8FF0000000000F000000002";

    [Fact]
    public void State_frames_have_the_protocol_5_layout()
    {
        Assert.Equal(5, ArenaLimits.ProtocolVersion);
        Assert.Equal((34, 17), (ArenaBinary.FighterBytes, ArenaBinary.PropBytes));
        MatchRules rules = MatchRules.Room(Stages.Rooftops, 10);
        byte[] bytes = ArenaBinary.EncodeState(SharedState(rules), rules);
        Assert.Equal(ArenaBinary.HeaderBytes + 2 * ArenaBinary.FighterBytes + 1 + 5 * ArenaBinary.PropBytes, bytes.Length);
        string hex = Convert.ToHexString(bytes);
        Assert.True(SharedFrame == hex, "the frame changed; if the layout is meant to, bump the protocol version and put this in SharedFrame and in client/tests/test_protocol.gd: " + hex);

        // Fighter 0: a bow master. Fighter 1: armed with a weapon above 15, no damage dealt.
        int o = ArenaBinary.HeaderBytes;
        Assert.Equal((byte)WeaponId.Bow | ArenaBinary.MasterBit, bytes[o + 30]);
        Assert.Equal((0, 0, 0), (bytes[o + 31], bytes[o + 32], bytes[o + 33]));
        o += ArenaBinary.FighterBytes;
        Assert.Equal((byte)WeaponId.Katana, bytes[o + 26]);
        Assert.Equal(7, bytes[o + 27]);
        Assert.Equal((byte)WeaponId.FreezeRay | 128, bytes[o + 28]); // the last hit: a sweet spot
        Assert.Equal(3, bytes[o + 29]);
        Assert.Equal(ArenaBinary.AllRounder, bytes[o + 30]);          // 127, not the old 15
        Assert.Equal(ArenaBinary.StatusSlowed | ArenaBinary.StatusDot | ArenaBinary.StatusMarked, bytes[o + 31]);
        Assert.Equal((byte)WeaponId.VenomDartLauncher, bytes[o + 32]);
        Assert.Equal((byte)WeaponId.FreezeRay, bytes[o + 33]);

        // The props in use, in slot order: a box, a zone, an arrow, a speaker, a wall. The pending box is not sent.
        o += ArenaBinary.FighterBytes;
        Assert.Equal(5, bytes[o]);
        o++;
        (PropKind Kind, WeaponId Weapon, ShotKind Shot, int X, int Facing, int Timer, int Owner, int Supply, int Wear)[] props =
        {
            (PropKind.Box, WeaponId.Matka, ShotKind.None, 2, 0, 300, 255, 3, 0),       // who dropped a box is not wear
            (PropKind.Shot, WeaponId.Lota, ShotKind.Zone, -4, 0, 300, 1, 0, 0),         // a timer past 255
            (PropKind.Shot, WeaponId.Bow, ShotKind.Arrow, 3, -1, 0, 0, 0, 0),
            (PropKind.Shot, WeaponId.SonicSpeaker, ShotKind.Speaker, 6, 0, 600, 1, -1, 1), // a facing as its supply
            (PropKind.Shot, WeaponId.Matka, ShotKind.Wall, -8, 0, 240, 0, 0, 2),        // two swings taken
        };
        foreach (var p in props)
        {
            Assert.Equal(((byte)p.Kind, (byte)p.Weapon, (byte)p.Shot), (bytes[o], bytes[o + 1], bytes[o + 2]));
            Assert.Equal(Fix.FromInt(p.X).Raw, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(o + 3)));
            Assert.Equal(p.Facing, (sbyte)bytes[o + 11]);
            Assert.Equal(p.Timer, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(o + 12)));
            Assert.Equal(p.Owner, bytes[o + 14]);
            Assert.Equal(p.Supply, (sbyte)bytes[o + 15]);
            Assert.Equal(p.Wear, bytes[o + 16]);
            o += ArenaBinary.PropBytes;
        }
        Assert.Equal(bytes.Length, o);
    }

    [Fact]
    public void Each_status_has_its_own_bit_and_a_main_weapon_is_its_id()
    {
        MatchRules rules = MatchRules.Room(Stages.Rooftops, 10);
        int status = ArenaBinary.HeaderBytes + 31;
        (Action<GameState> Set, int Bit)[] cases =
        {
            (s => s.Fighters[0].SlowFrames = 1, ArenaBinary.StatusSlowed),
            (s => s.Fighters[0].RootFrames = 400, ArenaBinary.StatusRooted),
            (s => s.Fighters[0].DotFrames = 30, ArenaBinary.StatusDot),
            (s => s.Fighters[0].MarkFrames = 300, ArenaBinary.StatusMarked),
            (s => s.Fighters[0].StunImmune = 150, ArenaBinary.StatusStunImmune),
            (s =>
            {
                // In a stink cloud: hidden (batch 4).
                s.Props[^1] = new Prop
                {
                    Kind = PropKind.Shot, Shot = ShotKind.Zone, Weapon = WeaponId.StinkCloudCapsule, Owner = 1,
                    Position = s.Fighters[0].Position, Timer = 300,
                };
            }, ArenaBinary.StatusHidden),
            (s =>
            {
                // A guard up: the Parat on its third frame (batch 4).
                ref Fighter f = ref s.Fighters[0];
                f.Weapon = WeaponId.Parat;
                f.WeaponSupply = 8;
                f.Action = FighterAction.Attack;
                f.Move = Weapons.Get(WeaponId.Parat).Move;
                f.ActionFrame = 3;
            }, ArenaBinary.StatusGuarding),
        };
        Assert.Equal(new[] { 1, 2, 4, 8, 16, 32, 64 }, cases.Select(c => c.Bit));
        int all = 0;
        GameState every = Simulation.CreateInitialState(rules, 2, 1);
        foreach (var c in cases)
        {
            GameState s = Simulation.CreateInitialState(rules, 2, 1);
            Assert.Equal(0, ArenaBinary.EncodeState(s, rules)[status]);
            c.Set(s);
            Assert.Equal(c.Bit, ArenaBinary.EncodeState(s, rules)[status]);
            Assert.Equal(0, ArenaBinary.EncodeState(s, rules)[status + ArenaBinary.FighterBytes]); // the other fighter's are its own
            c.Set(every);
            all |= c.Bit;
        }
        Assert.Equal(all, ArenaBinary.EncodeState(every, rules)[status]);

        // The main weapon: fists are 0, any weapon is its id (a hundred of them now), an all-rounder is 127.
        int main = ArenaBinary.HeaderBytes + 30;
        GameState m = Simulation.CreateInitialState(rules, 2, 1);
        Assert.Equal(127, ArenaBinary.EncodeState(m, rules)[main]);
        Record(m, 0, 0, WeaponId.None, 30, 0, 0);
        Assert.Equal(0, ArenaBinary.EncodeState(m, rules)[main]);
        Record(m, 0, 1, WeaponId.Plank, 60, 0, 0); // id 15: the number that used to mean "all-rounder"
        Assert.Equal(15, ArenaBinary.EncodeState(m, rules)[main]);
        Record(m, 0, 2, WeaponId.RubberChickenLauncher, 200, 2, 5);
        Assert.Equal(100 | 128, ArenaBinary.EncodeState(m, rules)[main]);
        Record(m, 0, 3, WeaponId.Bow, 200, 0, 0);
        Record(m, 0, 4, WeaponId.Uzi, 200, 0, 0); // now nothing is 40% of the total
        Assert.Equal(127, ArenaBinary.EncodeState(m, rules)[main]);
    }

    [Fact]
    public void A_full_room_frame_fits_the_size_the_spec_gives()
    {
        MatchRules rules = MatchRules.Room(Stages.Rooftops, 10);
        GameState state = Simulation.CreateInitialState(rules, 15, 1);
        Assert.Equal(64, state.Props.Length);
        for (int i = 0; i < state.Props.Length; i++)
            state.Props[i] = new Prop { Kind = PropKind.Shot, Shot = ShotKind.Mine, Weapon = WeaponId.Mines, Owner = i % 15, Timer = 900 };
        byte[] bytes = ArenaBinary.EncodeState(state, rules);
        Assert.Equal(12 + 15 * 34 + 1 + 64 * 17, bytes.Length);
        Assert.Equal(64, bytes[12 + 15 * 34]);
        Assert.True(bytes.Length <= 12 + 16 * 34 + 1 + 64 * 17); // 1,645: the largest frame a 16-fighter match could send
    }

    [Fact]
    public async Task An_old_client_is_told_to_update()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        var client = new RelayClient();
        await client.Socket.ConnectAsync(new Uri(host.WsBase, "relay/arena/OLDV?host=1&name=Old"), CancellationToken.None);
        Received r = await client.ReceiveAsync();
        Assert.Equal(WebSocketMessageType.Text, r.Type);
        Assert.Contains("out of date", System.Text.Encoding.UTF8.GetString(r.Data));
    }
}
