using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using Backdash;
using Backdash.Core;
using Backdash.Network;
using Rangbaazi.Net.Transport;
using Rangbaazi.Sim;

namespace Rangbaazi.Net;

/// <summary>
/// One online 1v1 match over Backdash rollback netcode. The game calls <see cref="Tick"/> once per
/// physics tick (60 Hz) with the local player's buttons, then draws <see cref="State"/>.
///
/// Lifecycle: <see cref="OnlinePhase.Handshaking"/> (our hello: same protocol, same rules, opposite
/// slots, fresh match ids) → <see cref="OnlinePhase.Synchronizing"/> (Backdash measures the
/// connection) → <see cref="OnlinePhase.Running"/> → <see cref="OnlinePhase.Ended"/>. Once ended
/// the network is closed and <see cref="State"/> stays as it was; start a new match to play again.
///
/// While running, both peers regularly exchange the checksum of a frame whose inputs are final on
/// both sides, so a desync is detected exactly, at the frame it shows. Backdash's own consistency
/// check is off: it can report false mismatches and then kills the session.
///
/// Not thread-safe: call everything from one thread. Any exception from Backdash ends the match
/// (its internal checks are live in release builds and leave a session unusable).
/// </summary>
public sealed class OnlineMatch : IDisposable
{
    private const int ByeRepeats = 3;
    private static readonly Buttons KnownButtons = AllButtons();

    private readonly OnlineMatchOptions _options;
    private readonly GuardedSocket _socket;
    private readonly ulong _nonce;
    private readonly ulong _fingerprint;
    private readonly Action<string>? _log;
    private readonly ConcurrentQueue<string> _backdashLog = new();
    private readonly List<(int Frame, ulong Checksum)> _remoteChecksums = new();

    private GameState _state;
    private RollbackHandler? _handler;
    private INetcodeSession<Buttons>? _session;
    private NetcodePlayer? _localPlayer;
    private NetcodePlayer? _remotePlayer;
    private ConfirmedInputLog? _confirmed;

    private ulong _peerNonce;
    private bool _peerSawUs;
    private bool _sessionStarted;
    private (OnlineEndReason Reason, string Message, ByeReason? Bye)? _pendingEnd;
    private long _tick;
    private long _phaseStartTick;
    private long _lastHelloTick = long.MinValue / 2;
    private long _lastChecksumTick;
    private int _lastSentChecksumFrame = -1;
    private int _stalledTicks;
    private int _skipTicks;
    private long _interruptEndsAtTick;
    private bool _disposed;

    /// <param name="transport">Bound socket for this player; the match owns and closes it.</param>
    /// <param name="log">Optional sink for diagnostics, called on the thread that calls <see cref="Tick"/>.</param>
    public OnlineMatch(OnlineMatchOptions options, IDatagramTransport transport, Action<string>? log = null)
    {
        options.Validate();
        _options = options;
        _log = log;
        _fingerprint = MatchFingerprint.Compute(options.Rules);
        _nonce = NewNonce();
        _state = Simulation.CreateInitialState(options.Rules, 2, 0);
        _socket = new GuardedSocket(transport, options.Peer);
        LocalEndPoint = transport.LocalEndPoint;
    }

    public OnlineMatchOptions Options => _options;
    public MatchRules Rules => _options.Rules;
    public int LocalSlot => _options.LocalSlot;
    public System.Net.IPEndPoint LocalEndPoint { get; }

    /// <summary>The match as of the latest frame (partly predicted while waiting for the opponent's input).</summary>
    public GameState State => _state;

    public OnlinePhase Phase { get; private set; } = OnlinePhase.Handshaking;
    public OnlineEndReason EndReason { get; private set; }
    public string EndMessage { get; private set; } = "";

    /// <summary>The match seed, agreed from both players' match ids. 0 until the handshake is done.</summary>
    public ulong Seed { get; private set; }

    /// <summary>Backdash's connection handshake progress, 0 to 1.</summary>
    public float SyncProgress { get; private set; }

    /// <summary>Round-trip time to the opponent, in milliseconds; 0 until measured.</summary>
    public int PingMs { get; private set; }

    /// <summary>Frames simulated past the last frame with the opponent's real input.</summary>
    public int FramesBehind { get; private set; }

