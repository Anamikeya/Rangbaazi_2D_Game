using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Rangbaazi.Relay.Arena;
using Rangbaazi.Sim;

namespace Rangbaazi.Relay.Tests;

/// <summary>
/// Teams in arena rooms (docs/design/rooms-teams-view-ui-v1.md, sections 1.1 to 1.5 and 6.3): the
/// lobby rules on <see cref="ArenaRoom"/> directly, and the requests, errors and messages end to end.
/// </summary>
public sealed class ArenaTeamTests : IDisposable
{
    private readonly ArenaServer _server = new(RelayHost.TestOptions, NullLogger<ArenaServer>.Instance);

    public void Dispose() => _server.Dispose();

    // ---- helpers: a room driven directly ----

    private ArenaPlayer Person(ArenaRoom room, string name) =>
        room.AddPerson(name, "shahn", new ArenaConnection(new FakeWebSocket(), _server));

    private ArenaRoom Room(int people, int bots = 0, int teams = 0)
    {
        var room = new ArenaRoom("TEAM", 10);
        for (int i = 0; i < people; i++) Person(room, "P" + i);
        if (teams > 0) room.SetTeams(teams);
        if (bots > 0) room.SetBots(bots);
        return room;
    }

    private static int[] Sizes(ArenaRoom room)
    {
        var sizes = new int[room.TeamCount];
        for (int t = 1; t <= room.TeamCount; t++) sizes[t - 1] = room.Size(t);
        return sizes;
    }

    private static List<ArenaPlayer> Bots(ArenaRoom room) => room.Members.Where(m => m.IsBot).ToList();

    private static JsonElement Json(byte[] message) => JsonDocument.Parse(message).RootElement.Clone();

    // ---- helpers: end to end ----

    private static async Task<RelayClient> Connect(RelayHost host, string code, bool asHost, string name, int version = ArenaLimits.ProtocolVersion)
    {
        var client = new RelayClient();
        using var timeout = new CancellationTokenSource(RelayClient.DefaultTimeout);
        var uri = new Uri(host.WsBase, $"relay/arena/{code}?host={(asHost ? 1 : 0)}&minutes=10&name={Uri.EscapeDataString(name)}&v={version}");
        await client.Socket.ConnectAsync(uri, timeout.Token);
        return client;
    }

    private static async Task<JsonElement> NextJson(RelayClient client, string type)
    {
        for (int i = 0; i < 4000; i++)
        {
            Received r = await client.ReceiveAsync();
            Assert.NotEqual(WebSocketMessageType.Close, r.Type);
            if (r.Type != WebSocketMessageType.Text) continue;
            JsonElement json = JsonDocument.Parse(r.Data).RootElement.Clone();
            if (json.GetProperty("type").GetString() == type) return json;
        }
        throw new InvalidOperationException("no " + type);
    }

    /// <summary>Lobby messages until one satisfies the condition (several may be on their way).</summary>
    private static async Task<JsonElement> LobbyWhere(RelayClient client, Func<JsonElement, bool> condition)
    {
        for (int i = 0; i < 50; i++)
        {
            JsonElement lobby = await NextJson(client, "lobby");
            if (condition(lobby)) return lobby;
        }
        throw new InvalidOperationException("no such lobby");
    }

    private static Task Send(RelayClient client, string json) =>
        client.Socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    private static int TeamOf(JsonElement lobby, int id)
    {
        foreach (JsonElement p in lobby.GetProperty("players").EnumerateArray())
            if (p.GetProperty("id").GetInt32() == id) return p.GetProperty("team").GetInt32();
        return -1;
    }

    // ---- the lobby rules ----

