using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Rangbaazi.Sim.Diagnostics;

namespace Rangbaazi.Net;

/// <summary>
/// Online play as asked for on the command line (after Godot's <c>--</c>):
/// <code>--online --port 7000 --peer 192.168.1.20:7000 --side 1 [--delay 2] [--bot normal] [--frames 900]</code>
/// <c>--side</c> is 1 (P1) or 2 (P2) and must differ between the two players. <c>--bot</c> lets a
/// sparring bot play this side (for soak tests). <c>--frames</c> runs the online smoke test: quit
/// with exit code 0 once that many frames are final and match the opponent.
/// </summary>
public sealed record OnlineLaunchOptions
{
    public const int DefaultPort = 7000;

    public int Port { get; init; } = DefaultPort;
    public required IPEndPoint Peer { get; init; }
    public required int Slot { get; init; }
    public int InputDelay { get; init; } = 2;
    public CpuLevel? Bot { get; init; }
    public int SmokeFrames { get; init; }

    /// <summary>Bind 127.0.0.1 only: both players on this PC, and no firewall prompt.</summary>
    public bool LoopbackOnly => IPAddress.IsLoopback(Peer.Address);

    public const string Usage =
        "--online --port <your port> --peer <their IPv4 or host>:<their port> --side <1|2> [--delay 0-7] [--bot easy|normal|hard] [--frames N]";

    /// <summary>
    /// False with no error when <c>--online</c> isn't there (an offline game); false with an error
    /// when it is but something is wrong.
    /// </summary>
    public static bool TryParse(IReadOnlyList<string> args, out OnlineLaunchOptions? options, out string? error)
    {
        options = null;
        error = null;
        if (!args.Contains("--online")) return false;

        int port = DefaultPort, delay = 2, frames = 0, side = 0;
        IPEndPoint? peer = null;
        CpuLevel? bot = null;
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg == "--online") continue;
            string? value = i + 1 < args.Count ? args[i + 1] : null;
            bool ok = arg switch
            {
                "--port" => TryInt(value, 1, 65535, out port),
                "--peer" => TryParseEndPoint(value, out peer, out error),
                "--side" => TryInt(value, 1, 2, out side),
                "--delay" => TryInt(value, 0, 7, out delay),
                "--frames" => TryInt(value, 1, int.MaxValue, out frames),
                "--bot" => TryLevel(value, out bot),
                _ => true, // someone else's argument (e.g. --smoke); not ours to judge
            };
            if (!ok)
            {
                error ??= $"Bad value for {arg}: '{value}'.";
                error += " Usage: " + Usage;
                return false;
            }
            if (arg is "--port" or "--peer" or "--side" or "--delay" or "--frames" or "--bot") i++;
        }

        if (peer == null || side == 0)
        {
            error = "Online play needs --peer and --side. Usage: " + Usage;
            return false;
        }
        options = new OnlineLaunchOptions { Port = port, Peer = peer, Slot = side - 1, InputDelay = delay, Bot = bot, SmokeFrames = frames };
        return true;
    }

    /// <summary>"192.168.1.20:7000", "localhost:7001" or "my-pc:7000" (resolved to its first IPv4 address).</summary>
    public static bool TryParseEndPoint(string? text, out IPEndPoint? endPoint, out string? error)
    {
        endPoint = null;
        error = null;
        int colon = text?.LastIndexOf(':') ?? -1;
        if (text == null || colon <= 0 || !TryInt(text[(colon + 1)..], 1, 65535, out int port))
        {
            error = $"'{text}' isn't an address like 192.168.1.20:7000.";
            return false;
        }
        string host = text[..colon];
        IPAddress? address = null;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) address = IPAddress.Loopback;
        else if (IPAddress.TryParse(host, out IPAddress? parsed))
        {
            if (parsed.AddressFamily != AddressFamily.InterNetwork)
            {
                error = $"'{host}' is IPv6; use the other PC's IPv4 address.";
                return false;
            }
            address = parsed;
        }
        else
        {
            try
            {
                address = Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            }
            catch (SocketException)
            {
            }
            if (address == null)
            {
                error = $"Couldn't find an IPv4 address for '{host}'.";
                return false;
            }
        }
        endPoint = new IPEndPoint(address, port);
        return true;
    }

    private static bool TryInt(string? text, int min, int max, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;

    private static bool TryLevel(string? text, out CpuLevel? level)
    {
        level = null;
        if (!Enum.TryParse(text, ignoreCase: true, out CpuLevel parsed) || !Enum.IsDefined(parsed)) return false;
        level = parsed;
        return true;
    }
}

public static class NetworkInfo
{
    /// <summary>
    /// This PC's LAN IPv4 address (the one other PCs on the network reach it at), or null. Asks the
    /// OS which interface would route to the internet; nothing is sent.
    /// </summary>
    public static IPAddress? LanAddress()
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 9)); // TEST-NET-1: never routed anywhere real
            return (probe.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException)
        {
            return null;
        }
    }
}