    /// <summary>
    /// Backdash's estimate of how many frames this side's clock is behind the opponent's (negative:
    /// ahead). Time sync makes the side that is ahead wait, so it hovers around 0.
    /// </summary>
    public int FramesBehindOpponent { get; private set; }

    /// <summary>No packets from the opponent for a while; the match ends if it lasts <see cref="InterruptSecondsLeft"/> more.</summary>
    public bool Interrupted { get; private set; }

    public float InterruptSecondsLeft => Interrupted ? Math.Max(0, _interruptEndsAtTick - _tick) / (float)Simulation.TicksPerSecond : 0;

    /// <summary>Frozen this tick: too far ahead of the opponent's confirmed input.</summary>
    public bool Stalled { get; private set; }

    public RollbackStats Rollbacks => _handler?.Rollbacks ?? EmptyStats;

    /// <summary>Checksums compared with the opponent's and found equal.</summary>
    public int ChecksumsVerified { get; private set; }

    public int LastVerifiedFrame { get; private set; } = -1;

    /// <summary>The frame a desync was found at, or -1.</summary>
    public int DesyncFrame { get; private set; } = -1;

    /// <summary>Ticks skipped so the faster side waits for the slower one (Backdash time sync).</summary>
    public int TimeSyncTicksSkipped { get; private set; }

    /// <summary>
    /// The newest frame whose state is final: every input before it is confirmed, and it has been
    /// simulated. Its checksum is what gets compared with the opponent.
    /// </summary>
    public int FinalFrame => _confirmed == null ? -1 : Math.Min(_confirmed.Frames, _state.Frame);

    public ConfirmedInputLog? ConfirmedInputs => _confirmed;

    public ChecksumHistory? Checksums => _handler?.History;

    public long DroppedFromStrangers => _socket.DroppedFromStrangers;
    public long DroppedMalformed => _socket.DroppedMalformed;

    private static readonly RollbackStats EmptyStats = new();

    /// <summary>Runs one tick: network, handshake, and at most one new simulated frame (plus any rollback).</summary>
    public void Tick(Buttons localButtons)
    {
        if (_disposed) return;
        _tick++;
        Stalled = false;
        try
        {
            if (Phase != OnlinePhase.Ended) ReadControlMessages();
            if (Phase is OnlinePhase.Handshaking or OnlinePhase.Synchronizing) SendHelloIfDue();
            if (Phase is OnlinePhase.Synchronizing or OnlinePhase.Running) TickSession(localButtons & KnownButtons);
            if (Phase == OnlinePhase.Running) ExchangeChecksums();
        }
        catch (Exception e)
        {
            _pendingEnd = null;
            End(OnlineEndReason.Error, $"Network error: {e.Message}", ByeReason.Error);
            Log($"[net] {e}");
        }
        DrainLog();
    }

    /// <summary>Leaves the match and tells the opponent (a rematch request when <paramref name="rematch"/>).</summary>
    public void Leave(bool rematch = false) =>
        End(rematch ? OnlineEndReason.LocalRematch : OnlineEndReason.LocalLeft,
            rematch ? "You asked for a rematch." : "You left the match.",
            rematch ? ByeReason.Rematch : ByeReason.Left);

    public void Dispose()
    {
        if (_disposed) return;
        Leave();
        _disposed = true;
        CloseNetwork();
        DrainLog();
    }

    // ---- handshake ----

    private void SendHelloIfDue()
    {
        if (_tick - _lastHelloTick < _options.HelloIntervalTicks) return;
        SendHello();
    }

    private void SendHello()
    {
        _lastHelloTick = _tick;
        _socket.SendControl(ControlMessage.Hello(_nonce, _peerNonce, _options.LocalSlot, _fingerprint));
    }

    private void ReadControlMessages()
    {
        while (Phase != OnlinePhase.Ended && _socket.TryReceiveControl(out ControlMessage message))
        {
            switch (message.Kind)
            {
                case ControlKind.Hello:
                    OnHello(in message);
                    break;
                case ControlKind.Checksum when message.Nonce == _peerNonce && _peerNonce != 0:
                    if (_remoteChecksums.Count < 64) _remoteChecksums.Add((message.Frame, message.Checksum));
                    break;
                case ControlKind.Bye when (message.Nonce == _peerNonce && _peerNonce != 0) || message.SeenNonce == _nonce:
                    OnBye(message.Reason);
                    break;
            }
        }
    }

