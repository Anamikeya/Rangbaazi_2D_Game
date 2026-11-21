using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using Rangbaazi.Sim;
using Rangbaazi.Sim.Diagnostics;

namespace Rangbaazi.Relay.Arena;

internal enum ArenaPhase
{
    Lobby,
    Playing,
    Over,
}

/// <summary>A room member: a connected person, or a CPU the host added. Guarded by the server lock.</summary>
internal sealed class ArenaPlayer
{
    public ArenaPlayer(int id, string name, ArenaConnection? connection)
    {
        Id = id;
        Name = name;
        Connection = connection;
    }

    /// <summary>Unique in its room, never reused, so clients can follow a player across matches.</summary>
    public int Id { get; }

    public string Name { get; }

    /// <summary>Which of the 31 fighters this member plays (looks only).</summary>
    public string Character { get; set; } = ArenaLimits.DefaultCharacter;

    /// <summary>Null for a CPU, and for a person who left during a match (the CPU plays their fighter to the end).</summary>
    public ArenaConnection? Connection { get; set; }

    public bool IsBot => Connection is null && !Left;

    /// <summary>A person who left during a match; the CPU plays their fighter, and they are removed when the match ends.</summary>
    public bool Left { get; set; }

    /// <summary>This member's team, 1 to 4, in a teams room; 0 in a free-for-all and for someone watching a match.</summary>
    public int Team { get; set; }

    /// <summary>This member's fighter in the current match, or -1 (lobby, or joined mid-match to watch).</summary>
    public int Fighter { get; set; } = -1;

    /// <summary>The buttons from the player's latest INPUT.</summary>
    public ushort Held { get; set; }

    /// <summary>Buttons pressed since the last frame, kept until it runs so a quick tap is never lost.</summary>
    public ushort PressedSinceTick { get; set; }

    public long LastChatMs { get; set; } = long.MinValue / 2;

    public void Send(byte[] payload, WebSocketMessageType type = WebSocketMessageType.Text) => Connection?.Enqueue(payload, type);

    /// <summary>Records an INPUT; a button newly down since the previous INPUT counts as a press.</summary>
    public void SetInput(ushort buttons)
    {
        PressedSinceTick |= (ushort)(buttons & ~Held);
        Held = buttons;
    }

    /// <summary>This frame's buttons: held now, plus any press that came and went since the last frame.</summary>
    public Buttons TakeInput()
    {
        var input = (Buttons)(Held | PressedSinceTick);
        PressedSinceTick = 0;
        return input;
    }
}

/// <summary>
/// One arena room: members (people and CPUs, at most 15), the host's settings, and the match the
/// server runs. Every method runs under the server lock.
/// </summary>
internal sealed class ArenaRoom
{
    private int _nextId;
    private int _overFrames;

    public ArenaRoom(string code, int minutes)
    {
        Code = code;
        Minutes = minutes;
    }

    public string Code { get; }

    public int Minutes { get; set; }

    /// <summary>The arena (an <see cref="ArenaStages"/> id), picked by the host.</summary>
    public string StageId { get; set; } = ArenaLimits.DefaultStage;

    public ArenaPhase Phase { get; private set; } = ArenaPhase.Lobby;

    /// <summary>People and CPUs, in join order (the lobby list).</summary>
    public List<ArenaPlayer> Members { get; } = new();

    public ArenaPlayer? Host { get; private set; }

    /// <summary>Who controls each fighter of the current match (by fighter index).</summary>
    public List<ArenaPlayer> Fighters { get; } = new();

    public MatchRules? Rules { get; private set; }

    public GameState? State { get; private set; }

    public long LastSeed { get; set; }

    /// <summary>Above 0, every match lasts this many frames instead of <see cref="Minutes"/> (local tests only).</summary>
    public int MatchFrames { get; set; }

    public int PeopleCount
    {
        get
        {
            int n = 0;
            foreach (ArenaPlayer p in Members) if (p.Connection is not null) n++;
            return n;
        }
    }

    public bool IsEmpty => PeopleCount == 0;

    /// <summary>
    /// Fifteen people. CPUs never keep a person out: in the lobby a joiner takes the place of the
    /// last-joined CPU, and mid-match a joiner watches until the lobby (final round, 2026-11-21).
    /// </summary>
    public bool IsFull => PeopleCount >= ArenaLimits.MaxPlayers;

    /// <summary>
    /// The CPUs the host asked for (the slider's value, "up to"): a person joining a full room takes a
    /// CPU's place, and the CPU comes back when a person leaves the lobby.
    /// </summary>
    public int BotsWanted { get; private set; }

