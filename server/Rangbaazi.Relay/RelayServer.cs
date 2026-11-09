using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Rangbaazi.Relay;

internal enum JoinOutcome
{
    Joined,
    Full,
    Busy,
}

/// <summary>A room: up to two players, slot 0 (P1) and slot 1 (P2). Guarded by the server lock.</summary>
internal sealed class Room
{
    public Room(string code) => Code = code;

    public string Code { get; }

    public RelayConnection?[] Slots { get; } = new RelayConnection?[2];

    /// <summary>Seed of the last pairing, so the next pairing never reuses it.</summary>
    public long LastSeed { get; set; }
}

/// <summary>
/// Shared relay state: the rooms, connection counts and the timer that enforces idle and close timeouts.
/// Room membership changes only under <see cref="_gate"/>; forwarding takes only the sending connection's
/// own peer lock, so rooms never contend with each other on the hot path.
/// Logs counts and events only, never addresses, room codes or message contents.
/// </summary>
internal sealed partial class RelayServer : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Room> _rooms = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<RelayConnection, byte> _live = new();
    private readonly ILogger _log;
    private readonly Timer _sweeper;
    private int _activeConnections;
    private long _droppedWithoutPeer;
    private long _sendQueueOverflows;
    private int _sweeping;
    private int _stopping;

    public RelayServer(RelayOptions options, ILogger<RelayServer> log)
    {
        Options = options;
        _log = log;
        long periodMs = Math.Clamp((long)(options.IdleTimeout.TotalMilliseconds / 4), 25, 1000);
        _sweeper = new Timer(static s => ((RelayServer)s!).Sweep(), this, periodMs, periodMs);
    }

    public RelayOptions Options { get; }

    public int RoomCount
    {
        get { lock (_gate) return _rooms.Count; }
    }

    public int ActiveConnections => Volatile.Read(ref _activeConnections);

    /// <summary>Binary messages dropped because the sender had no partner (diagnostics and tests).</summary>
    public long DroppedWithoutPeer => Interlocked.Read(ref _droppedWithoutPeer);

    /// <summary>Connections dropped because their outgoing queue overflowed (diagnostics and tests).</summary>
    public long SendQueueOverflows => Interlocked.Read(ref _sendQueueOverflows);

    public bool TryReserveConnection()
    {
        if (Interlocked.Increment(ref _activeConnections) <= Options.MaxConnections) return true;
        Interlocked.Decrement(ref _activeConnections);
        return false;
    }

    public void ReleaseConnection() => Interlocked.Decrement(ref _activeConnections);

    /// <summary>
    /// Puts <paramref name="connection"/> in the lowest free slot of room <paramref name="code"/> and queues
    /// its welcome. If that pairs the room, queues both start messages (one shared fresh seed) before
    /// linking the peers, so a player always sees start before any forwarded binary.
    /// </summary>
    public JoinOutcome Join(string code, RelayConnection connection)
    {
        JoinOutcome outcome = JoinOutcome.Busy;
        int slot = -1, rooms;
        bool created = false, paired = false;
        lock (_gate)
        {
            if (!_rooms.TryGetValue(code, out Room? room) && _rooms.Count < Options.MaxRooms)
            {
                room = new Room(code);
                _rooms.Add(code, room);
                created = true;
            }

            if (room is not null)
            {
                slot = room.Slots[0] is null ? 0 : room.Slots[1] is null ? 1 : -1;
                outcome = slot < 0 ? JoinOutcome.Full : JoinOutcome.Joined;
            }

            if (outcome == JoinOutcome.Joined)
            {
                room!.Slots[slot] = connection;
                connection.Room = room;
                connection.Slot = slot;
                connection.Enqueue(RelayMessages.Welcome(slot, code));

                RelayConnection? other = room.Slots[1 - slot];
                if (other is not null)
                {
                    long seed = SeedGenerator.Next(room.LastSeed);
                    room.LastSeed = seed;
                    RelayConnection.Pair(other, RelayMessages.Start(seed, other.Slot), connection, RelayMessages.Start(seed, slot));
                    paired = true;
                }
            }
            rooms = _rooms.Count;
        }

        int connections = ActiveConnections;
        switch (outcome)
        {
            case JoinOutcome.Joined:
                if (created) LogRoomCreated(rooms, connections);
                LogJoined(slot, paired ? 2 : 1, rooms, connections);
                break;
            case JoinOutcome.Full:
                LogJoinRejected("room full", rooms, connections);
                break;
            default:
                LogJoinRejected("room limit", rooms, connections);
                break;
        }
        return outcome;
    }

    /// <summary>Removes the connection from its room (idempotent). The partner, if any, gets peer_left and keeps its slot.</summary>
    public void Leave(RelayConnection connection)
    {
        bool closedRoom;
        int rooms;
        lock (_gate)
        {
            Room? room = connection.Room;
            if (room is null) return;
            int slot = connection.Slot;
            connection.Room = null;
            connection.Slot = -1;
            connection.SetPeer(null);
            room.Slots[slot] = null;

            RelayConnection? other = room.Slots[1 - slot];
            if (other is not null)
            {
                other.SetPeer(null);
                other.Enqueue(RelayMessages.PeerLeft);
                closedRoom = false;
            }
            else
            {
                _rooms.Remove(room.Code);
                closedRoom = true;
            }
            rooms = _rooms.Count;
        }

        LogLeft(rooms, ActiveConnections);
        if (closedRoom) LogRoomClosed(rooms, ActiveConnections);
    }

    /// <summary>
    /// Registers a joined connection for the sweeper and for <see cref="CloseAll"/>. A connection that
    /// arrives once shutdown has begun is closed at once, so none is missed by CloseAll's pass.
    /// </summary>
    public void Track(RelayConnection connection)
    {
        _live.TryAdd(connection, 0);
        // Full fences on both sides (here and in CloseAll): either CloseAll's pass sees this connection, or
        // this sees the stopping flag, or both (closing twice is harmless).
        if (Interlocked.CompareExchange(ref _stopping, 0, 0) != 0)
        {
            connection.RequestClose(WebSocketCloseStatus.EndpointUnavailable, "server shutting down");
        }
    }

    public void Untrack(RelayConnection connection) => _live.TryRemove(connection, out _);

    public void CountDropped() => Interlocked.Increment(ref _droppedWithoutPeer);

    public void CountOverflow() => Interlocked.Increment(ref _sendQueueOverflows);

    /// <summary>Asks every live connection to close (server shutdown).</summary>
    public void CloseAll()
    {
        Interlocked.Exchange(ref _stopping, 1);
        LogShuttingDown(_live.Count);
        foreach (RelayConnection connection in _live.Keys)
        {
            connection.RequestClose(WebSocketCloseStatus.EndpointUnavailable, "server shutting down");
        }
    }

    /// <summary>Sends one TEXT frame to a connection that never joined a room, then closes it.</summary>
    public async Task RejectAsync(WebSocket socket, byte[] message, WebSocketCloseStatus status, string description)
    {
        using var timeout = new CancellationTokenSource(Options.CloseTimeout);
        try
        {
            await socket.SendAsync(new ReadOnlyMemory<byte>(message), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
            // CloseAsync sends the close frame and waits (discarding data) for the client's reply.
            await socket.CloseAsync(status, description, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            socket.Abort();
        }
    }

    public void Dispose() => _sweeper.Dispose();

    private void Sweep()
    {
        if (Interlocked.Exchange(ref _sweeping, 1) != 0) return;
        try
        {
            long now = Environment.TickCount64;
            foreach (RelayConnection connection in _live.Keys) connection.CheckTimers(now);
        }
        finally
        {
            Volatile.Write(ref _sweeping, 0);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Room created ({Rooms} rooms, {Connections} connections)")]
    private partial void LogRoomCreated(int rooms, int connections);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Room closed ({Rooms} rooms, {Connections} connections)")]
    private partial void LogRoomClosed(int rooms, int connections);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Player joined slot {Slot}, room has {Players} ({Rooms} rooms, {Connections} connections)")]
    private partial void LogJoined(int slot, int players, int rooms, int connections);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Player left ({Rooms} rooms, {Connections} connections)")]
    private partial void LogLeft(int rooms, int connections);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Join rejected: {Reason} ({Rooms} rooms, {Connections} connections)")]
    public partial void LogJoinRejected(string reason, int rooms, int connections);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Connection closed: {Reason}")]
    public partial void LogConnectionClosed(string reason);

    [LoggerMessage(EventId = 7, Level = LogLevel.Debug, Message = "Request rejected: missing or wrong origin secret")]
    public partial void LogOriginRejected();

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "RELAY_ORIGIN_SECRET is not set: requests are not checked for the CloudFront origin header")]
    public partial void LogOriginCheckDisabled();

    [LoggerMessage(EventId = 9, Level = LogLevel.Information, Message = "Relay listening on port {Port} (max {MaxRooms} rooms, {MaxConnections} connections, origin check {OriginCheck})")]
    public partial void LogStarted(int port, int maxRooms, int maxConnections, string originCheck);

    [LoggerMessage(EventId = 10, Level = LogLevel.Information, Message = "Shutting down, closing {Connections} connections")]
    private partial void LogShuttingDown(int connections);

    [LoggerMessage(EventId = 11, Level = LogLevel.Warning, Message = "WEB_ROOT is set but the folder doesn't exist; serving no game files")]
    public partial void LogWebRootMissing();
}