    private void OnHello(in ControlMessage hello)
    {
        if (hello.Version != NetProtocol.Version)
        {
            End(OnlineEndReason.VersionMismatch,
                $"Your opponent runs a different version of the game (netcode {hello.Version}, yours {NetProtocol.Version}).", ByeReason.Rejected);
            return;
        }
        if (hello.Fingerprint != _fingerprint)
        {
            End(OnlineEndReason.RulesMismatch,
                "Your opponent's game plays by different rules (a different build, or a setting such as Guts differs).", ByeReason.Rejected);
            return;
        }
        if (hello.Slot == _options.LocalSlot || hello.Slot > 1)
        {
            End(OnlineEndReason.SameSide, $"You both picked P{_options.LocalSlot + 1}. One of you must be P1 and the other P2.", ByeReason.Rejected);
            return;
        }

        if (hello.Nonce != _peerNonce)
        {
            if (_peerNonce != 0 && Phase != OnlinePhase.Handshaking)
            {
                End(OnlineEndReason.OpponentLeft, "Your opponent restarted their game.", bye: null);
                return;
            }
            bool first = _peerNonce == 0;
            _peerNonce = hello.Nonce;
            _peerSawUs = false;
            if (first) SendHello(); // answer at once instead of on the next interval
        }
        if (hello.SeenNonce == _nonce) _peerSawUs = true;
        if (Phase == OnlinePhase.Handshaking && _peerSawUs) StartSession();
    }

    private void OnBye(ByeReason reason)
    {
        switch (reason)
        {
            case ByeReason.Rematch:
                End(OnlineEndReason.OpponentRematch, "Your opponent wants a rematch.", bye: null);
                break;
            case ByeReason.Desync:
                End(OnlineEndReason.Desync, "Your opponent's game found a desync.", bye: null);
                break;
            case ByeReason.Rejected:
                End(OnlineEndReason.OpponentRejected,
                    "Your opponent's game refused the match (different version or rules, or you both picked the same side).", bye: null);
                break;
            case ByeReason.GaveUpWaiting:
                End(OnlineEndReason.OpponentLeft, "Your opponent stopped waiting for you.", bye: null);
                break;
            case ByeReason.Error:
                End(OnlineEndReason.OpponentLeft, "Your opponent's game hit an error and left.", bye: null);
                break;
            default:
                End(OnlineEndReason.OpponentLeft, "Your opponent left.", bye: null);
                break;
        }
    }

    // ---- Backdash session ----

    private void StartSession()
    {
        ulong p1 = _options.LocalSlot == 0 ? _nonce : _peerNonce;
        ulong p2 = _options.LocalSlot == 0 ? _peerNonce : _nonce;
        Seed = MixSeed(p1, p2);
        _state = Simulation.CreateInitialState(_options.Rules, 2, Seed);

        _handler = new RollbackHandler(_state, _options.Rules) { AfterAdvance = _options.AfterAdvance };
        _handler.SessionStarted = () => _sessionStarted = true;
        _handler.SessionClosed = () =>
        {
            if (Phase != OnlinePhase.Ended) _pendingEnd ??= (OnlineEndReason.Error, "The connection was closed.", ByeReason.Error);
        };
        _handler.TimeSyncRequested = frames => _skipTicks += Math.Max(0, frames);
        _handler.PeerEvent = OnPeerEvent;
        _confirmed = new ConfirmedInputLog(2);

        _localPlayer = NetcodePlayer.CreateLocal();
        _remotePlayer = NetcodePlayer.CreateRemote(_options.Peer);
        var players = _options.LocalSlot == 0
            ? new[] { _localPlayer, _remotePlayer }
            : new[] { _remotePlayer, _localPlayer };

        OnlineMatchOptions o = _options;
        _session = RollbackNetcode.WithInputType<Buttons>()
            .WithPort(_socket.Port)
            .WithPlayers(players)
            .WithHandler(_handler)
            .WithSocketFactory((_, _) => _socket)
            .WithChecksumProvider(static data => GameStateCodec.Fold(data))
            .WithInputListener(_confirmed)
            .WithStateSizeHint(GameStateCodec.SizeFor(2))
            .WithLogLevel(LogLevel.Warning)
            .WithLogWriter((level, message) =>
            {
                // It warns on every tick spent at the prediction limit; Stalled already reports that.
                if (!message.Contains("prediction barrier", StringComparison.Ordinal)) _backdashLog.Enqueue($"[backdash {level}] {message}");
            })
            .Configure(n =>
            {
                n.PredictionFrames = o.PredictionFrames;
                n.InputDelayFrames = o.InputDelayFrames;
                n.FrameRate = Simulation.TicksPerSecond;
                n.StateSerializationEndianness = Endianness.LittleEndian;
                n.Protocol.DisconnectNotifyStart = o.DisconnectNotifyStart;
                n.Protocol.DisconnectTimeout = o.DisconnectTimeout;
                n.Protocol.ConsistencyCheckEnabled = false;
                o.ConfigureBackdash?.Invoke(n);
            })
            .ForRemote()
            .Build();
        _handler.Attach(_session);
        _session.Start();

        Phase = OnlinePhase.Synchronizing;
        _phaseStartTick = _tick;
        Log($"[net] Connected to {_options.Peer} as P{_options.LocalSlot + 1}; synchronizing (seed {Seed:X16}).");
    }