    [Fact]
    public void Joiners_and_cpus_fill_the_smallest_team_and_the_9th_and_13th_raise_the_count()
    {
        ArenaRoom room = Room(1, teams: 2);
        Assert.Equal(2, room.TeamCount);
        Assert.Equal(1, room.Members[0].Team);
        ArenaPlayer second = Person(room, "Second");
        Assert.Equal(2, second.Team); // the smaller team

        room.SetBots(6); // 8 members: 4 and 4
        Assert.Equal(new[] { 4, 4 }, Sizes(room));
        Assert.Equal(1, room.People(1));
        Assert.Equal(1, room.People(2));

        room.SetBots(7); // the 9th: a third team
        Assert.Equal(3, room.TeamCount);
        Assert.Equal(9, Sizes(room).Sum());
        Assert.True(Sizes(room).Max() - Sizes(room).Min() <= 1);

        room.SetBots(10); // 12: three of four
        Assert.Equal(new[] { 4, 4, 4 }, Sizes(room));
        Person(room, "Thirteenth");
        Assert.Equal(4, room.TeamCount);
        Assert.Equal(13, Sizes(room).Sum());
        room.SetBots(13); // the full 15
        Assert.Equal(15, room.Members.Count);
        Assert.True(Sizes(room).All(s => s is 3 or 4), string.Join(",", Sizes(room)));
        Assert.Equal(4, ArenaLimits.TeamsNeeded(15));
        Assert.Equal(2, ArenaLimits.TeamsNeeded(1));
        Assert.Equal(2, ArenaLimits.TeamsNeeded(8));
        Assert.Equal(3, ArenaLimits.TeamsNeeded(9));
        Assert.Equal(3, ArenaLimits.TeamsNeeded(12));
        Assert.Equal(4, ArenaLimits.TeamsNeeded(13));
    }

    [Fact]
    public void Team_count_never_drops_below_what_the_room_needs()
    {
        ArenaRoom room = Room(1, bots: 9, teams: 2); // 10 members after the CPUs
        Assert.Equal(3, room.TeamCount);
        room.SetTeams(2);
        Assert.Equal(3, room.TeamCount);
        room.SetTeams(1);
        Assert.Equal(3, room.TeamCount);
        room.SetTeams(9);
        Assert.Equal(4, room.TeamCount);
        Assert.All(room.Members, m => Assert.InRange(m.Team, 1, 4));

        room.SetTeams(0); // free-for-all: nobody has a team
        Assert.Equal(0, room.TeamCount);
        Assert.False(room.TeamsMode);
        Assert.All(room.Members, m => Assert.Equal(0, m.Team));
    }

    [Fact]
    public void Lowering_the_count_rehomes_members_people_first()
    {
        ArenaRoom room = Room(3, teams: 4);
        Assert.Equal(new[] { 1, 2, 3 }, room.Members.Select(m => m.Team).ToArray());
        room.SetBots(3); // team 4, team 4 (it has no people), team 1: 2/1/1/2
        Assert.Equal(new[] { 2, 1, 1, 2 }, Sizes(room));
        ArenaPlayer third = room.Members[2];
        Assert.Equal(3, third.Team);

        // Down to 2: the person on team 3 is placed before the two CPUs from team 4, so it gets the
        // smaller team (2, with one member), and the CPUs fill in after it.
        room.SetTeams(2);
        Assert.Equal(2, room.TeamCount);
        Assert.Equal(new[] { 3, 3 }, Sizes(room));
        Assert.Equal(1, room.Members[0].Team);
        Assert.Equal(2, room.Members[1].Team);
        Assert.Equal(2, third.Team);
        Assert.Equal(2, room.People(2));
        Assert.All(room.Members, m => Assert.InRange(m.Team, 1, 2));
    }

    [Fact]
    public void Bots_rebalance_but_people_never_move()
    {
        ArenaRoom room = Room(4, bots: 2, teams: 2); // people 1,2,1,2 then CPUs 1,2: 3 v 3
        ArenaPlayer[] people = room.Members.Where(m => !m.IsBot).ToArray();
        Assert.True(room.PickTeam(people[1], 1)); // 4 v 2: a CPU crosses over to even it
        Assert.Equal(new[] { 3, 3 }, Sizes(room));
        Assert.Equal(new[] { 1, 1, 1, 2 }, people.Select(p => p.Team).ToArray());

        Assert.True(room.PickTeam(people[3], 1)); // all four people on team 1
        Assert.Equal(new[] { 1, 1, 1, 1 }, people.Select(p => p.Team).ToArray());
        Assert.Equal(new[] { 4, 2 }, Sizes(room)); // uneven, but only people on team 1: nobody is moved
        Assert.All(Bots(room), b => Assert.Equal(2, b.Team));
    }

