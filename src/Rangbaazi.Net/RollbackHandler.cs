using System;
using Backdash;
using Backdash.Serialization;
using Rangbaazi.Sim;

namespace Rangbaazi.Net;

/// <summary>
/// Backdash's view of the match: saves and loads <see cref="GameState"/> for rollback and
/// re-simulates frames. Every callback runs on the thread that calls the session (the game
/// thread). Terminal events are only recorded here; <see cref="OnlineMatch"/> acts on them after
/// the session call returns, because a session must not be disposed from inside its own callback.
/// </summary>
internal sealed class RollbackHandler : INetcodeSessionHandler
{
    private readonly MatchRules _rules;
    private INetcodeSession<Buttons>? _session;
    private bool _inRollback;
    private int _resimulated;

    public RollbackHandler(GameState state, MatchRules rules)
    {
        State = state;
        _rules = rules;
    }

    public GameState State { get; }

    public ChecksumHistory History { get; } = new(1024);

    public RollbackStats Rollbacks { get; } = new();

    /// <summary>Test hook: runs after every simulated frame, re-simulations included.</summary>
    public Action<GameState>? AfterAdvance { get; init; }

    public Action? SessionStarted { get; set; }
    public Action? SessionClosed { get; set; }
    public Action<int>? TimeSyncRequested { get; set; }
    public Action<NetcodePlayer, PeerEventInfo>? PeerEvent { get; set; }

    public void Attach(INetcodeSession<Buttons> session) => _session = session;

    /// <summary>Simulates one frame with the inputs the session synchronized for it.</summary>
    public void Step()
    {
        INetcodeSession<Buttons> session = _session ?? throw new InvalidOperationException("No session attached.");
        Simulation.Advance(State, _rules, session.CurrentInputs[..State.Fighters.Length]);
        AfterAdvance?.Invoke(State);
    }

    public void SaveState(Frame frame, ref readonly BinaryBufferWriter writer)
    {
        if (frame.Number != State.Frame)
            throw new InvalidOperationException($"Backdash saved frame {frame.Number} while the match is on frame {State.Frame}.");
        ulong checksum = GameStateCodec.Write(State, in writer);
        History.Record(State.Frame, checksum);
    }

    public void LoadState(Frame frame, ref readonly BinaryBufferReader reader)
    {
        GameStateCodec.Read(State, in reader);
        if (State.Frame != frame.Number)
            throw new InvalidOperationException($"Backdash loaded frame {frame.Number} but the saved state is frame {State.Frame}.");
    }

    public void AdvanceFrame()
    {
        INetcodeSession<Buttons> session = _session ?? throw new InvalidOperationException("No session attached.");
        if (_inRollback) _resimulated++;
        session.SynchronizeInputs();
        Step();
        session.AdvanceFrame();
    }

    // A rollback's depth is the number of frames re-simulated between these two calls (Backdash
    // passes different frames to them in different session types, so they aren't used).
    public void BeginRollback(Frame frame)
    {
        _inRollback = true;
        _resimulated = 0;
    }

    public void EndRollback(Frame frame)
    {
        if (_inRollback) Rollbacks.Record(_resimulated);
        _inRollback = false;
    }

    public void OnSessionStart() => SessionStarted?.Invoke();

    public void OnSessionClose() => SessionClosed?.Invoke();

    public void TimeSync(FrameSpan framesAhead) => TimeSyncRequested?.Invoke(framesAhead.Frames);

    public void OnPeerEvent(NetcodePlayer player, in PeerEventInfo evt) => PeerEvent?.Invoke(player, evt);
}

/// <summary>The checksum of each recent frame's state, overwritten when a frame is re-simulated.</summary>
public sealed class ChecksumHistory
{
    private readonly int[] _frames;
    private readonly ulong[] _checksums;

    public ChecksumHistory(int capacity)
    {
        _frames = new int[capacity];
        _checksums = new ulong[capacity];
        Array.Fill(_frames, -1);
    }

    public void Record(int frame, ulong checksum)
    {
        int slot = frame % _frames.Length;
        _frames[slot] = frame;
        _checksums[slot] = checksum;
    }

    public bool TryGet(int frame, out ulong checksum)
    {
        int slot = frame % _frames.Length;
        bool found = frame >= 0 && _frames[slot] == frame;
        checksum = found ? _checksums[slot] : 0;
        return found;
    }
}

/// <summary>How far back rollbacks went (GDD KPI: median 4 frames online).</summary>
public sealed class RollbackStats
{
    private readonly int[] _byDepth = new int[64];

    public int Count { get; private set; }
    public int Last { get; private set; }
    public int Max { get; private set; }

    /// <summary>Median depth of the rollbacks so far; 0 before the first.</summary>
    public int Median
    {
        get
        {
            if (Count == 0) return 0;
            int seen = 0;
            for (int depth = 0; depth < _byDepth.Length; depth++)
            {
                seen += _byDepth[depth];
                if (seen * 2 >= Count) return depth;
            }
            return _byDepth.Length - 1;
        }
    }

    public void Record(int depth)
    {
        if (depth <= 0) return;
        Count++;
        Last = depth;
        if (depth > Max) Max = depth;
        _byDepth[Math.Min(depth, _byDepth.Length - 1)]++;
    }
}
