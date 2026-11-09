using System;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Rangbaazi.Relay;

/// <summary>Builds the relay web app: health check, origin-secret gate and the room WebSocket endpoint.</summary>
public static class RelayApp
{
    public const string HealthPath = "/relay/health";
    public const string RoomPathPrefix = "/relay/room/";

    public static WebApplication Build(RelayOptions options, string[]? args = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = args ?? Array.Empty<string>() });

        // Counts and events only. Framework logs stay at Warning: its request and connection logs carry
        // paths (room codes) and, at Debug, client addresses.
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.UseUtcTimestamp = true;
            o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
        });
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(options.ListenAddress, options.Port, listen => listen.Protocols = HttpProtocols.Http1);
        });
        builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = options.ShutdownTimeout);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(sp => new RelayServer(options, sp.GetRequiredService<ILogger<RelayServer>>()));

        WebApplication app = builder.Build();
        RelayServer relay = app.Services.GetRequiredService<RelayServer>();
        app.Lifetime.ApplicationStopping.Register(relay.CloseAll);

        if (options.OriginSecret is { Length: > 0 } secret)
        {
            var check = new OriginSecretCheck(secret);
            app.Use((HttpContext context, RequestDelegate next) =>
            {
                StringValues provided = context.Request.Headers[RelayOptions.OriginSecretHeader];
                if (provided.Count == 1 && check.Matches(provided[0])) return next(context);
                relay.LogOriginRejected();
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            });
        }
        else
        {
            relay.LogOriginCheckDisabled();
        }

        if (options.WebRoot is { Length: > 0 } webRoot)
        {
            if (GameFiles.CanServe(webRoot)) GameFiles.Use(app, webRoot);
            else relay.LogWebRootMissing();
        }

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = options.KeepAliveInterval });

        RequestDelegate health = context =>
        {
            context.Response.ContentType = "text/plain; charset=utf-8";
            return context.Response.WriteAsync("ok");
        };
        app.MapGet(HealthPath, health);

        // Catch-all so that any code (even an empty or multi-segment one) gets the protocol's error + 4000.
        RequestDelegate room = context => HandleRoomAsync(context, relay);
        app.Map(RoomPathPrefix + "{**code}", room);

        relay.LogStarted(options.Port, options.MaxRooms, options.MaxConnections, options.OriginSecret is null ? "off" : "on");
        return app;
    }

    private static async Task HandleRoomAsync(HttpContext context, RelayServer relay)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        string? rawCode = context.Request.RouteValues["code"] as string;
        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);

        if (!RoomCode.TryNormalize(rawCode, out string code))
        {
            relay.LogJoinRejected("bad room code", relay.RoomCount, relay.ActiveConnections);
            await relay.RejectAsync(socket, RelayMessages.Error("Room code must be 4 to 8 letters or digits."),
                RelayCloseCodes.BadCode, "bad room code").ConfigureAwait(false);
            return;
        }

        if (!relay.TryReserveConnection())
        {
            relay.LogJoinRejected("connection limit", relay.RoomCount, relay.ActiveConnections);
            await relay.RejectAsync(socket, RelayMessages.Busy, RelayCloseCodes.Busy, "server busy").ConfigureAwait(false);
            return;
        }

        try
        {
            await new RelayConnection(socket, relay).RunAsync(code).ConfigureAwait(false);
        }
        finally
        {
            relay.ReleaseConnection();
        }
    }
}