    /// <summary>When the last person left (Environment.TickCount64), or -1: an empty room is kept for
    /// <see cref="RelayOptions.EmptyRoomGrace"/> so its host can come back to it.</summary>
    public long EmptySinceMs { get; set; } = -1;

    /// <summary>CPU members now.</summary>
    public int BotCount
    {
        get
        {
            int n = 0;
            foreach (ArenaPlayer p in Members) if (p.IsBot) n++;
            return n;
        }
    }

    /// <summary>0 in a free-for-all; 2 to 4 in a teams room (rooms v1, section 1.2). Kept when the host role passes on.</summary>
    public int TeamCount { get; private set; }

    public bool TeamsMode => TeamCount > 0;

    /// <summary>Members on this team.</summary>
    public int Size(int team)
    {
        int n = 0;
        foreach (ArenaPlayer p in Members) if (p.Team == team) n++;
        return n;
    }

    /// <summary>People (not CPUs) on this team.</summary>
    public int People(int team)
    {
        int n = 0;
        foreach (ArenaPlayer p in Members) if (p.Team == team && !p.IsBot) n++;
        return n;
    }

    public ArenaPlayer AddPerson(string name, string character, ArenaConnection connection)
    {
        if (Phase == ArenaPhase.Lobby && Members.Count >= ArenaLimits.MaxPlayers)
        {
            // A full lobby with a CPU in it: the joiner takes the last-joined CPU's place.
            ArenaPlayer? cpu = Members.FindLast(m => m.IsBot);
            if (cpu is not null) Members.Remove(cpu);
        }
        var player = new ArenaPlayer(_nextId++, name, connection) { Character = ArenaLimits.CleanCharacter(character) };
        Members.Add(player);
        Host ??= player;
        if (TeamsMode && Phase == ArenaPhase.Lobby)
        {
            PlaceNew(player);
            BalanceBots();
        }
        // Mid-match the joiner watches with Team 0 and is placed at ReturnToLobby.
        return player;
    }

    /// <summary>Removes a person. Mid-match the CPU plays their fighter until the match ends. The host role passes on.</summary>
    public void RemovePerson(ArenaPlayer player)
    {
        player.Connection = null;
        if (Phase == ArenaPhase.Lobby || player.Fighter < 0)
        {
            Members.Remove(player);
            if (Phase == ArenaPhase.Lobby) RefillBots();
            if (TeamsMode && Phase == ArenaPhase.Lobby) BalanceBots();
        }
        else
        {
            player.Left = true;
            player.Held = 0;
            player.PressedSinceTick = 0;
        }

        if (Host == player)
        {
            Host = null;
            foreach (ArenaPlayer p in Members)
            {
                if (p.Connection is not null)
                {
                    Host = p;
                    break;
                }
            }
        }
    }

    /// <summary>Sets the number of CPU members, within the 15-member limit. Lobby only.</summary>
    public void SetBots(int count)
    {
        BotsWanted = Math.Clamp(count, 0, ArenaLimits.MaxPlayers - 1);
        int people = 0;
        for (int i = Members.Count - 1; i >= 0; i--)
        {
            if (Members[i].IsBot) Members.RemoveAt(i);
            else people++;
        }
        int bots = Math.Clamp(count, 0, ArenaLimits.MaxPlayers - people);
        for (int i = 0; i < bots; i++) AddBot(i);
        if (TeamsMode)
        {
            TeamCount = Math.Max(TeamCount, ArenaLimits.TeamsNeeded(Members.Count));
            BalanceBots();
        }
    }

    /// <summary>
    /// Brings the CPUs back up to <see cref="BotsWanted"/> where there is room (a person who took a
    /// CPU's place has left). Lobby only; the team count stays what it was.
    /// </summary>
    public void RefillBots()
    {
        int bots = BotCount;
        while (bots < BotsWanted && Members.Count < ArenaLimits.MaxPlayers)
        {
            AddBot(bots++);
        }
        if (TeamsMode) BalanceBots();
    }

    /// <summary>The <paramref name="i"/>th CPU (from 0): a fighter nobody else has picked, in a fixed order, so a room looks varied.</summary>
    private void AddBot(int i)
    {
        string character = ArenaLimits.Characters[(Code.GetHashCode(StringComparison.Ordinal) & 0x7FFFFFFF) % ArenaLimits.Characters.Length];
        for (int k = 0; k < ArenaLimits.Characters.Length; k++)
        {
            string c = ArenaLimits.Characters[(i * 7 + k + 3) % ArenaLimits.Characters.Length];
            if (!Members.Exists(m => m.Character == c))
            {
                character = c;
                break;
            }
        }
        var bot = new ArenaPlayer(_nextId++, "CPU " + (i + 1), null) { Character = character };
        Members.Add(bot);
        if (TeamsMode) PlaceNew(bot);
    }