    [Fact]
    public void Picking_a_team_full_with_a_cpu_swaps_the_cpu_out()
    {
        ArenaRoom room = Room(1, bots: 7, teams: 2); // 4 v 4
        ArenaPlayer me = room.Members[0];
        Assert.Equal(1, me.Team);
        ArenaPlayer lastCpuOnTwo = room.Members.Last(m => m.IsBot && m.Team == 2);
        Assert.True(room.PickTeam(me, 2));
        Assert.Equal(2, me.Team);
        Assert.Equal(1, lastCpuOnTwo.Team);
        Assert.Equal(new[] { 4, 4 }, Sizes(room));
        Assert.False(room.PickTeam(me, 2)); // its own team
        Assert.False(room.PickTeam(me, 3)); // not a team in this room
    }

    [Fact]
    public void A_fifth_pick_of_a_team_of_people_is_refused()
    {
        ArenaRoom room = Room(5, teams: 2);
        ArenaPlayer[] p = room.Members.ToArray();
        foreach (ArenaPlayer x in p.Where(x => x.Team == 2).Take(1)) Assert.True(room.PickTeam(x, 1));
        Assert.Equal(4, room.People(1));
        ArenaPlayer last = p.Single(x => x.Team == 2);
        Assert.False(room.PickTeam(last, 1));
        Assert.Equal(2, last.Team);
    }

    [Fact]
    public void Start_needs_two_teams_in_use()
    {
        ArenaRoom room = Room(1, teams: 2);
        Assert.False(room.CanStart(out string reason));
        Assert.Equal("Put someone on a second team to start.", reason);
        room.SetBots(1);
        Assert.True(room.CanStart(out _));
        Assert.True(Room(1).CanStart(out _)); // a free-for-all is unchanged
    }

    [Fact]
    public void Start_message_and_rules_carry_each_fighters_team()
    {
        ArenaRoom room = Room(2, bots: 4, teams: 3);
        room.Start(1);
        Assert.True(room.Rules!.HasTeams());
        JsonElement start = Json(ArenaMessages.Start(room, room.Members[0]));
        Assert.Equal(3, start.GetProperty("teams").GetInt32());
        JsonElement[] fighters = start.GetProperty("fighters").EnumerateArray().ToArray();
        Assert.Equal(6, fighters.Length);
        for (int i = 0; i < room.Fighters.Count; i++)
        {
            Assert.Equal(room.Fighters[i].Team, room.Rules.TeamOf(i));
            Assert.Equal(room.Fighters[i].Team, fighters[i].GetProperty("team").GetInt32());
            Assert.InRange(room.Fighters[i].Team, 1, 3);
        }

        // A free-for-all sends teams 0 and no teams in the rules.
        ArenaRoom free = Room(2, bots: 2);
        free.Start(1);
        Assert.Null(free.Rules!.Teams);
        JsonElement freeStart = Json(ArenaMessages.Start(free, free.Members[0]));
        Assert.Equal(0, freeStart.GetProperty("teams").GetInt32());
        Assert.All(freeStart.GetProperty("fighters").EnumerateArray(), f => Assert.Equal(0, f.GetProperty("team").GetInt32()));
    }

    [Fact]
    public void Start_with_9_is_big_and_with_8_normal()
    {
        // Rooms v1, 2.2: the server picks the level once, at the start, from the number of fighters.
        ArenaRoom eight = Room(1, bots: 7);
        eight.Start(1);
        Assert.Equal(StageSize.Normal, eight.Rules!.Stage.Size);
        Assert.Same(Stages.Rooftops, eight.Rules.Stage);
        Assert.Equal("normal", Json(ArenaMessages.Start(eight, eight.Members[0])).GetProperty("size").GetString());

        ArenaRoom nine = Room(1, bots: 8, teams: 3);
        nine.Start(1);
        Assert.Equal(StageSize.Big, nine.Rules!.Stage.Size);
        Assert.Same(Stages.ById("rooftops", StageSize.Big), nine.Rules.Stage);
        Assert.Equal(15, nine.Rules.Stage.SpawnPoints.Count);
        JsonElement start = Json(ArenaMessages.Start(nine, nine.Members[0]));
        Assert.Equal("big", start.GetProperty("size").GetString());
        Assert.Equal("rooftops", start.GetProperty("stage").GetString());

        // A full free-for-all room is big too; back in the lobby and down to 8, the next match is normal.
        ArenaRoom full = Room(1, bots: 14);
        full.Start(2);
        Assert.Equal(StageSize.Big, full.Rules!.Stage.Size);
        Assert.False(full.Rules.HasTeams());
        full.ReturnToLobby();
        full.SetBots(7);
        full.Start(3);
        Assert.Equal(StageSize.Normal, full.Rules!.Stage.Size);
    }