    private void TickSession(Buttons local)
    {
        INetcodeSession<Buttons> session = _session!;
        session.BeginFrame();
        if (ApplyPendingEnd()) return;
        ReadNetworkStats();

        if (!_sessionStarted)
        {
            if (_tick - _phaseStartTick > _options.SyncTimeoutTicks)
                End(OnlineEndReason.SyncFailed, "Couldn't synchronize with your opponent.", ByeReason.GaveUpWaiting);
            return;
        }
        if (Phase == OnlinePhase.Synchronizing)
        {
            Phase = OnlinePhase.Running;
            SyncProgress = 1;
            Log("[net] Match running.");
        }

        // Time sync: this side is ahead, so let the opponent catch up by skipping some ticks,
        // one every other tick so it never looks like a freeze.
        if (_skipTicks > 0 && (_tick & 1) == 0)
        {
            _skipTicks--;
            TimeSyncTicksSkipped++;
            return;
        }

        ResultCode result = session.AddLocalInput(_localPlayer!, local);
        if (ApplyPendingEnd()) return;
        switch (result)
        {
            case ResultCode.Ok:
            case ResultCode.InputDropped: // the input was taken; only a resend buffer was full
                break;
            case ResultCode.NotSynchronized:
                return;
            case ResultCode.PredictionThreshold:
                Stalled = true;
                if (++_stalledTicks > _options.StallTimeoutTicks)
                    End(OnlineEndReason.OpponentNotResponding, "Your opponent's game stopped responding.", ByeReason.GaveUpWaiting);
                return;
            default:
                throw new InvalidOperationException($"Backdash refused the local input: {result}.");
        }
        _stalledTicks = 0;

        result = session.SynchronizeInputs();
        if (result != ResultCode.Ok) throw new InvalidOperationException($"Backdash couldn't synchronize inputs: {result}.");
        _handler!.Step();
        session.AdvanceFrame();
        ApplyPendingEnd();
    }

    private void OnPeerEvent(NetcodePlayer player, PeerEventInfo evt)
    {
        switch (evt.Type)
        {
            case PeerEvent.Synchronizing:
                if (evt.Synchronizing.TotalSteps > 0) SyncProgress = (float)evt.Synchronizing.CurrentStep / evt.Synchronizing.TotalSteps;
                break;
            case PeerEvent.Synchronized:
                SyncProgress = 1;
                break;
            case PeerEvent.SynchronizationFailure:
                _pendingEnd ??= (OnlineEndReason.SyncFailed, "Couldn't synchronize with your opponent.", ByeReason.GaveUpWaiting);
                break;
            case PeerEvent.ConnectionInterrupted:
                Interrupted = true;
                _interruptEndsAtTick = _tick + (long)Math.Ceiling(evt.ConnectionInterrupted.DisconnectTimeout.TotalSeconds * Simulation.TicksPerSecond);
                Log("[net] Connection interrupted.");
                break;
            case PeerEvent.ConnectionResumed:
                Interrupted = false;
                Log("[net] Connection resumed.");
                break;
            case PeerEvent.Disconnected:
                _pendingEnd ??= (OnlineEndReason.OpponentLeft, "Lost the connection to your opponent.", null);
                break;
            case PeerEvent.ChecksumMismatch:
                // Backdash's own check is off; if it ever reports, trust our exact check instead.
                Log($"[net] Backdash reported a checksum mismatch at frame {evt.ChecksumMismatch.MismatchFrame.Number}.");
                break;
        }
    }