    /// <summary>
    /// The host's one chip row (rooms v1, section 1.3): 0 is a free-for-all; otherwise 2 to 4 teams,
    /// never fewer than the room needs. Members with no team, or on a team that is gone, are placed
    /// again, people first in join order, then CPUs. Lobby only.
    /// </summary>
    public void SetTeams(int n)
    {
        if (n == 0)
        {
            TeamCount = 0;
            foreach (ArenaPlayer p in Members) p.Team = 0;
            return;
        }
        TeamCount = Math.Clamp(n, ArenaLimits.TeamsNeeded(Members.Count), ArenaLimits.MaxTeams);
        foreach (ArenaPlayer p in Members)
        {
            if (p.Team > TeamCount) p.Team = 0;
        }
        foreach (ArenaPlayer p in Members)
        {
            if (p.Team == 0 && !p.IsBot) PlaceNew(p);
        }
        foreach (ArenaPlayer p in Members)
        {
            if (p.Team == 0 && p.IsBot) PlaceNew(p);
        }
        BalanceBots();
    }

    /// <summary>
    /// Puts a member with no team on the team with the fewest members, then the fewest people, then
    /// the lowest number. With every team full and fewer than 4 teams, a team is added first.
    /// </summary>
    public void PlaceNew(ArenaPlayer p)
    {
        p.Team = 0;
        bool allFull = true;
        for (int t = 1; t <= TeamCount; t++)
        {
            if (Size(t) < ArenaLimits.MaxTeamSize) allFull = false;
        }
        if (allFull && TeamCount < ArenaLimits.MaxTeams) TeamCount++;

        int best = 1;
        for (int t = 2; t <= TeamCount; t++)
        {
            int size = Size(t), bestSize = Size(best);
            if (size < bestSize || (size == bestSize && People(t) < People(best))) best = t;
        }
        p.Team = best;
    }

    /// <summary>
    /// A member's pick of a team: false for a team outside 1 to <see cref="TeamCount"/> or their own.
    /// A full team takes them only if it has a CPU, whose last-joined one swaps places with them.
    /// </summary>
    public bool PickTeam(ArenaPlayer p, int team)
    {
        if (team < 1 || team > TeamCount || team == p.Team) return false;
        if (Size(team) < ArenaLimits.MaxTeamSize)
        {
            p.Team = team;
        }
        else
        {
            ArenaPlayer? cpu = LastBotOn(team);
            if (cpu is null) return false; // four people
            cpu.Team = p.Team;
            p.Team = team;
        }
        BalanceBots();
        return true;
    }

    /// <summary>
    /// Keeps CPU-filled teams within one of each other: while the biggest team has 2 or more members
    /// over the smallest and has a CPU, its last-joined CPU moves to the smallest (the lowest number
    /// on ties). People are never moved.
    /// </summary>
    public void BalanceBots()
    {
        if (!TeamsMode) return;
        while (true)
        {
            int smallest = 1, biggest = -1;
            for (int t = 2; t <= TeamCount; t++)
            {
                if (Size(t) < Size(smallest)) smallest = t;
            }
            int most = 0;
            for (int t = 1; t <= TeamCount; t++) most = Math.Max(most, Size(t));
            if (most - Size(smallest) <= 1) return;
            for (int t = 1; t <= TeamCount && biggest < 0; t++)
            {
                if (Size(t) == most && LastBotOn(t) is not null) biggest = t;
            }
            if (biggest < 0) return; // only people on the big teams: the lobby warns instead
            LastBotOn(biggest)!.Team = smallest;
        }
    }

    private ArenaPlayer? LastBotOn(int team)
    {
        for (int i = Members.Count - 1; i >= 0; i--)
        {
            if (Members[i].IsBot && Members[i].Team == team) return Members[i];
        }
        return null;
    }

    /// <summary>Whether the host may start: a teams room needs someone on at least two teams.</summary>
    public bool CanStart(out string reason)
    {
        reason = "";
        if (!TeamsMode) return true;
        int used = 0;
        for (int t = 1; t <= TeamCount; t++)
        {
            if (Size(t) > 0) used++;
        }
        if (used >= 2) return true;
        reason = "Put someone on a second team to start.";
        return false;
    }