    [Fact]
    public void A_mid_match_joiner_watches_and_gets_a_team_back_in_the_lobby()
    {
        ArenaRoom room = Room(1, bots: 3, teams: 2);
        room.Start(1);
        ArenaPlayer late = Person(room, "Late");
        Assert.Equal(0, late.Team);
        Assert.Equal(-1, late.Fighter);
        room.ReturnToLobby();
        Assert.InRange(late.Team, 1, 2);
        Assert.Equal(new[] { 3, 2 }.Order().ToArray(), Sizes(room).Order().ToArray());
    }

    [Fact]
    public void A_leaver_is_played_by_the_cpu_and_marked_left()
    {
        foreach (int teams in new[] { 0, 2 })
        {
            ArenaRoom room = Room(2, bots: 1, teams: teams);
            room.Start(1);
            ArenaPlayer leaver = room.Members[1];
            int fighter = leaver.Fighter;
            for (int f = 0; f < 60; f++) room.Tick(out _); // landed
            long landedX = room.State!.Fighters[fighter].Position.X.Raw;
            room.RemovePerson(leaver);
            Assert.True(leaver.Left);
            Assert.False(leaver.IsBot);
            JsonElement lobby = Json(ArenaMessages.Lobby(room, room.Members[0]));
            JsonElement row = lobby.GetProperty("players").EnumerateArray().Single(p => p.GetProperty("id").GetInt32() == leaver.Id);
            Assert.True(row.GetProperty("left").GetBoolean());

            // Nobody presses anything for the leaver, but its fighter keeps playing.
            bool moved = false;
            for (int f = 0; f < 240 && !moved; f++)
            {
                room.Tick(out _);
                Fighter me = room.State!.Fighters[fighter];
                moved = me.Position.X.Raw != landedX || me.Action == FighterAction.Attack;
            }
            Assert.True(moved, $"teams {teams}: the leaver's fighter should be played by the CPU");
            room.ReturnToLobby();
            Assert.DoesNotContain(leaver, room.Members);
        }
    }

    [Fact]
    public void Team_state_frame_winner_is_the_mvp()
    {
        var rules = new MatchRules { Stage = Stages.Rooftops, TimeLimitFrames = 3, Teams = new[] { 1, 2, 1, 2 } };
        GameState state = Simulation.CreateInitialState(rules, 4, 1);
        (int kos, int falls)[] scores = { (3, 4), (5, 0), (3, 1), (0, 2) };
        for (int i = 0; i < 4; i++) (state.Fighters[i].Kos, state.Fighters[i].Falls) = scores[i];
        var inputs = new Buttons[4];
        while (!state.MatchOver) Simulation.Advance(state, rules, inputs);
        byte[] frame = ArenaBinary.EncodeState(state, rules);
        Assert.Equal(2, (sbyte)frame[10]); // team 1 wins 6 to 5; its MVP is fighter 2
        Assert.Equal(1, Simulation.TimedWinner(state.Fighters)); // not the free-for-all winner
    }

    [Fact]
    public void A_test_relay_can_shorten_matches_and_keeps_the_teams()
    {
        Assert.Equal(0, RelayOptions.FromEnvironment(_ => null).ArenaMatchSeconds);
        Assert.Equal(40, RelayOptions.FromEnvironment(n => n == RelayOptions.ArenaMatchSecondsVariable ? "40" : null).ArenaMatchSeconds);
        ArenaRoom room = Room(1, bots: 3, teams: 2);
        room.MatchFrames = 40 * Simulation.TicksPerSecond;
        room.Start(1);
        Assert.Equal(2400, room.Rules!.TimeLimitFrames);
        Assert.True(room.Rules.HasTeams());
        Assert.True(room.Rules.Weapons && room.Rules.Hazards && room.Rules.Flurry);
        Assert.Equal(MatchRules.Room(Stages.Rooftops, 10).RagePercent, room.Rules.RagePercent);
    }

