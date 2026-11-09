using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Rangbaazi.Net.Transport;

/// <summary>
/// A plain IPv4 UDP socket. Unlike Backdash's own socket it binds exclusively, so a second game
/// instance on the same port fails loudly instead of silently sharing the port, and it never
/// reports a network error as fatal: a Wi-Fi blip loses packets, not the match.
/// Bind to <see cref="IPAddress.Loopback"/> for two instances on one PC (no firewall prompt), or to
/// <see cref="IPAddress.Any"/> to accept a player on another PC.
/// </summary>
public sealed class UdpTransport : IDatagramTransport
{
    // Windows reports an ICMP "port unreachable" for an earlier send as an error on the next
    // receive. Turn that off, or a peer that hasn't started yet would keep breaking receives.
    private const int SioUdpConnReset = -1744830452;

    private readonly Socket _socket;
    private volatile bool _disposed;

    public UdpTransport(IPEndPoint bind)
    {
        if (bind.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Only IPv4 addresses are supported.", nameof(bind));

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                _socket.ExclusiveAddressUse = true;
                _socket.IOControl(SioUdpConnReset, new byte[4], null);
            }
            _socket.Bind(bind);
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
        LocalEndPoint = (IPEndPoint)_socket.LocalEndPoint!;
    }

    public UdpTransport(int port, bool loopbackOnly = false)
        : this(new IPEndPoint(loopbackOnly ? IPAddress.Loopback : IPAddress.Any, port))
    {
    }

    public IPEndPoint LocalEndPoint { get; }

    public void Send(ReadOnlySpan<byte> datagram, SocketAddress to)
    {
        if (_disposed) return;
        try
        {
            _socket.SendTo(datagram, SocketFlags.None, to);
        }
        catch (SocketException)
        {
            // Unreachable network, full buffer, ...: the datagram is lost, like any UDP datagram.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, SocketAddress from, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_disposed) throw new OperationCanceledException("The transport is closed.");
            try
            {
                return await _socket.ReceiveFromAsync(buffer, SocketFlags.None, from, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ObjectDisposedException)
            {
                throw new OperationCanceledException("The transport is closed.");
            }
            catch (SocketException) when (!_disposed && !cancellationToken.IsCancellationRequested)
            {
                // An oversized datagram, a reset or a transient network error: skip it. Back off a
                // little so a persistent error can't spin a core.
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                throw new OperationCanceledException("The transport is closed.");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _socket.Dispose();
    }
}
