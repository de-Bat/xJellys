using System.Text.Json.Nodes;
using Jellyfin.Plugin.Jellycap;
using Jellyfin.Plugin.Jellycap.Overlay;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.Jellycap.Tests;

public class RecapOverlayDriverTests
{
    private const string Device = "d1";
    private const string Session = "s1";

    private readonly FakeTimeProvider _clock = new();
    private readonly List<string> _log = new();
    private readonly List<string> _shownJson = new();
    private readonly RecapOverlayDriver _driver;
    private bool _accept = true;

    public RecapOverlayDriverTests()
    {
        _driver = new RecapOverlayDriver(
            (session, command) =>
            {
                _log.Add($"{command}@{session}");
                return Task.CompletedTask;
            },
            _clock)
        {
            ShowLayout = (device, json) =>
            {
                var id = JsonNode.Parse(json)!["id"]!.GetValue<string>();
                _log.Add($"show:{id}@{device}");
                _shownJson.Add(json);
                return _accept;
            },
            CloseLayout = (device, id) => _log.Add($"close:{id}@{device}"),
        };
    }

    private static OverlayRecap PauseRecap(Guid? item = null) => new()
    {
        ItemId = item ?? Guid.NewGuid(),
        SessionId = Session,
        Mode = OverlayMode.Pause,
        ResumeTicks = 12_345,
        Ready = true,
        Cards = new[] { new OverlayCard("S01E01 · One", "Body one", 0, 5), new OverlayCard("S01E02 · Two", null, 5, 4) },
    };

    private static OverlayRecap TimedRecap(Guid item) => new()
    {
        ItemId = item,
        SessionId = Session,
        Mode = OverlayMode.Timed,
        Ready = true,
        Cards = new[] { new OverlayCard("A", "a", 0, 5), new OverlayCard("B", "b", 30, 6) },
    };

    [Fact]
    public async Task PauseMode_Ready_ShowsThenPauses()
    {
        var shown = await _driver.OnReady(Device, PauseRecap());

        Assert.True(shown);
        Assert.Equal(new[] { "show:recap@d1", "Pause@s1" }, _log);
        Assert.True(_driver.IsHeld(Device));
    }

    [Fact]
    public async Task PauseMode_ShowHappensBeforePause()
    {
        await _driver.OnReady(Device, PauseRecap());

        var show = _log.FindIndex(l => l.StartsWith("show:", StringComparison.Ordinal));
        var pause = _log.IndexOf("Pause@s1");
        Assert.True(show >= 0 && pause > show, string.Join(", ", _log));
    }

    [Fact]
    public async Task PauseMode_HostRejectsShow_NoPause()
    {
        _accept = false;

        var shown = await _driver.OnReady(Device, PauseRecap());

        Assert.False(shown);
        Assert.DoesNotContain("Pause@s1", _log);
        Assert.False(_driver.IsHeld(Device));

        // the fallback timer is not running either
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(new[] { "show:recap@d1" }, _log);
    }

    [Fact]
    public async Task RecapDone_SeeksToResumeThenUnpauses()
    {
        await _driver.OnReady(Device, PauseRecap());
        _log.Clear();

        await _driver.OnAction(Device, "Jellycap/recap", "recapDone");

        Assert.Equal(new[] { "Seek:12345@s1", "Unpause@s1", "close:recap@d1" }, _log);
        Assert.False(_driver.IsHeld(Device));
    }

    [Fact]
    public async Task RecapDone_ClosesOnlyAfterUnpause()
    {
        // R19: the recap layout (suppressTriggers pause/resume/doublePause) stays up until our own Unpause went out,
        // so that Unpause cannot reach JellyXR as a pause-play-pause pattern
        await _driver.OnReady(Device, PauseRecap());
        _log.Clear();

        await _driver.OnClosed(Device, "Jellycap/recap", "finished");

        var unpause = _log.IndexOf("Unpause@s1");
        var close = _log.IndexOf("close:recap@d1");
        Assert.True(unpause >= 0 && close > unpause, string.Join(", ", _log));
    }

    [Fact]
    public async Task RecapDone_Twice_ResumesOnce()
    {
        await _driver.OnReady(Device, PauseRecap());
        _log.Clear();

        await _driver.OnAction(Device, "Jellycap/recap", "recapDone");
        await _driver.OnAction(Device, "Jellycap/recap", "recapDone");
        _clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Single(_log, "Unpause@s1");
    }

    [Fact]
    public async Task OwnPause_IsIgnored()
    {
        await _driver.OnReady(Device, PauseRecap());
        _log.Clear();

        await _driver.OnTrigger(Device, "pause", null, 0);

        Assert.Empty(_log);
        Assert.True(_driver.IsHeld(Device));
    }

    [Fact]
    public async Task ResumeDuringCards_ClosesWithoutSeek()
    {
        await _driver.OnReady(Device, PauseRecap());
        await _driver.OnTrigger(Device, "pause", null, 0);
        _log.Clear();

        await _driver.OnTrigger(Device, "resume", null, 50_000_000);

        Assert.Equal(new[] { "close:recap@d1" }, _log);
        Assert.False(_driver.IsHeld(Device));

        // no late resume from the fallback timer
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(new[] { "close:recap@d1" }, _log);
    }