    // ---- CPUs never keep a person out; an empty room waits for its host (final round) ----

    [Fact]
    public void A_joiner_of_a_full_lobby_takes_the_last_cpus_place_and_the_cpu_comes_back()
    {
        foreach (int teams in new[] { 0, 2 })
        {
            ArenaRoom room = Room(1, bots: 14, teams: teams);
            Assert.Equal(15, room.Members.Count);
            Assert.False(room.IsFull);
            ArenaPlayer lastCpu = room.Members.Last(m => m.IsBot);
            ArenaPlayer friend = Person(room, "Friend");
            Assert.Equal(15, room.Members.Count);
            Assert.Equal(13, room.BotCount);
            Assert.DoesNotContain(lastCpu, room.Members);
            Assert.Contains(friend, room.Members);
            if (teams > 0)
            {
                Assert.Equal(4, room.TeamCount);
                Assert.InRange(friend.Team, 1, 4);
                Assert.True(Sizes(room).All(s => s is 3 or 4), string.Join(",", Sizes(room)));
            }

            room.RemovePerson(friend); // the slider's 14 is "up to": the CPU comes back
            Assert.Equal(15, room.Members.Count);
            Assert.Equal(14, room.BotCount);
            if (teams > 0) Assert.True(Sizes(room).All(s => s is 3 or 4), string.Join(",", Sizes(room)));
        }
    }

    [Fact]
    public void Fifteen_people_fill_a_room()
    {
        ArenaRoom room = Room(15);
        Assert.True(room.IsFull);
        room.SetBots(14);
        Assert.Equal(0, room.BotCount);
        Assert.Equal(15, room.Members.Count);
    }

    [Fact]
    public void A_joiner_of_a_full_room_mid_match_watches_then_takes_a_cpus_place()
    {
        ArenaRoom room = Room(1, bots: 14, teams: 4);
        room.Start(1);
        ArenaPlayer late = Person(room, "Late");
        Assert.Equal(-1, late.Fighter);
        Assert.Equal(0, late.Team);
        Assert.Equal(15, room.Fighters.Count);
        room.ReturnToLobby();
        Assert.Equal(15, room.Members.Count);
        Assert.Equal(13, room.BotCount);
        Assert.InRange(late.Team, 1, 4);
        Assert.True(Sizes(room).All(s => s is 3 or 4), string.Join(",", Sizes(room)));
    }

    private ArenaConnection Joined(string code, bool host)
    {
        var connection = new ArenaConnection(new FakeWebSocket(), _server);
        Assert.Equal(ArenaJoinOutcome.Joined, _server.Join(code, host, 10, "P", "shahn", connection));
        return connection;
    }

    [Fact]
    public void An_empty_room_waits_for_its_host_back_in_the_lobby_then_closes()
    {
        long grace = (long)RelayHost.TestOptions.EmptyRoomGrace.TotalMilliseconds;
        Assert.True(grace >= 120_000, "a couple of minutes at least, to share a link in another app");
        long t0 = Environment.TickCount64; // the server's own sweeper runs on this clock too
        ArenaConnection host = Joined("GRACE", true);
        ArenaRoom room = host.Room!;
        room.SetBots(5);
        room.SetTeams(2);
        room.Start(1);
        _server.Leave(host, t0);
        Assert.Equal(1, _server.RoomCount);
        Assert.Equal(ArenaPhase.Lobby, room.Phase); // the match stopped with nobody left
        Assert.Null(room.Host);

        // The host comes back, as a guest (Rejoin) or hosting the same code, and is its host again.
        _server.CloseEmptyRooms(t0 + grace - 1);
        Assert.Equal(1, _server.RoomCount);
        ArenaConnection back = Joined("GRACE", false);
        Assert.Same(room, back.Room);
        Assert.Same(back.Player, room.Host);
        Assert.Equal(5, room.BotCount);
        Assert.Equal(2, room.TeamCount);
        _server.CloseEmptyRooms(t0 + 10 * grace);
        Assert.Equal(1, _server.RoomCount); // not empty: kept

        _server.Leave(back, t0 + 50_000);
        ArenaConnection again = Joined("GRACE", true);
        Assert.Same(room, again.Room);
        _server.Leave(again, t0 + 60_000);
        _server.CloseEmptyRooms(t0 + 60_000 + grace - 1);
        Assert.Equal(1, _server.RoomCount);
        _server.CloseEmptyRooms(t0 + 60_000 + grace);
        Assert.Equal(0, _server.RoomCount);
        Assert.Equal(ArenaJoinOutcome.NoRoom, _server.Join("GRACE", false, 10, "P", "shahn", new ArenaConnection(new FakeWebSocket(), _server)));
    }

