using System.Text.Json.Nodes;
using Jellyfin.Plugin.JellySubSync.Configuration;
using Jellyfin.Plugin.JellySubSync.Overlay;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.JellySubSync.Tests;

public class OverlayClientHostTests
{
    private const string Offer = "{\"offer\":{\"id\":\"sync\",\"label\":\"Sync subtitles\"}}";

    private static readonly Guid Item = Guid.NewGuid();
    private readonly PluginConfiguration _cfg = new() { Scope = SyncScope.All, MaxOffsetSeconds = 600 };
    private readonly FakeTimeProvider _clock = new();
    private readonly List<string> _shows = new();
    private readonly List<(string Session, SessionCommand Cmd)> _commands = new();
    private readonly List<(Guid Item, string? Source, int Index, long Offset)> _applies = new();
    private bool _hasApp = true;
    private bool _allowed = true;
    private bool _admin = true;
    private int? _sub = 3;
    private bool _canSet = true;
    private int _stillPlayingPolls;
    private Func<Task<int>> _applyResult = () => Task.FromResult(7);
    private TrackState? _track = new TrackState(true, null, 0, "English (SRT)");

    private OverlayClientHost NewHost()
    {
        var host = new OverlayClientHost(null!, null!, null!, null!, NullLogger<OverlayClientHost>.Instance)
        {
            Clock = _clock,
            Config = () => _cfg,
            Allowed = _ => _allowed,
            IsAdmin = _ => _admin,
            FindSession = id =>
            {
                if (id != "s1")
                {
                    return null;
                }

                // after Stop: the session still reports the item for a few polls
                var playing = _commands.Any(c => c.Cmd.Name == "Stop") ? _stillPlayingPolls-- > 0 : true;
                return new SessionView("s1", playing ? Item : null, _sub, "ms1", _canSet);
            },
            GetTrack = (_, _, _) => _track,
            Apply = (item, source, index, offset, _) =>
            {
                lock (_applies)
                {
                    _applies.Add((item, source, index, offset));
                }

                return _applyResult();
            },
            SendCommand = (session, cmd) =>
            {
                lock (_commands)
                {
                    _commands.Add((session, cmd));
                }

                return Task.CompletedTask;
            },
        };
        host.AppPresent = _ => _hasApp;
        host.ShowLayout = (d, json) =>
        {
            lock (_shows)
            {
                _shows.Add(json);
            }

            return true;
        };
        return host;
    }

    private static string Trigger(string name) =>
        $"{{\"type\":\"trigger\",\"trigger\":\"{name}\",\"deviceId\":\"d1\",\"sessionId\":\"s1\",\"itemId\":\"{Item:N}\",\"positionTicks\":123,\"userId\":\"u1\"}}";

    private static string Chosen(string offer = "sync") =>
        $"{{\"type\":\"chosen\",\"deviceId\":\"d1\",\"sessionId\":\"s1\",\"offerId\":\"{offer}\",\"itemId\":\"{Item:N}\",\"positionTicks\":123}}";

    private static string Apply(long offset) =>
        $"{{\"type\":\"action\",\"deviceId\":\"d1\",\"sessionId\":\"s1\",\"layoutId\":\"JellySubSync/box\",\"action\":\"apply\",\"elementId\":null,\"value\":{{\"offset\":{offset}}}}}";

    private static IEnumerable<JsonObject> Walk(JsonNode? n)
    {
        if (n is not JsonObject o)
        {
            yield break;
        }

        yield return o;
        if (o["children"] is JsonArray kids)
        {
            foreach (var d in kids.SelectMany(Walk))
            {
                yield return d;
            }
        }
    }

    private static List<string> TextsOf(string json) =>
        Walk(JsonNode.Parse(json)!["root"]).Where(o => o["type"]?.GetValue<string>() == "text").Select(o => o["text"]!.GetValue<string>()).ToList();

    private string LastText()
    {
        lock (_shows)
        {
            return TextsOf(_shows[^1]).Last();
        }
    }

    private async Task OpenBox(OverlayClientHost host)
    {
        Assert.Equal(Offer, await host.OnEventAsync(Trigger("doublePause")));
        Assert.Null(await host.OnEventAsync(Chosen()));
        await host.Idle();
    }