    [Fact]
    public async Task StopDuringCards_DropsHoldWithoutCommands()
    {
        await _driver.OnReady(Device, PauseRecap());
        _log.Clear();

        await _driver.OnTrigger(Device, "stop", null, 0);
        _clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Empty(_log);
        Assert.False(_driver.IsHeld(Device));
    }

    [Fact]
    public async Task ItemStartOfSameEpisodeAfterShow_ReshowsTheCards()
    {
        var item = Guid.NewGuid();
        await _driver.OnReady(Device, PauseRecap(item));
        _log.Clear();

        // the host saw the episode start only after the cards were shown, and closed them (closeOn itemStart)
        await _driver.OnTrigger(Device, "itemStart", item.ToString("N"), 0);

        Assert.Equal(new[] { "show:recap@d1" }, _log);
        Assert.True(_driver.IsHeld(Device));
    }

    [Fact]
    public async Task NoReleaseFromApp_FallbackResumesAfterTotalPlus30()
    {
        await _driver.OnReady(Device, PauseRecap()); // total 9 s
        _log.Clear();

        _clock.Advance(TimeSpan.FromSeconds(38));
        Assert.Empty(_log);

        _clock.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Equal(new[] { "Seek:12345@s1", "Unpause@s1", "close:recap@d1" }, _log);
        Assert.False(_driver.IsHeld(Device));
    }

    [Fact]
    public async Task TimedMode_ShowsCardAtItsTime()
    {
        var item = Guid.NewGuid();
        Assert.True(await _driver.OnReady(Device, TimedRecap(item)));
        Assert.Empty(_log); // nothing until the recap video starts

        await _driver.OnTrigger(Device, "itemStart", item.ToString("N"), 0);
        Assert.Equal(new[] { "show:recap-card@d1" }, _log);
        Assert.Contains("\"A\"", _shownJson[0], StringComparison.Ordinal);

        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("close:recap-card@d1", _log[^1]);

        _clock.Advance(TimeSpan.FromSeconds(24));
        Assert.Equal(2, _log.Count);

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("show:recap-card@d1", _log[^1]);
        Assert.Contains("\"B\"", _shownJson[^1], StringComparison.Ordinal);

        _clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal("close:recap-card@d1", _log[^1]);
        Assert.Equal(4, _log.Count);
    }

    [Fact]
    public async Task TimedMode_OtherItemStart_DoesNothing()
    {
        await _driver.OnReady(Device, TimedRecap(Guid.NewGuid()));

        await _driver.OnTrigger(Device, "itemStart", Guid.NewGuid().ToString("N"), 0);
        _clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Empty(_log);
    }

    [Fact]
    public async Task TimedMode_Seek_ResyncsFromTriggerPosition()
    {
        var item = Guid.NewGuid();
        await _driver.OnReady(Device, TimedRecap(item));
        await _driver.OnTrigger(Device, "itemStart", item.ToString("N"), 0);
        _log.Clear();
        _shownJson.Clear();

        // seek to 32 s: inside card B (30..36), which shows at once for its remaining 4 s
        await _driver.OnTrigger(Device, "seek", item.ToString("N"), TimeSpan.FromSeconds(32).Ticks);
        Assert.Equal(new[] { "show:recap-card@d1" }, _log);
        Assert.Contains("\"B\"", _shownJson[0], StringComparison.Ordinal);

        // the old schedule is gone: nothing at the old 30 s mark
        _clock.Advance(TimeSpan.FromSeconds(3.5));
        Assert.Single(_log);

        _clock.Advance(TimeSpan.FromSeconds(0.5));
        Assert.Equal("close:recap-card@d1", _log[^1]);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(2, _log.Count);
    }

    [Fact]
    public async Task TimedMode_BackToBackCards_NextIsNotClosedByPreviousEnd()
    {
        var item = Guid.NewGuid();
        var recap = new OverlayRecap
        {
            ItemId = item,
            SessionId = Session,
            Mode = OverlayMode.Timed,
            Ready = true,
            Cards = new[] { new OverlayCard("A", null, 0, 5), new OverlayCard("B", null, 5, 5) },
        };
        await _driver.OnReady(Device, recap);
        await _driver.OnTrigger(Device, "itemStart", item.ToString("N"), 0);

        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("show:recap-card@d1", _log[^1]);
        Assert.Contains("\"B\"", _shownJson[^1], StringComparison.Ordinal);

        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("close:recap-card@d1", _log[^1]);
    }

    [Fact]
    public async Task TimedMode_StopDropsSchedule()
    {
        var item = Guid.NewGuid();
        await _driver.OnReady(Device, TimedRecap(item));
        await _driver.OnTrigger(Device, "itemStart", item.ToString("N"), 0);
        _log.Clear();

        await _driver.OnTrigger(Device, "stop", item.ToString("N"), 0);
        _clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Empty(_log);
    }
}
