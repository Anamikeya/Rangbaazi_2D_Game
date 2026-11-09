using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Rangbaazi.Relay.Tests;

/// <summary>The real relay app on a free loopback port, plus helpers to connect players to it.</summary>
internal sealed class RelayHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly List<RelayClient> _clients = new();

    private RelayHost(WebApplication app, Uri httpBase)
    {
        _app = app;
        HttpBase = httpBase;
        WsBase = new UriBuilder(httpBase) { Scheme = "ws" }.Uri;
        Http = new HttpClient { BaseAddress = httpBase, Timeout = RelayClient.DefaultTimeout };
        Server = app.Services.GetRequiredService<RelayServer>();
    }

    public Uri HttpBase { get; }

    public Uri WsBase { get; }

    public HttpClient Http { get; }

    public RelayServer Server { get; }

    /// <summary>Test defaults: loopback, port 0, short close and shutdown timeouts.</summary>
    public static RelayOptions TestOptions => new()
    {
        Port = 0,
        ListenAddress = IPAddress.Loopback,
        CloseTimeout = TimeSpan.FromSeconds(3),
        ShutdownTimeout = TimeSpan.FromSeconds(2),
    };

    public static async Task<RelayHost> StartAsync(RelayOptions? options = null)
    {
        WebApplication app = RelayApp.Build(options ?? TestOptions);
        await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new RelayHost(app, new Uri(address));
    }

    public Uri RoomUri(string code) => new(WsBase, "relay/room/" + code);

    /// <summary>Connects to a room. The caller reads the server's first message (welcome, full, error or busy).</summary>
    public async Task<RelayClient> ConnectAsync(string code, string? originSecret = null)
    {
        var client = new RelayClient();
        if (originSecret is not null) client.Socket.Options.SetRequestHeader(RelayOptions.OriginSecretHeader, originSecret);
        lock (_clients) _clients.Add(client);
        using var timeout = new CancellationTokenSource(RelayClient.DefaultTimeout);
        await client.Socket.ConnectAsync(RoomUri(code), timeout.Token);
        return client;
    }

    /// <summary>
    /// Connects a player whose TCP receive buffer is tiny, for tests where it then never reads: the
    /// server's sends to it soon stop completing, like a frozen or very slow phone.
    /// </summary>
    public async Task<RelayClient> ConnectStalledAsync(string code)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp)
                {
                    NoDelay = true,
                    ReceiveBufferSize = 1024,
                };
                try
                {
                    await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
                    return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        var client = new RelayClient(new HttpMessageInvoker(handler, disposeHandler: true));
        lock (_clients) _clients.Add(client);
        using var timeout = new CancellationTokenSource(RelayClient.DefaultTimeout);
        await client.Socket.ConnectAsync(RoomUri(code), client.Invoker, timeout.Token);
        return client;
    }

    /// <summary>Connects and reads the welcome message, checking its slot and room.</summary>
    public async Task<RelayClient> JoinAsync(string code, int expectedSlot)
    {
        RelayClient client = await ConnectAsync(code);
        var welcome = await client.ReceiveJsonAsync("welcome");
        Assert.Equal(expectedSlot, welcome.GetProperty("slot").GetInt32());
        Assert.Equal(code.ToUpperInvariant(), welcome.GetProperty("room").GetString());
        return client;
    }

    /// <summary>Two players in one room, both past welcome and start. Returns the shared seed.</summary>
    public async Task<(RelayClient P1, RelayClient P2, long Seed)> PairAsync(string code)
    {
        RelayClient p1 = await JoinAsync(code, 0);
        RelayClient p2 = await JoinAsync(code, 1);
        long s1 = await p1.ReceiveStartAsync(0);
        long s2 = await p2.ReceiveStartAsync(1);
        Assert.Equal(s1, s2);
        return (p1, p2, s1);
    }

    /// <summary>Polls a server-side condition (no fixed sleeps): fails after the default timeout.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow + RelayClient.DefaultTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting for " + what);
            await Task.Delay(10);
        }
    }

    /// <summary>Stops the app the way systemd would (SIGTERM): connections are asked to close first.</summary>
    public Task StopAsync() => _app.StopAsync();

    public async ValueTask DisposeAsync()
    {
        RelayClient[] clients;
        lock (_clients) clients = _clients.ToArray();
        foreach (RelayClient client in clients) client.Socket.Abort();
        foreach (RelayClient client in clients) client.Dispose();
        Http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
