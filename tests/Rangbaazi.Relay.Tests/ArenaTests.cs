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

    [Fact]
    public async Task A_room_holds_fifteen_and_refuses_the_sixteenth()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        RelayClient owner = await Connect(host, "FULL", true, "Owner");
        await NextJson(owner, "lobby");
        await Send(owner, "{\"type\":\"bots\",\"count\":14}");
        JsonElement lobby = await NextJson(owner, "lobby");
        Assert.Equal(15, lobby.GetProperty("players").GetArrayLength());

        RelayClient late = await Connect(host, "FULL", false, "Late");
        Assert.Equal("full", (await ExpectRejection(late, RelayCloseCodes.RoomFull)).Code);
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

        // Hold right: the guest's fighter (index 1, which starts on the right) runs right.
        for (int i = 0; i < 40; i++)
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

    [Fact]
    public void State_frames_carry_weapons_and_props()
    {
        MatchRules rules = MatchRules.Room(Stages.Rooftops, 10);
        GameState state = Simulation.CreateInitialState(rules, 2, 1);
        state.Fighters[1].Weapon = WeaponId.Bow;
        state.Fighters[1].WeaponSupply = 5;
        state.Props[4] = new Prop { Kind = PropKind.Shot, Shot = ShotKind.Arrow, Weapon = WeaponId.Bow, Position = new FixVec2(Fix.FromInt(3), Fix.One), Velocity = new FixVec2(-Fix.Half, Fix.Zero) };
        state.Props[9] = new Prop { Kind = PropKind.PendingBox, Weapon = WeaponId.Uzi }; // not on the stage yet: not sent
        byte[] bytes = ArenaBinary.EncodeState(state, rules);
        int o = ArenaBinary.HeaderBytes + ArenaBinary.FighterBytes;
        Assert.Equal((byte)WeaponId.Bow, bytes[o + 26]);
        Assert.Equal(5, bytes[o + 27]);
        o = ArenaBinary.HeaderBytes + 2 * ArenaBinary.FighterBytes;
        Assert.Equal(1, bytes[o]);
        Assert.Equal(o + 1 + ArenaBinary.PropBytes, bytes.Length);
        Assert.Equal((byte)PropKind.Shot, bytes[o + 1]);
        Assert.Equal((byte)ShotKind.Arrow, bytes[o + 3]);
        Assert.Equal(Fix.FromInt(3).Raw, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(o + 4)));
        Assert.Equal(-1, (sbyte)bytes[o + 12]);
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
