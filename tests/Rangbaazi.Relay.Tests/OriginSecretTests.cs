using System.Net;
using System.Net.WebSockets;

namespace Rangbaazi.Relay.Tests;

/// <summary>RELAY_ORIGIN_SECRET: only CloudFront (which adds the header) may reach the relay.</summary>
public sealed class OriginSecretTests
{
    private const string Secret = "cloudfront-only-7f3a";

    private static Task<RelayHost> StartAsync() => RelayHost.StartAsync(RelayHost.TestOptions with { OriginSecret = Secret });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong")]
    [InlineData(Secret + "x")]
    [InlineData("cloudfront-only-7f3A")]
    public async Task Health_without_the_right_header_is_403(string? header)
    {
        await using RelayHost host = await StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/relay/health");
        if (header is not null) request.Headers.TryAddWithoutValidation(RelayOptions.OriginSecretHeader, header);
        using HttpResponseMessage response = await host.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Health_with_the_right_header_is_ok()
    {
        await using RelayHost host = await StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/relay/health");
        request.Headers.Add(RelayOptions.OriginSecretHeader, Secret);
        using HttpResponseMessage response = await host.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Repeated_header_is_403()
    {
        await using RelayHost host = await StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/relay/health");
        request.Headers.Add(RelayOptions.OriginSecretHeader, new[] { Secret, Secret });
        using HttpResponseMessage response = await host.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task WebSocket_without_the_right_header_is_refused_with_403(string? header)
    {
        await using RelayHost host = await StartAsync();
        var client = new RelayClient();
        if (header is not null) client.Socket.Options.SetRequestHeader(RelayOptions.OriginSecretHeader, header);
        using (client)
        {
            using var timeout = new CancellationTokenSource(RelayClient.DefaultTimeout);
            await Assert.ThrowsAsync<WebSocketException>(() => client.Socket.ConnectAsync(host.RoomUri("SECRET"), timeout.Token));
            Assert.Equal(HttpStatusCode.Forbidden, client.Socket.HttpStatusCode);
        }
        Assert.Equal(0, host.Server.RoomCount);
    }

    [Fact]
    public async Task WebSocket_with_the_right_header_works()
    {
        await using RelayHost host = await StartAsync();
        RelayClient p1 = await host.ConnectAsync("SECRET", Secret);
        var welcome = await p1.ReceiveJsonAsync("welcome");
        Assert.Equal(0, welcome.GetProperty("slot").GetInt32());
    }
}