    private void ReadNetworkStats()
    {
        if (_session == null || _remotePlayer == null) return;
        FramesBehind = Math.Max(0, _session.FramesBehind.Frames); // negative: the opponent's input is already here for coming frames
        if (_remotePlayer.NetworkStats.Valid)
        {
            PingMs = (int)Math.Round(_remotePlayer.NetworkStats.Ping.TotalMilliseconds);
            FramesBehindOpponent = _remotePlayer.NetworkStats.LocalFramesBehind.Frames;
        }
    }

    // ---- desync detection ----

    private void ExchangeChecksums()
    {
        int final = FinalFrame;
        RollbackHandler handler = _handler!;

        if (_tick - _lastChecksumTick >= _options.ChecksumIntervalTicks && final > _lastSentChecksumFrame
            && handler.History.TryGet(final, out ulong mine))
        {
            _socket.SendControl(ControlMessage.ChecksumOf(_nonce, final, mine));
            _lastSentChecksumFrame = final;
            _lastChecksumTick = _tick;
        }

        for (int i = _remoteChecksums.Count - 1; i >= 0; i--)
        {
            (int frame, ulong theirs) = _remoteChecksums[i];
            if (frame > final) continue; // not final here yet: compare later
            _remoteChecksums.RemoveAt(i);
            if (!handler.History.TryGet(frame, out ulong ours)) continue; // too old to compare
            if (ours == theirs)
            {
                ChecksumsVerified++;
                LastVerifiedFrame = Math.Max(LastVerifiedFrame, frame);
                continue;
            }
            DesyncFrame = frame;
            End(OnlineEndReason.Desync, $"Desync: the games differ at frame {frame} ({ours:X16} here, {theirs:X16} there).", ByeReason.Desync);
            return;
        }
    }

    // ---- ending ----

    private bool ApplyPendingEnd()
    {
        if (_pendingEnd is not { } end) return Phase == OnlinePhase.Ended;
        _pendingEnd = null;
        End(end.Reason, end.Message, end.Bye);
        return true;
    }

    private void End(OnlineEndReason reason, string message, ByeReason? bye)
    {
        if (Phase == OnlinePhase.Ended) return;
        Phase = OnlinePhase.Ended;
        EndReason = reason;
        EndMessage = message;
        Interrupted = false;
        Log($"[net] Match over: {message}");
        if (bye is { } why)
            for (int i = 0; i < ByeRepeats; i++) _socket.SendControl(ControlMessage.Bye(_nonce, _peerNonce, why));
        CloseNetwork();
    }

    private void CloseNetwork()
    {
        INetcodeSession<Buttons>? session = _session;
        _session = null;
        if (session != null)
        {
            try
            {
                session.Dispose();
            }
            catch (Exception e)
            {
                Log($"[net] Closing the session failed: {e.Message}");
            }
        }
        _socket.Dispose();
    }

    private void Log(string message) => _log?.Invoke(message);

    private void DrainLog()
    {
        while (_backdashLog.TryDequeue(out string? line)) Log(line);
    }

    private static ulong NewNonce()
    {
        Span<byte> bytes = stackalloc byte[8];
        ulong nonce;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            nonce = BitConverter.ToUInt64(bytes);
        } while (nonce == 0);
        return nonce;
    }

    /// <summary>The match seed both peers derive from the two match ids (P1's first).</summary>
    internal static ulong MixSeed(ulong p1, ulong p2)
    {
        var h = new StateHasher();
        h.Add(p1);
        h.Add(p2);
        return h.Value;
    }

    private static Buttons AllButtons()
    {
        Buttons all = Buttons.None;
        foreach (Buttons b in Enum.GetValues<Buttons>()) all |= b;
        return all;
    }
}
