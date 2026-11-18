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

    /// <summary>Null for a CPU, and for a person who left during a match (their fighter stays, idle).</summary>
    public ArenaConnection? Connection { get; set; }

    public bool IsBot => Connection is null && !Left;

    /// <summary>A person who left during a match; removed when the match ends.</summary>
    public bool Left { get; set; }

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

    public bool IsFull => Members.Count >= ArenaLimits.MaxPlayers;

    public ArenaPlayer AddPerson(string name, string character, ArenaConnection connection)
    {
        var player = new ArenaPlayer(_nextId++, name, connection) { Character = ArenaLimits.CleanCharacter(character) };
        Members.Add(player);
        Host ??= player;
        return player;
    }

    /// <summary>Removes a person. Mid-match their fighter stays (idle) until the match ends. The host role passes on.</summary>
    public void RemovePerson(ArenaPlayer player)
    {
        player.Connection = null;
        if (Phase == ArenaPhase.Lobby || player.Fighter < 0)
        {
            Members.Remove(player);
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
        int people = 0;
        for (int i = Members.Count - 1; i >= 0; i--)
        {
            if (Members[i].IsBot) Members.RemoveAt(i);
            else people++;
        }
        int bots = Math.Clamp(count, 0, ArenaLimits.MaxPlayers - people);
        for (int i = 0; i < bots; i++)
        {
            // CPUs take the fighters nobody else has picked, in a fixed order, so a room looks varied.
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
            Members.Add(new ArenaPlayer(_nextId++, "CPU " + (i + 1), null) { Character = character });
        }
    }

    /// <summary>Starts a match with every member as a fighter.</summary>
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
        for (int i = 0; i < signatures.Length; i++) signatures[i] = Weapons.SignatureFor(Fighters[i].Character);
        Rules = MatchRules.Room(ArenaStages.ById(StageId) ?? Stages.Rooftops, Minutes, signatures);
        State = Simulation.CreateInitialState(Rules, Fighters.Count, (ulong)seed);
        Phase = ArenaPhase.Playing;
        _overFrames = 0;
    }

    /// <summary>
    /// Runs one frame: each fighter's buttons (people's latest, CPUs' from the sparring bot), then the
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
            inputs[i] = p.IsBot ? SparringBot.Think(State, Rules, i, CpuLevel.Normal) : p.TakeInput();
        }
        Simulation.Advance(State, Rules, inputs);
        if (State.MatchOver) Phase = ArenaPhase.Over;
        return ArenaBinary.EncodeState(State, Rules);
    }

    /// <summary>Ends the match early (or after the result screen): back to the lobby, leavers dropped.</summary>
    public void ReturnToLobby()
    {
        Members.RemoveAll(p => p.Left);
        foreach (ArenaPlayer p in Members) p.Fighter = -1;
        Fighters.Clear();
        State = null;
        Rules = null;
        Phase = ArenaPhase.Lobby;
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
