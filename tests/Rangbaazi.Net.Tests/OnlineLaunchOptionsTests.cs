using System.Net;
using Rangbaazi.Sim.Diagnostics;

namespace Rangbaazi.Net.Tests;

public class OnlineLaunchOptionsTests
{
    [Fact]
    public void NoOnlineFlagMeansAnOfflineGame()
    {
        Assert.False(OnlineLaunchOptions.TryParse(new[] { "--smoke" }, out var options, out string? error));
        Assert.Null(options);
        Assert.Null(error);
    }

    [Fact]
    public void ReadsEverySetting()
    {
        string[] args = { "--online", "--port", "7005", "--peer", "192.168.1.20:7006", "--side", "2", "--delay", "1", "--bot", "hard", "--frames", "900" };
        Assert.True(OnlineLaunchOptions.TryParse(args, out var o, out string? error), error);
        Assert.Equal(7005, o!.Port);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.168.1.20"), 7006), o.Peer);
        Assert.Equal(1, o.Slot);
        Assert.Equal(1, o.InputDelay);
        Assert.Equal(CpuLevel.Hard, o.Bot);
        Assert.Equal(900, o.SmokeFrames);
        Assert.False(o.LoopbackOnly);
    }

    [Fact]
    public void DefaultsAndLoopback()
    {
        Assert.True(OnlineLaunchOptions.TryParse(new[] { "--online", "--peer", "localhost:7001", "--side", "1" }, out var o, out _));
        Assert.Equal(OnlineLaunchOptions.DefaultPort, o!.Port);
        Assert.Equal(2, o.InputDelay);
        Assert.Null(o.Bot);
        Assert.Equal(0, o.SmokeFrames);
        Assert.True(o.LoopbackOnly);
        Assert.Equal(IPAddress.Loopback, o.Peer.Address);
    }

    [Theory]
    [InlineData("--online", "--side", "1")]                                    // no peer
    [InlineData("--online", "--peer", "10.0.0.2:7000")]                        // no side
    [InlineData("--online", "--peer", "10.0.0.2:7000", "--side", "3")]
    [InlineData("--online", "--peer", "10.0.0.2", "--side", "1")]              // no port
    [InlineData("--online", "--peer", "10.0.0.2:70000", "--side", "1")]
    [InlineData("--online", "--peer", "[::1]:7000", "--side", "1")]            // IPv6
    [InlineData("--online", "--peer", "10.0.0.2:7000", "--side", "1", "--port", "0")]
    [InlineData("--online", "--peer", "10.0.0.2:7000", "--side", "1", "--delay", "9")]
    [InlineData("--online", "--peer", "10.0.0.2:7000", "--side", "1", "--bot", "godlike")]
    [InlineData("--online", "--peer", "10.0.0.2:7000", "--side")]              // value missing
    public void BadSettingsAreExplained(params string[] args)
    {
        Assert.False(OnlineLaunchOptions.TryParse(args, out var options, out string? error));
        Assert.Null(options);
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Contains("--peer", error);
    }
}