    /// <summary>Starts a match with every member as a fighter, in join order.</summary>
    public void Start(long seed)
    {
        Fighters.Clear();
        foreach (ArenaPlayer p in Members)
        {
            p.Fighter = Fighters.Count;
            p.Held = 0;
            p.PressedSinceTick = 0;
            Fighters.Add(p);
        }
        // Each fighter's own weapon hits a little harder for them (Weapons.SignatureFor).
        var signatures = new WeaponId[Fighters.Count];
        for (int i = 0; i < signatures.Length; i++) signatures[i] = Weapons.SignatureFor(ArenaLimits.Look(Fighters[i].Character));
        int[]? teams = null;
        if (TeamsMode)
        {
            teams = new int[Fighters.Count];
            for (int i = 0; i < teams.Length; i++) teams[i] = Fighters[i].Team;
        }
        // 9 or more fighters play the big level (rooms v1, 2.2): the size is fixed for the whole match.
        StageSize size = StageScaling.SizeFor(Fighters.Count);
        StageDefinition stage = Stages.ById(StageId, size) ?? Stages.ById("rooftops", size)!;
        MatchRules rules = MatchRules.Room(stage, Minutes, signatures, teams);
        if (MatchFrames > 0)
        {
            // A local end-to-end test's short match (RelayOptions.ArenaMatchSeconds): the same rules, a shorter clock.
            rules = new MatchRules
            {
                Stage = rules.Stage, TimeLimitFrames = MatchFrames, RagePercent = rules.RagePercent, RespawnShield = rules.RespawnShield,
                Weapons = rules.Weapons, Hazards = rules.Hazards, Flurry = rules.Flurry, Signatures = rules.Signatures, Teams = rules.Teams,
            };
        }
        Rules = rules;
        State = Simulation.CreateInitialState(Rules, Fighters.Count, (ulong)seed);
        Phase = ArenaPhase.Playing;
        _overFrames = 0;
    }

    /// <summary>
    /// Runs one frame: each fighter's buttons (people's latest; CPUs', and those of people who left,
    /// from the sparring bot), then the
    /// sim. Returns the STATE message to send, or null when there is no match running. Once the match
    /// is over the room shows the result for <see cref="ArenaLimits.OverFrames"/>, then returns to the lobby.
    /// </summary>
    public byte[]? Tick(out bool backToLobby)
    {
        backToLobby = false;
        if (State is null || Rules is null) return null;

        if (Phase == ArenaPhase.Over)
        {
            if (++_overFrames >= ArenaLimits.OverFrames)
            {
                ReturnToLobby();
                backToLobby = true;
            }
            return null;
        }

        var inputs = new Buttons[Fighters.Count];
        for (int i = 0; i < Fighters.Count; i++)
        {
            ArenaPlayer p = Fighters[i];
            inputs[i] = p.IsBot || p.Left ? SparringBot.Think(State, Rules, i, CpuLevel.Normal) : p.TakeInput();
        }
        Simulation.Advance(State, Rules, inputs);
        if (State.MatchOver) Phase = ArenaPhase.Over;
        return ArenaBinary.EncodeState(State, Rules);
    }

    /// <summary>Ends the match early (or after the result screen): back to the lobby, leavers dropped.</summary>
    public void ReturnToLobby()
    {
        Members.RemoveAll(p => p.Left);
        // Those who joined a full room to watch take the last-joined CPUs' places.
        while (Members.Count > ArenaLimits.MaxPlayers)
        {
            ArenaPlayer? cpu = Members.FindLast(m => m.IsBot);
            if (cpu is null) break;
            Members.Remove(cpu);
        }
        foreach (ArenaPlayer p in Members) p.Fighter = -1;
        Fighters.Clear();
        State = null;
        Rules = null;
        Phase = ArenaPhase.Lobby;
        if (TeamsMode)
        {
            // Those who joined to watch get a team now.
            foreach (ArenaPlayer p in Members)
            {
                if (p.Team == 0) PlaceNew(p);
            }
            BalanceBots();
        }
        RefillBots(); // CPUs whose places leavers had taken come back
    }

    public void Broadcast(byte[] payload, WebSocketMessageType type = WebSocketMessageType.Text)
    {
        foreach (ArenaPlayer p in Members) p.Send(payload, type);
    }

    /// <summary>Everyone gets the lobby list, each with their own id.</summary>
    public void BroadcastLobby()
    {
        foreach (ArenaPlayer p in Members)
        {
            if (p.Connection is not null) p.Send(ArenaMessages.Lobby(this, p));
        }
    }
}