    [Fact]
    public void A_full_server_closes_the_room_empty_longest_to_let_a_new_host_in()
    {
        using var server = new ArenaServer(RelayHost.TestOptions with { MaxRooms = 2 }, NullLogger<ArenaServer>.Instance);
        ArenaJoinOutcome Join(string code, bool host, out ArenaConnection connection)
        {
            connection = new ArenaConnection(new FakeWebSocket(), server);
            return server.Join(code, host, 10, "P", "shahn", connection);
        }

        long t0 = Environment.TickCount64;
        Assert.Equal(ArenaJoinOutcome.Joined, Join("OLDER", true, out ArenaConnection older));
        Assert.Equal(ArenaJoinOutcome.Joined, Join("NEWER", true, out ArenaConnection newer));
        Assert.Equal(ArenaJoinOutcome.Busy, Join("THIRD", true, out _)); // both rooms in use: no way in

        server.Leave(older, t0);
        server.Leave(newer, t0 + 1_000);
        Assert.Equal(2, server.RoomCount); // both kept for their hosts

        // Rooms nobody is in can't keep a new host out: the one empty longest makes way.
        Assert.Equal(ArenaJoinOutcome.Joined, Join("THIRD", true, out _));
        Assert.Equal(2, server.RoomCount);
        Assert.Equal(ArenaJoinOutcome.NoRoom, Join("OLDER", false, out _));
        Assert.Equal(ArenaJoinOutcome.Joined, Join("NEWER", false, out _)); // still kept, and theirs again
    }

    // ---- end to end ----

    [Fact]
    public async Task Host_sets_teams_and_the_lobby_carries_the_count_and_each_team()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        RelayClient owner = await Connect(host, "TEAMS1", true, "Ravi");
        await NextJson(owner, "lobby");
        RelayClient guest = await Connect(host, "TEAMS1", false, "Neha");
        await NextJson(guest, "lobby");
        await Send(owner, "{\"type\":\"bots\",\"count\":2}");
        await Send(owner, "{\"type\":\"teams\",\"count\":2}");
        JsonElement lobby = await LobbyWhere(guest, l => l.GetProperty("teams").GetInt32() == 2);
        int[] teams = lobby.GetProperty("players").EnumerateArray().Select(p => p.GetProperty("team").GetInt32()).ToArray();
        Assert.Equal(4, teams.Length);
        Assert.Equal(2, teams.Count(t => t == 1));
        Assert.Equal(2, teams.Count(t => t == 2));
        Assert.All(lobby.GetProperty("players").EnumerateArray(), p => Assert.False(p.GetProperty("left").GetBoolean()));

