using System;
using System.Net;
using Godot;
using Rangbaazi.Game.Controls;
using Rangbaazi.Net;
using Rangbaazi.Net.Transport;
using Rangbaazi.Sim;
using Rangbaazi.Sim.Diagnostics;

namespace Rangbaazi.Game.Online;

/// <summary>
/// Drives one online 1v1 from the graybox scene: opens the UDP port, feeds <see cref="OnlineMatch"/>
/// the local player's buttons once per physics tick (P1's keys and pad, whichever side this
/// player is, or a <see cref="SparringBot"/> with <c>--bot</c>), and starts a new match on rematch.
/// Presentation-side only: the sim state comes from the match, never from here.
/// </summary>
public sealed class OnlinePlay : IDisposable
{
    private readonly OnlineLaunchOptions _launch;

    /// <summary>Opens the port; throws <see cref="System.Net.Sockets.SocketException"/> if it's taken.</summary>
    public OnlinePlay(OnlineLaunchOptions launch)
    {
        _launch = launch;
        Match = Create();
        IPAddress? lan = launch.LoopbackOnly ? IPAddress.Loopback : NetworkInfo.LanAddress();
        YourAddress = lan == null ? $"port {launch.Port}" : $"{lan}:{launch.Port}";
    }

    public OnlineMatch Match { get; private set; }

    public OnlineLaunchOptions Launch => _launch;

    /// <summary>Where the other player should point their <c>--peer</c>.</summary>
    public string YourAddress { get; }

    public bool BotPlays => _launch.Bot != null;

    public void Tick()
    {
        OnlineMatch m = Match;
        Buttons local = _launch.Bot is { } level
            ? (m.Phase == OnlinePhase.Running ? SparringBot.Think(m.State, m.Rules, m.LocalSlot, level) : Buttons.None)
            : InputBindings.Read(0);
        m.Tick(local);
    }

    /// <summary>Leaves the current match (asking the opponent for a rematch) and starts a new one.</summary>
    public void Rematch()
    {
        Match.Leave(rematch: Match.Phase != OnlinePhase.Ended);
        Match.Dispose();
        Match = Create();
    }

    public void Dispose() => Match.Dispose();

    private OnlineMatch Create()
    {
        var transport = new UdpTransport(new IPEndPoint(_launch.LoopbackOnly ? IPAddress.Loopback : IPAddress.Any, _launch.Port));
        var options = new OnlineMatchOptions
        {
            Peer = _launch.Peer,
            LocalSlot = _launch.Slot,
            InputDelayFrames = _launch.InputDelay,
        };
        try
        {
            return new OnlineMatch(options, transport, GD.Print);
        }
        catch
        {
            transport.Dispose();
            throw;
        }
    }
}
