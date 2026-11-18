using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading;
using Microsoft.Extensions.Logging;
using Rangbaazi.Sim;

namespace Rangbaazi.Relay.Arena;

internal enum ArenaJoinOutcome
{
    Joined,
    Full,
    Busy,
    Taken,
    NoRoom,
}

/// <summary>
/// Arena rooms: up to 15 players each, with the match run here. One lock (<see cref="_gate"/>) guards
/// every room; a dedicated thread runs all rooms' frames at 60 Hz under it, and receive loops take
/// it briefly to record inputs and requests. Sends never block: each connection has its own queue.
/// Logs counts and events only, never names, room codes or addresses.
/// </summary>
internal sealed partial class ArenaServer : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ArenaRoom> _rooms = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<ArenaConnection, byte> _live = new();
    private readonly ILogger _log;
    private readonly Timer _sweeper;
    private readonly Thread _ticker;
    private volatile bool _stopping;
    private int _sweeping;
    private long _framesRun;

    public ArenaServer(RelayOptions options, ILogger<ArenaServer> log)
    {
        Options = options;
        _log = log;
        long periodMs = Math.Clamp((long)(options.IdleTimeout.TotalMilliseconds / 4), 25, 1000);
        _sweeper = new Timer(static s => ((ArenaServer)s!).Sweep(), this, periodMs, periodMs);
        _ticker = new Thread(TickLoop) { IsBackground = true, Name = "arena-ticks" };
        _ticker.Start();
    }

    public RelayOptions Options { get; }

    public int RoomCount
    {
        get { lock (_gate) return _rooms.Count; }
    }

    /// <summary>Room frames run so far, across all rooms (diagnostics and tests).</summary>
    public long FramesRun => Interlocked.Read(ref _framesRun);

    /// <summary>
    /// Creates (host) or joins (guest) room <paramref name="code"/>. The joiner gets the lobby list,
    /// and so does everyone already there. Joining a room mid-match makes the player a spectator
    /// until the next match.
    /// </summary>
    public ArenaJoinOutcome Join(string code, bool host, int minutes, string name, string character, ArenaConnection connection)
    {
        ArenaJoinOutcome outcome;
        int rooms;
        lock (_gate)
        {
            if (_rooms.TryGetValue(code, out ArenaRoom? room))
            {
                outcome = host ? ArenaJoinOutcome.Taken : room.IsFull ? ArenaJoinOutcome.Full : ArenaJoinOutcome.Joined;
            }
            else if (!host)
            {
                outcome = ArenaJoinOutcome.NoRoom;
            }
            else if (_rooms.Count >= Options.MaxRooms)
            {
                outcome = ArenaJoinOutcome.Busy;
            }
            else
            {
                room = new ArenaRoom(code, Math.Clamp(minutes, ArenaLimits.MinMinutes, ArenaLimits.MaxMinutes));
                _rooms.Add(code, room);
                outcome = ArenaJoinOutcome.Joined;
            }

            if (outcome == ArenaJoinOutcome.Joined)
            {
                ArenaPlayer player = room!.AddPerson(name, character, connection);
                connection.Room = room;
                connection.Player = player;
                room.BroadcastLobby();
                if (room.Phase != ArenaPhase.Lobby) player.Send(ArenaMessages.Start(room, player));
            }
            rooms = _rooms.Count;
        }

        if (outcome == ArenaJoinOutcome.Joined) LogJoined(rooms);
        else LogJoinRejected(outcome.ToString(), rooms);
        return outcome;
    }

    /// <summary>Removes the connection from its room (idempotent); an empty room closes.</summary>
    public void Leave(ArenaConnection connection)
    {
        int rooms;
        bool closed = false;
        lock (_gate)
        {
            ArenaRoom? room = connection.Room;
            ArenaPlayer? player = connection.Player;
            if (room is null || player is null) return;
            connection.Room = null;
            connection.Player = null;
            room.RemovePerson(player);
            if (room.IsEmpty)
            {
                _rooms.Remove(room.Code);
                closed = true;
            }
            else
            {
                room.BroadcastLobby();
            }
            rooms = _rooms.Count;
        }
        LogLeft(rooms);
        if (closed) LogRoomClosed(rooms);
    }

    public void Input(ArenaConnection connection, ushort buttons)
    {
        lock (_gate) connection.Player?.SetInput(buttons);
    }

    /// <summary>Handles a TEXT request. Returns false for a malformed or unknown one.</summary>
    public bool Request(ArenaConnection connection, ArenaRequest request)
    {
        lock (_gate)
        {
            ArenaRoom? room = connection.Room;
            ArenaPlayer? player = connection.Player;
            if (room is null || player is null) return true; // already leaving
            bool isHost = room.Host == player;
            switch (request.Type)
            {
                case "ping":
                    return true;

                case "settings" when isHost && room.Phase == ArenaPhase.Lobby:
                    room.Minutes = Math.Clamp(request.Number, ArenaLimits.MinMinutes, ArenaLimits.MaxMinutes);
                    room.BroadcastLobby();
                    return true;

                case "stage" when isHost && room.Phase == ArenaPhase.Lobby:
                    if (!ArenaLimits.IsStage(request.Id)) return false;
                    room.StageId = request.Id!;
                    room.BroadcastLobby();
                    return true;

                case "character" when room.Phase == ArenaPhase.Lobby:
                    player.Character = ArenaLimits.CleanCharacter(request.Id);
                    room.BroadcastLobby();
                    return true;

                case "bots" when isHost && room.Phase == ArenaPhase.Lobby:
                    room.SetBots(request.Number);
                    room.BroadcastLobby();
                    return true;

                case "start" when isHost && room.Phase == ArenaPhase.Lobby:
                    room.LastSeed = SeedGenerator.Next(room.LastSeed);
                    room.Start(room.LastSeed);
                    foreach (ArenaPlayer p in room.Members) p.Send(ArenaMessages.Start(room, p));
                    room.BroadcastLobby();
                    LogMatchStarted(room.Fighters.Count);
                    return true;

                case "end" when isHost && room.Phase != ArenaPhase.Lobby:
                    room.ReturnToLobby();
                    room.BroadcastLobby();
                    return true;

                case "chat":
                    if (request.Number < 0 || request.Number >= ArenaLimits.QuickChatPhrases) return false;
                    long now = Environment.TickCount64;
                    if (now - player.LastChatMs < ArenaLimits.ChatIntervalMs) return true; // too fast: dropped
                    player.LastChatMs = now;
                    room.Broadcast(ArenaMessages.Chat(player, request.Number));
                    return true;

                case "settings" or "bots" or "start" or "end" or "stage" or "character":
                    player.Send(ArenaMessages.Error("not_allowed", "Only the host can do that, and only in the lobby."));
                    return true;

                default:
                    return false;
            }
        }
    }

    public void Track(ArenaConnection connection)
    {
        _live.TryAdd(connection, 0);
        if (_stopping) connection.RequestClose(WebSocketCloseStatus.EndpointUnavailable, "server shutting down");
    }

    public void Untrack(ArenaConnection connection) => _live.TryRemove(connection, out _);

    public void CloseAll()
    {
        _stopping = true;
        foreach (ArenaConnection connection in _live.Keys)
        {
            connection.RequestClose(WebSocketCloseStatus.EndpointUnavailable, "server shutting down");
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _sweeper.Dispose();
        _ticker.Join(TimeSpan.FromSeconds(2));
    }

    /// <summary>Runs every room's frame at 60 Hz, catching up (at most a few frames) after a late wake.</summary>
    private void TickLoop()
    {
        long frequency = Stopwatch.Frequency;
        long start = Stopwatch.GetTimestamp();
        long done = 0;
        var playing = new List<ArenaRoom>();
        while (!_stopping)
        {
            long due = (Stopwatch.GetTimestamp() - start) * Simulation.TicksPerSecond / frequency;
            if (due - done > 4) done = due - 4; // a long stall: skip ahead rather than race
            while (done < due)
            {
                done++;
                RunFrame(playing);
            }
            long nextAt = start + (done + 1) * frequency / Simulation.TicksPerSecond;
            long waitMs = (nextAt - Stopwatch.GetTimestamp()) * 1000 / frequency;
            Thread.Sleep(waitMs > 1 ? (int)waitMs - 1 : 1);
        }
    }

    private void RunFrame(List<ArenaRoom> playing)
    {
        try
        {
            lock (_gate)
            {
                playing.Clear();
                foreach (ArenaRoom room in _rooms.Values) if (room.Phase != ArenaPhase.Lobby) playing.Add(room);
                foreach (ArenaRoom room in playing)
                {
                    byte[]? state = room.Tick(out bool backToLobby);
                    if (state is not null)
                    {
                        room.Broadcast(state, WebSocketMessageType.Binary);
                        Interlocked.Increment(ref _framesRun);
                    }
                    if (backToLobby) room.BroadcastLobby();
                }
            }
        }
        catch (Exception ex)
        {
            // A bug in one frame must not stop every room's clock.
            LogFrameFailed(ex);
        }
    }

    private void Sweep()
    {
        if (Interlocked.Exchange(ref _sweeping, 1) != 0) return;
        try
        {
            long now = Environment.TickCount64;
            foreach (ArenaConnection connection in _live.Keys) connection.CheckTimers(now);
        }
        finally
        {
            Volatile.Write(ref _sweeping, 0);
        }
    }

    [LoggerMessage(EventId = 101, Level = LogLevel.Information, Message = "Arena player joined ({Rooms} arena rooms)")]
    private partial void LogJoined(int rooms);

    [LoggerMessage(EventId = 102, Level = LogLevel.Information, Message = "Arena join rejected: {Reason} ({Rooms} arena rooms)")]
    public partial void LogJoinRejected(string reason, int rooms);

    [LoggerMessage(EventId = 103, Level = LogLevel.Information, Message = "Arena player left ({Rooms} arena rooms)")]
    private partial void LogLeft(int rooms);

    [LoggerMessage(EventId = 104, Level = LogLevel.Information, Message = "Arena room closed ({Rooms} arena rooms)")]
    private partial void LogRoomClosed(int rooms);

    [LoggerMessage(EventId = 105, Level = LogLevel.Information, Message = "Arena match started with {Fighters} fighters")]
    private partial void LogMatchStarted(int fighters);

    [LoggerMessage(EventId = 106, Level = LogLevel.Information, Message = "Arena connection closed: {Reason}")]
    public partial void LogConnectionClosed(string reason);

    [LoggerMessage(EventId = 107, Level = LogLevel.Error, Message = "Arena frame failed")]
    private partial void LogFrameFailed(Exception ex);
}