    private async Task RunApply(OverlayClientHost host, long offset)
    {
        Assert.Null(await host.OnEventAsync(Apply(offset)));
        await host.Idle();
        var t = host.LastApply!;
        for (var i = 0; i < 200 && !t.IsCompleted; i++)
        {
            await Task.Delay(5);
            _clock.Advance(TimeSpan.FromMilliseconds(300));
        }

        await t;
    }

    [Fact]
    public async Task DoublePause_Offers_WhenAllowedAndApp()
    {
        var host = NewHost();
        Assert.Equal(Offer, await host.OnEventAsync(Trigger("doublePause")));
    }

    [Theory]
    [InlineData("noApp")]
    [InlineData("scope")]
    [InlineData("disabledScope")]
    [InlineData("noConfig")]
    public async Task DoublePause_Gated_NoOffer(string why)
    {
        switch (why)
        {
            case "noApp": _hasApp = false; break;
            case "scope": _allowed = false; break;
            case "disabledScope": _cfg.Scope = SyncScope.Off; _allowed = false; break;
        }

        var host = NewHost();
        if (why == "noConfig")
        {
            host.Config = () => null;
        }

        Assert.Null(await host.OnEventAsync(Trigger("doublePause")));
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("itemStart")]
    [InlineData("seek")]
    public async Task OtherTriggers_NoOffer(string trigger)
    {
        var host = NewHost();
        Assert.Null(await host.OnEventAsync(Trigger(trigger)));
        await host.Idle();
        Assert.Empty(_shows);
    }

    [Fact]
    public async Task DoublePause_AdminsOnly_NonAdmin_NoOffer()
    {
        _cfg.AdminsOnly = true;
        _admin = false;
        var host = NewHost();
        Assert.Null(await host.OnEventAsync(Trigger("doublePause")));
    }

    [Fact]
    public async Task Chosen_ShowsBox()
    {
        _track = new TrackState(true, null, 1250, "English (SRT)");
        var host = NewHost();
        await OpenBox(host);

        var box = JsonNode.Parse(Assert.Single(_shows))!;
        Assert.Equal("box", box["id"]!.GetValue<string>());
        Assert.Equal("interactive", box["focus"]!.GetValue<string>());
        var v = Walk(box["root"]).Single(o => o["type"]?.GetValue<string>() == "value");
        Assert.Equal(1250, v["value"]!.GetValue<long>());
        Assert.Equal(600000, v["max"]!.GetValue<long>());
        Assert.Contains("English (SRT)", TextsOf(_shows[0]));
    }

    [Fact]
    public async Task Chosen_OtherOffer_Ignored()
    {
        var host = NewHost();
        Assert.Null(await host.OnEventAsync(Chosen("cast")));
        await host.Idle();
        Assert.Empty(_shows);
    }

    [Fact]
    public async Task Chosen_NoSubtitleTrack_ShowsHint()
    {
        _sub = null;
        var host = NewHost();
        await OpenBox(host);

        Assert.Equal("Turn on a subtitle track in the player first.", LastText());
        Assert.Equal("passive", JsonNode.Parse(_shows[0])!["focus"]!.GetValue<string>());
    }

    [Fact]
    public async Task Chosen_Unsupported_ShowsReason()
    {
        _track = new TrackState(false, "Picture subtitles (pgssub) can't be shifted. Pick a text track (SRT, ASS).", 0, "English");
        var host = NewHost();
        await OpenBox(host);

        Assert.Equal("Picture subtitles (pgssub) can't be shifted. Pick a text track (SRT, ASS).", LastText());
    }

