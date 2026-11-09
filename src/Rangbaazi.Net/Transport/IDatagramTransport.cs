using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Rangbaazi.Net.Transport;

/// <summary>
/// Sends and receives raw datagrams: a UDP socket (<see cref="UdpTransport"/>), or the in-memory
/// network used by tests (<see cref="InMemoryNetwork"/>). Later, a relay such as Steam Datagram
/// Relay can implement it. Addresses are IPv4 <see cref="SocketAddress"/>es, which is what
/// Backdash compares byte for byte.
/// </summary>
public interface IDatagramTransport : IDisposable
{
    IPEndPoint LocalEndPoint { get; }

    /// <summary>Sends one datagram. Network errors are not reported: the datagram is just lost, as UDP allows.</summary>
    void Send(ReadOnlySpan<byte> datagram, SocketAddress to);

    /// <summary>
    /// Waits for the next datagram, copies it into <paramref name="buffer"/> and its sender into
    /// <paramref name="from"/>, and returns its length. Throws <see cref="OperationCanceledException"/>
    /// when cancelled or disposed, and nothing else.
    /// </summary>
    ValueTask<int> ReceiveAsync(Memory<byte> buffer, SocketAddress from, CancellationToken cancellationToken);
}