        // The guest picks the other team: a CPU crosses back to keep it 2 v 2.
        int me = lobby.GetProperty("you").GetInt32();
        int other = TeamOf(lobby, me) == 1 ? 2 : 1;
        await Send(guest, $"{{\"type\":\"team\",\"team\":{other}}}");
        JsonElement picked = await LobbyWhere(guest, l => TeamOf(l, me) == other);
        Assert.Equal(2, picked.GetProperty("players").EnumerateArray().Count(p => p.GetProperty("team").GetInt32() == other));
    }

    [Fact]
    public async Task Free_for_all_lobby_sends_teams_0_and_team_0()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        RelayClient owner = await Connect(host, "FREE1", true, "Ravi");
        JsonElement lobby = await NextJson(owner, "lobby");
        Assert.Equal(0, lobby.GetProperty("teams").GetInt32());
        JsonElement me = lobby.GetProperty("players")[0];
        Assert.Equal(0, me.GetProperty("team").GetInt32());
        Assert.False(me.GetProperty("left").GetBoolean());
    }

    [Fact]
    public async Task Guests_cannot_set_teams()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        RelayClient owner = await Connect(host, "GUEST1", true, "Ravi");
        await NextJson(owner, "lobby");
        RelayClient guest = await Connect(host, "GUEST1", false, "Neha");
        await NextJson(guest, "lobby");
        await Send(guest, "{\"type\":\"teams\",\"count\":2}");
        Assert.Equal("not_allowed", (await NextJson(guest, "error")).GetProperty("code").GetString());
        await Send(guest, "{\"type\":\"team\",\"team\":1}"); // and no team to pick in a free-for-all
        Assert.Equal("not_allowed", (await NextJson(guest, "error")).GetProperty("code").GetString());
        await Send(owner, "{\"type\":\"bots\",\"count\":1}");
        Assert.Equal(0, (await NextJson(guest, "lobby")).GetProperty("teams").GetInt32());
    }

    [Fact]
    public async Task A_fifth_pick_of_a_team_of_people_is_refused_with_team_full_and_the_connection_stays()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        var clients = new List<RelayClient> { await Connect(host, "FIVE1", true, "P0") };
        await NextJson(clients[0], "lobby");
        for (int i = 1; i < 5; i++)
        {
            clients.Add(await Connect(host, "FIVE1", false, "P" + i));
            await NextJson(clients[i], "lobby");
        }
        await Send(clients[0], "{\"type\":\"teams\",\"count\":2}");
        JsonElement lobby = await LobbyWhere(clients[4], l => l.GetProperty("teams").GetInt32() == 2);
        // Placed 1, 2, 1, 2, 1: one from team 2 moves over, then the other is refused.
        int[] ids = lobby.GetProperty("players").EnumerateArray().Select(p => p.GetProperty("id").GetInt32()).ToArray();
        Assert.Equal(new[] { 1, 2, 1, 2, 1 }, ids.Select(id => TeamOf(lobby, id)).ToArray());
        await Send(clients[1], "{\"type\":\"team\",\"team\":1}");
        await LobbyWhere(clients[3], l => TeamOf(l, ids[1]) == 1);
        await Send(clients[3], "{\"type\":\"team\",\"team\":1}");
        JsonElement error = await NextJson(clients[3], "error");
        Assert.Equal("team_full", error.GetProperty("code").GetString());
        Assert.Equal("That team is full.", error.GetProperty("message").GetString());

        // Still in the room: it still gets the lobby.
        await Send(clients[0], "{\"type\":\"settings\",\"minutes\":12}");
        JsonElement after = await LobbyWhere(clients[3], l => l.GetProperty("minutes").GetInt32() == 12);
        Assert.Equal(2, TeamOf(after, ids[3]));
    }

    [Fact]
    public async Task Starting_with_one_team_in_use_is_refused_with_teams_needed()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        RelayClient owner = await Connect(host, "ALONE1", true, "Ravi");
        await NextJson(owner, "lobby");
        await Send(owner, "{\"type\":\"teams\",\"count\":2}");
        await LobbyWhere(owner, l => l.GetProperty("teams").GetInt32() == 2);
        await Send(owner, "{\"type\":\"start\"}");
        JsonElement error = await NextJson(owner, "error");
        Assert.Equal("teams_needed", error.GetProperty("code").GetString());

        await Send(owner, "{\"type\":\"bots\",\"count\":1}");
        await LobbyWhere(owner, l => l.GetProperty("players").GetArrayLength() == 2);
        await Send(owner, "{\"type\":\"start\"}");
        JsonElement start = await NextJson(owner, "start");
        Assert.Equal(2, start.GetProperty("teams").GetInt32());
        int[] teams = start.GetProperty("fighters").EnumerateArray().Select(f => f.GetProperty("team").GetInt32()).ToArray();
        Assert.Equal(new[] { 1, 2 }, teams);
    }

    [Fact]
    public async Task An_old_client_is_told_to_update_with_code_old_client()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        RelayClient client = await Connect(host, "OLDV2", true, "Old", ArenaLimits.ProtocolVersion - 1);
        JsonElement error = await client.ReceiveJsonAsync("error");
        Assert.Equal("old_client", error.GetProperty("code").GetString());
        Assert.Contains("out of date", error.GetProperty("message").GetString());
        Assert.Equal(RelayCloseCodes.BadCode, await client.ReceiveCloseAsync());
    }
}