    [Fact]
    public async Task Apply_Sequence_StopWaitDone_ThenPendingSwitch()
    {
        _stillPlayingPolls = 2;
        var host = NewHost();
        await OpenBox(host);
        await RunApply(host, 250);

        Assert.Equal((Item, (string?)"ms1", 3, 250L), Assert.Single(_applies));
        var texts = _shows.Skip(1).Select(s => TextsOf(s).Last()).ToList();
        Assert.Equal(new[] { "Syncing subtitles…", "Stopping playback…", "Subtitles +0.25 s. Press play to resume." }, texts);
        Assert.Equal(0, JsonNode.Parse(_shows[1])!["ttlMs"]!.GetValue<int>()); // busy
        Assert.Equal(new[] { "itemStart" }, JsonNode.Parse(_shows[^1])!["closeOn"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(("s1", new SessionCommand("Stop", null)), Assert.Single(_commands));
        Assert.True(_stillPlayingPolls < 0); // waited until the session reported nothing playing

        // the viewer resumes from the item page: the player starts on the original track
        await host.OnProgressAsync("d1", "s1", Item, false, 3);
        _clock.Advance(TimeSpan.FromMilliseconds(2500));
        await host.OnProgressAsync("d1", "s1", Item, false, 3);
        Assert.Equal(("s1", new SessionCommand("SetSubtitleStreamIndex", 7)), _commands[^1]);
        await host.OnProgressAsync("d1", "s1", Item, false, 7);
        Assert.False(host.HasPending("d1"));
        Assert.DoesNotContain(_commands, c => c.Cmd.Name is not ("Stop" or "SetSubtitleStreamIndex"));
    }

    [Fact]
    public async Task Apply_Zero_BackToOriginal()
    {
        var host = NewHost();
        await OpenBox(host);
        await RunApply(host, 0);

        var texts = _shows.Skip(1).Select(s => TextsOf(s).Last()).ToList();
        Assert.Equal(new[] { "Back to the original subtitles…", "Stopping playback…", "Subtitles 0 s (original). Press play to resume." }, texts);
    }

    [Fact]
    public async Task Apply_StopWait_GivesUpAfterStopWaitMs()
    {
        _stillPlayingPolls = int.MaxValue;
        var host = NewHost();
        await OpenBox(host);
        var start = _clock.GetUtcNow();
        await RunApply(host, 500);

        var waited = _clock.GetUtcNow() - start;
        Assert.InRange(waited.TotalMilliseconds, OverlayClientHost.StopWaitMs, OverlayClientHost.StopWaitMs + 600);
        Assert.Equal("Subtitles +0.5 s. Press play to resume.", LastText());
        Assert.True(host.HasPending("d1"));
    }

    [Fact]
    public async Task Apply_Failure_ShowsExceptionText_NoStop()
    {
        _applyResult = () => throw new SubSyncException("The subtitle file is missing on the server.");
        var host = NewHost();
        await OpenBox(host);
        await RunApply(host, 250);

        Assert.Equal("The subtitle file is missing on the server.", LastText());
        Assert.Empty(_commands);
        Assert.False(host.HasPending("d1"));
    }

    [Fact]
    public async Task Apply_UnexpectedFailure_ShowsServerText()
    {
        _applyResult = () => throw new InvalidOperationException("boom");
        var host = NewHost();
        await OpenBox(host);
        await RunApply(host, 250);

        Assert.Equal("Subtitle sync failed on the server: boom", LastText());
    }

    [Fact]
    public async Task Apply_WithoutBox_Ignored()
    {
        var host = NewHost();
        Assert.Null(await host.OnEventAsync(Apply(250)));
        await host.Idle();
        Assert.Null(host.LastApply);
        Assert.Empty(_applies);
    }

    [Fact]
    public async Task Pending_GiveUp_ShowsPickHint()
    {
        _canSet = false;
        var host = NewHost();
        await OpenBox(host);
        await RunApply(host, 250);

        await host.OnProgressAsync("d1", "s1", Item, false, 3);
        Assert.Equal("Pick the \"Synced\" track in the player's subtitle menu.", LastText());
        Assert.False(host.HasPending("d1"));
        Assert.DoesNotContain(_commands, c => c.Cmd.Name == "SetSubtitleStreamIndex");
    }

    [Fact]
    public async Task Pending_OtherDevice_Ignored()
    {
        var host = NewHost();
        await OpenBox(host);
        await RunApply(host, 250);

        await host.OnProgressAsync("d2", "s9", Item, false, 3);
        _clock.Advance(TimeSpan.FromMilliseconds(5000));
        await host.OnProgressAsync("d2", "s9", Item, false, 3);
        Assert.True(host.HasPending("d1"));
        Assert.DoesNotContain(_commands, c => c.Cmd.Name == "SetSubtitleStreamIndex");
    }

    [Fact]
    public async Task DoublePause_WhilePending_NoOffer()
    {
        var host = NewHost();
        await OpenBox(host);
        await RunApply(host, 250);

        Assert.Null(await host.OnEventAsync(Trigger("doublePause")));
    }

    [Fact]
    public void NeverPlayNow_CommandNames()
    {
        Assert.Equal(new[] { "Stop", "SetSubtitleStreamIndex" }, OverlayClientHost.CommandNames);
    }
}
