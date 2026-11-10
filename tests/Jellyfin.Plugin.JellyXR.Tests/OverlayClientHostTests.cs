using Jellyfin.Plugin.JellyXR.Configuration;
using Jellyfin.Plugin.JellyXR.Overlay;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyXR.Tests;

public class OverlayClientHostTests
{
    private const string Offer = "{\"offer\":{\"id\":\"cast\",\"label\":\"Cast\"}}";

    private readonly PluginConfiguration _cfg = new() { Enabled = true, TvOverlay = true, ClientFilter = "Android TV" };
    private readonly List<string> _shows = new();
    private bool _hasApp = true;

    private OverlayClientHost NewHost()
    {
        // Library and cards are never reached by these tests (builds start in the background and are counted only).
        var host = new OverlayClientHost(null!, null!, null!, NullLogger<OverlayClientHost>.Instance)
        {
            Config = () => _cfg,
            FindSession = id => id == "s1" ? ("Android TV", "Living room") : null,
        };
        host.AppPresent = _ => _hasApp;
        host.ShowLayout = (d, json) =>
        {
            _shows.Add(json);
            return true;
        };
        return host;
    }

    private static string Trigger(string name, string session = "s1") =>
        $"{{\"type\":\"trigger\",\"trigger\":\"{name}\",\"deviceId\":\"d1\",\"sessionId\":\"{session}\",\"itemId\":\"{Guid.NewGuid():N}\",\"positionTicks\":123,\"userId\":\"u1\"}}";

    [Fact]
    public async Task Pause_Passive_StartsShow_NoOffer()
    {
        var host = NewHost();
        Assert.Null(await host.OnEventAsync(Trigger("pause")));
        Assert.Equal(1, host.ShowsStarted);
    }

    [Fact]
    public async Task Pause_Interactive_ReturnsOffer()
    {
        _cfg.TvPanelInteractive = true;
        var host = NewHost();
        Assert.Equal(Offer, await host.OnEventAsync(Trigger("pause")));
        Assert.Equal(0, host.ShowsStarted);
    }

    [Fact]
    public async Task DoublePause_ReturnsOffer()
    {
        var host = NewHost();
        Assert.Equal(Offer, await host.OnEventAsync(Trigger("doublePause")));
    }

    [Fact]
    public async Task Chosen_StartsShow()
    {
        var host = NewHost();
        var json = $"{{\"type\":\"chosen\",\"deviceId\":\"d1\",\"sessionId\":\"s1\",\"offerId\":\"cast\",\"itemId\":\"{Guid.NewGuid():N}\",\"positionTicks\":1}}";
        Assert.Null(await host.OnEventAsync(json));
        Assert.Equal(1, host.ShowsStarted);
    }

    [Theory]
    [InlineData("tvOff")]
    [InlineData("disabled")]
    [InlineData("noApp")]
    [InlineData("filter")]
    [InlineData("noSession")]
    public async Task Gated_NoOfferNoShow(string why)
    {
        var session = "s1";
        switch (why)
        {
            case "tvOff": _cfg.TvOverlay = false; break;
            case "disabled": _cfg.Enabled = false; break;
            case "noApp": _hasApp = false; break;
            case "filter": _cfg.ClientFilter = "Shield"; break;
            default: session = "other"; break;
        }

        _cfg.TvPanelInteractive = false;
        var host = NewHost();
        Assert.Null(await host.OnEventAsync(Trigger("pause", session)));
        Assert.Null(await host.OnEventAsync(Trigger("doublePause", session)));
        var chosen = $"{{\"type\":\"chosen\",\"deviceId\":\"d1\",\"sessionId\":\"{session}\",\"offerId\":\"cast\",\"itemId\":\"{Guid.NewGuid():N}\",\"positionTicks\":1}}";
        Assert.Null(await host.OnEventAsync(chosen));
        Assert.Equal(0, host.ShowsStarted);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[1,2]")]
    [InlineData("{\"type\":\"trigger\"}")]
    [InlineData("{\"type\":\"trigger\",\"trigger\":\"pause\",\"deviceId\":5}")]
    public async Task Malformed_ReturnsNull_NoThrow(string json)
    {
        var host = NewHost();
        Assert.Null(await host.OnEventAsync(json));
        Assert.Equal(0, host.ShowsStarted);
    }

    private static string Closed(string reason, string layoutId = "JellyXR/cast") =>
        $"{{\"type\":\"closed\",\"deviceId\":\"d1\",\"layoutId\":\"{layoutId}\",\"reason\":\"{reason}\"}}";

    [Fact]
    public async Task ClosedReplaced_KeepsInteractiveGuard()
    {
        var host = NewHost();
        Assert.True(host.Gate.TryShow("d1", host.Gate.Begin("d1"), interactive: true, () => true));

        await host.OnEventAsync(Closed("replaced"));
        await host.OnEventAsync(Closed("back", "JellyXR/other"));
        Assert.False(host.Gate.TryShow("d1", host.Gate.Begin("d1"), interactive: false, () => true));

        await host.OnEventAsync(Closed("back"));
        Assert.True(host.Gate.TryShow("d1", host.Gate.Begin("d1"), interactive: false, () => true));
    }

    [Fact]
    public async Task ResumeTrigger_StalesPendingBuild()
    {
        var host = NewHost();
        var pending = host.Gate.Begin("d1");
        await host.OnEventAsync(Trigger("resume"));
        Assert.False(host.Gate.TryShow("d1", pending, interactive: false, () => true));
    }

    [Fact]
    public async Task OtherTriggers_NoOffer()
    {
        var host = NewHost();
        foreach (var t in new[] { "resume", "stop", "itemStart", "seek" })
        {
            Assert.Null(await host.OnEventAsync(Trigger(t)));
        }

        Assert.Equal(0, host.ShowsStarted);
    }
}
