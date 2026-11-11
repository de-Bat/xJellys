using System.Text.Json.Nodes;
using Jellyfin.Plugin.JellyTvOverlay.Layout;
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
        var host = new OverlayClientHost(null!, null!, null!, null!, NullLogger<OverlayClientHost>.Instance)
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

    private static string ChosenJson() =>
        $"{{\"type\":\"chosen\",\"deviceId\":\"d1\",\"sessionId\":\"s1\",\"offerId\":\"cast\",\"itemId\":\"{Guid.NewGuid():N}\",\"positionTicks\":1}}";

    [Fact]
    public async Task Chosen_AfterSinglePauseOffer_IsPassthrough()
    {
        // R16: "interactive on pause" keeps play/seek with the player; only the double-pause path takes them
        _cfg.TvPanelInteractive = true;
        var host = NewHost();
        Assert.Equal(Offer, await host.OnEventAsync(Trigger("pause")));
        await host.OnEventAsync(ChosenJson());
        Assert.Equal(CastLayoutBuilder.FocusPassthrough, host.LastFocus);
    }

    [Fact]
    public async Task Chosen_AfterDoublePauseOffer_IsInteractive()
    {
        _cfg.TvPanelInteractive = true;
        var host = NewHost();
        await host.OnEventAsync(Trigger("pause"));
        await host.OnEventAsync(ChosenJson());
        Assert.Equal(Offer, await host.OnEventAsync(Trigger("doublePause")));
        await host.OnEventAsync(ChosenJson());
        Assert.Equal(CastLayoutBuilder.FocusInteractive, host.LastFocus);
    }

    [Fact]
    public async Task Chosen_WithoutOffer_IsInteractive()
    {
        var host = NewHost();
        await host.OnEventAsync(ChosenJson());
        Assert.Equal(CastLayoutBuilder.FocusInteractive, host.LastFocus);
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

    private static string Detail(Guid person) =>
        ActorDetailBuilder.Build(Person(person)!.Value.Person, Array.Empty<LibraryTitle>()).ToJsonString(OverlayJson.Options);

    private static (PersonInfo Person, IReadOnlyList<LibraryTitle> Titles)? Person(Guid person, int titles = 0) =>
        (new PersonInfo(person, "גל גדות", null, null, null),
            Enumerable.Range(0, titles).Select(i => new LibraryTitle(Guid.NewGuid(), "T" + i, 2000 + i, "Movie", null, null, null, null)).ToList());

    private static Task<(PersonInfo Person, IReadOnlyList<LibraryTitle> Titles)?> Loaded(Guid person) => Task.FromResult(Person(person));

    private static string ActorAction(Guid person, string layoutId = "JellyXR/cast") =>
        $"{{\"type\":\"action\",\"deviceId\":\"d1\",\"sessionId\":\"s1\",\"layoutId\":\"{layoutId}\",\"action\":\"actor:{person:N}\",\"elementId\":\"p0\",\"value\":{{}}}}";

    private async Task WaitForShows(int count)
    {
        for (var i = 0; i < 200 && _shows.Count < count; i++)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Preload_ReadyDetailsOnly_ActionShowsTheSameJson()
    {
        var host = NewHost();
        host.PreloadWait = TimeSpan.FromMilliseconds(150);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var slow = new TaskCompletionSource<(PersonInfo Person, IReadOnlyList<LibraryTitle> Titles)?>();
        host.LoadPerson = (p, _, _, _) => p == a ? Loaded(a) : slow.Task;

        var preload = await host.PreloadAsync("d1", new[] { a, b }, "u1", Guid.NewGuid());

        Assert.Single(preload);
        Assert.Equal(Detail(a), preload[0]!.ToJsonString(OverlayJson.Options));

        // the cast layout carrying it passes the host's validator
        var cast = CastLayoutBuilder.Build(new CardSet("Cast", new[] { new Card("Ada", null, PersonId: a), new Card("Bob", null, PersonId: b) }), interactive: true);
        cast["preload"] = preload;
        var v = LayoutValidator.Validate(cast.ToJsonString(), "JellyXR");
        Assert.Equal("JellyXR/actor:" + a.ToString("N"), v.Json["preload"]![0]!["id"]!.GetValue<string>());

        // OK on a preloaded actor: Show with the exact preloaded JSON (the app's cache hits)
        Assert.Null(await host.OnEventAsync(ActorAction(a)));
        Assert.Equal(new[] { Detail(a) }, _shows);

        // not ready in time: shown when its build finishes
        Assert.Null(await host.OnEventAsync(ActorAction(b)));
        Assert.Single(_shows);
        slow.SetResult(Person(b));
        await WaitForShows(2);
        Assert.Equal(new[] { Detail(a), Detail(b) }, _shows);
    }

    [Fact]
    public async Task Preload_AtMostEightBuildsAtOnce()
    {
        var host = NewHost();
        var running = 0;
        var max = 0;
        host.LoadPerson = async (p, _, _, _) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref max, now);
            await Task.Delay(20);
            Interlocked.Decrement(ref running);
            return Person(p);
        };

        var people = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToList();
        var preload = await host.PreloadAsync("d1", people, "u1", Guid.NewGuid());

        Assert.InRange(max, 1, 8);

        // capped at 16 preloads, in panel order
        Assert.Equal(OverlayClientHost.MaxPreloads, preload.Count);
        Assert.Equal(16, OverlayClientHost.MaxPreloads);
        Assert.Equal(people.Take(16).Select(p => "actor:" + p.ToString("N")), preload.Select(p => p!["id"]!.GetValue<string>()));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int cur;
        while ((cur = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, cur) != cur)
        {
        }
    }

    [Fact]
    public async Task Action_UnknownActorOrOtherLayout_ShowsNothing()
    {
        var host = NewHost();
        var a = Guid.NewGuid();
        host.LoadPerson = (p, _, _, _) => Loaded(p);
        await host.PreloadAsync("d1", new[] { a }, "u1", Guid.NewGuid());

        await host.OnEventAsync(ActorAction(Guid.NewGuid()));
        await host.OnEventAsync(ActorAction(a, "JellyXR/other"));
        await host.OnEventAsync(ActorAction(a).Replace("d1", "d2"));
        await Task.Delay(50);
        Assert.Empty(_shows);
    }

    [Fact]
    public async Task Resume_DropsPreloadedDetails()
    {
        var host = NewHost();
        var a = Guid.NewGuid();
        host.LoadPerson = (p, _, _, _) => Loaded(p);
        await host.PreloadAsync("d1", new[] { a }, "u1", Guid.NewGuid());

        await host.OnEventAsync(Trigger("resume"));
        await host.OnEventAsync(ActorAction(a));
        Assert.Empty(_shows);
    }

    [Fact]
    public async Task ClosedActorDetail_KeepsCastInteractive_NoReshow()
    {
        var host = NewHost();
        Assert.True(host.Gate.TryShow("d1", host.Gate.Begin("d1"), interactive: true, () => true));

        // BACK on the detail: the cast panel under it takes the keys again (app focus stack); nothing to show
        await host.OnEventAsync(Closed("back", "JellyXR/actor:" + Guid.NewGuid().ToString("N")));

        Assert.Empty(_shows);
        Assert.False(host.Gate.TryShow("d1", host.Gate.Begin("d1"), interactive: false, () => true));
    }

    [Fact]
    public async Task Trigger_RemembersUserForTheChosenShow()
    {
        var host = NewHost();
        var json = Trigger("doublePause").Replace("\"userId\":\"u1\"", "\"userId\":\"user-7\"");
        await host.OnEventAsync(json);
        Assert.Equal("user-7", host.UserOf("d1"));
    }
    [Fact]
    public async Task InteractiveCastJson_CarriesActorPreloads_TitlesCapped()
    {
        var host = NewHost();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        host.LoadPerson = (p, user, _, _) =>
        {
            Assert.Equal("u1", user);
            return Task.FromResult(Person(p, titles: 30));
        };
        var set = new CardSet("Cast", new[] { new Card("Ada", null, PersonId: a), new Card("Bob", null, PersonId: b), new Card("Cy", null) });

        var json = await host.CastJsonAsync("d1", set, interactive: true, overlaySeconds: 0, itemId: Guid.NewGuid(), userId: "u1");

        var layout = JsonNode.Parse(json)!.AsObject();
        var preload = layout["preload"]!.AsArray();
        Assert.Equal(new[] { "actor:" + a.ToString("N"), "actor:" + b.ToString("N") }, preload.Select(p => p!["id"]!.GetValue<string>()));
        foreach (var p in preload)
        {
            var list = Walk(p!["root"]).Single(n => n["type"]?.GetValue<string>() == "list");
            Assert.Equal(OverlayClientHost.PreloadTitles, list["children"]!.AsArray().Count);
        }

        Assert.Equal(20, OverlayClientHost.PreloadTitles);
        Assert.Contains("גל גדות", json); // Hebrew as UTF-8, not \u escapes
        LayoutValidator.Validate(json, "JellyXR");

        // OK on the actor shows a JSON string equal to the preloaded one
        await host.OnEventAsync(ActorAction(a));
        Assert.Equal(preload[0]!.ToJsonString(OverlayJson.Options), Assert.Single(_shows));
    }

    [Fact]
    public async Task PassiveCastJson_NoPreload_NoBuilds()
    {
        var host = NewHost();
        var calls = 0;
        host.LoadPerson = (p, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Loaded(p);
        };
        var set = new CardSet("Cast", new[] { new Card("Ada", null, PersonId: Guid.NewGuid()) });

        var json = await host.CastJsonAsync("d1", set, interactive: false, overlaySeconds: 10, itemId: Guid.NewGuid(), userId: "u1");

        var layout = JsonNode.Parse(json)!.AsObject();
        Assert.Null(layout["preload"]);
        Assert.Equal(10000, layout["ttlMs"]!.GetValue<int>());
        Assert.Equal(0, calls);
    }

    private static IEnumerable<JsonObject> Walk(JsonNode? node)
    {
        if (node is not JsonObject o)
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
}
