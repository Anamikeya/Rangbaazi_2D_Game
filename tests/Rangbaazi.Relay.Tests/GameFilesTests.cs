using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;

namespace Rangbaazi.Relay.Tests;

/// <summary>WEB_ROOT: the relay serving the game's web build, with gzip copies of the big files.</summary>
public sealed class GameFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rangbaazi-web-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _wasm = Enumerable.Range(0, 5000).Select(i => (byte)(i % 7)).ToArray();

    public GameFilesTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "index.html"), "<html>game</html>");
        File.WriteAllBytes(Path.Combine(_root, "index.wasm"), _wasm);
        using (FileStream gz = File.Create(Path.Combine(_root, "index.wasm.gz")))
        using (var zip = new GZipStream(gz, CompressionLevel.Optimal))
            zip.Write(_wasm);
        File.WriteAllBytes(Path.Combine(_root, "index.pck"), new byte[] { 1, 2, 3 });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private Task<RelayHost> Start() => RelayHost.StartAsync(RelayHost.TestOptions with { WebRoot = _root });

    [Fact]
    public async Task ThePageIsServedAtTheRootAndNeverCached()
    {
        await using RelayHost host = await Start();
        HttpResponseMessage page = await host.Http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType!.MediaType);
        Assert.Equal("<html>game</html>", await page.Content.ReadAsStringAsync());
        Assert.True(page.Headers.CacheControl!.NoCache);
    }

    [Fact]
    public async Task TheWasmGoesOutGzippedToBrowsersThatAcceptIt()
    {
        await using RelayHost host = await Start();
        var request = new HttpRequestMessage(HttpMethod.Get, "/index.wasm");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
        HttpResponseMessage response = await host.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/wasm", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("gzip", response.Content.Headers.ContentEncoding);
        using var unzip = new GZipStream(await response.Content.ReadAsStreamAsync(), CompressionMode.Decompress);
        using var bytes = new MemoryStream();
        await unzip.CopyToAsync(bytes);
        Assert.Equal(_wasm, bytes.ToArray());
    }

    [Fact]
    public async Task WithoutGzipTheWasmGoesOutAsIs()
    {
        await using RelayHost host = await Start();
        var refused = new HttpRequestMessage(HttpMethod.Get, "/index.wasm");
        refused.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip;q=0, identity");
        foreach (HttpRequestMessage request in new[] { new HttpRequestMessage(HttpMethod.Get, "/index.wasm"), refused })
        {
            HttpResponseMessage response = await host.Http.SendAsync(request);
            Assert.Equal("application/wasm", response.Content.Headers.ContentType!.MediaType);
            Assert.Empty(response.Content.Headers.ContentEncoding);
            Assert.Equal(_wasm, await response.Content.ReadAsByteArrayAsync());
        }
    }

    [Fact]
    public async Task GodotFileTypesAndMissingFiles()
    {
        await using RelayHost host = await Start();
        HttpResponseMessage pck = await host.Http.GetAsync("/index.pck");
        Assert.Equal("application/octet-stream", pck.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Http.GetAsync("/nothing.js")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Http.GetAsync("/index.wasm.gz.gz")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Http.GetAsync("/%2e%2e/secret.txt")).StatusCode);
    }

    [Fact]
    public async Task TheRelayStillWorksBesideTheFiles()
    {
        await using RelayHost host = await Start();
        Assert.Equal("ok", await host.Http.GetStringAsync(RelayApp.HealthPath));
        RelayClient a = await host.ConnectAsync("FILES");
        await a.ReceiveJsonAsync("welcome");
    }

    [Fact]
    public async Task WithoutWebRootNothingIsServed()
    {
        await using RelayHost host = await RelayHost.StartAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await host.Http.GetAsync("/")).StatusCode);
        await using RelayHost missing = await RelayHost.StartAsync(RelayHost.TestOptions with { WebRoot = _root + "-missing" });
        Assert.Equal(HttpStatusCode.NotFound, (await missing.Http.GetAsync("/")).StatusCode);
    }

    [Fact]
    public void WebRootComesFromTheEnvironment()
    {
        RelayOptions options = RelayOptions.FromEnvironment(name => name == RelayOptions.WebRootVariable ? " /opt/web " : null);
        Assert.Equal("/opt/web", options.WebRoot);
        Assert.Null(RelayOptions.FromEnvironment(_ => null).WebRoot);
    }
}
