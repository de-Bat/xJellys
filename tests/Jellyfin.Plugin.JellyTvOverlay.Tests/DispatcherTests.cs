using System.Text.Json.Nodes;
using Jellyfin.Plugin.JellyTvOverlay.Delivery;
using Jellyfin.Plugin.JellyTvOverlay.Host;
using Jellyfin.Plugin.JellyTvOverlay.Triggers;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.JellyTvOverlay.Tests;

public class DispatcherTests
{
    private const string Dev = "dev1";
    private static readonly Guid Item = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private sealed class FakeClient
    {
        private readonly List<JsonObject> _events = new();

        public string? OfferId { get; set; }

        public string? OfferLabel { get; set; }

        public TimeSpan? Delay { get; set; }

        public TimeProvider? Clock { get; set; }

        public bool ThrowSync { get; set; }

        public bool Fault { get; set; }

        public IReadOnlyList<JsonObject> Events
        {
            get
            {
                lock (_events)
                {
                    return _events.ToList();
                }
            }
        }

        public IEnumerable<JsonObject> OfType(string type) => Events.Where(e => (string?)e["type"] == type);

        public IEnumerable<JsonObject> OfType(string type, string trigger) => OfType(type).Where(e => (string?)e["trigger"] == trigger);

        public Func<string, Task<string?>> Handler => Handle;

        private Task<string?> Handle(string json)
        {
            lock (_events)
            {
                _events.Add(JsonNode.Parse(json)!.AsObject());
            }

            if (ThrowSync)
            {
                throw new InvalidOperationException("boom");
            }

            if (Fault)
            {
                return Task.FromException<string?>(new InvalidOperationException("faulted"));
            }

            var isTrigger = json.Contains("\"type\":\"trigger\"", StringComparison.Ordinal);
            string? answer = isTrigger && OfferId is not null
                ? new JsonObject { ["offer"] = new JsonObject { ["id"] = OfferId, ["label"] = OfferLabel ?? OfferId } }.ToJsonString()
                : null;
            if (isTrigger && Delay is { } d)
            {
                return DelayThen(d, answer);
            }

            return Task.FromResult(answer);
        }

        private async Task<string?> DelayThen(TimeSpan d, string? answer)
        {
            await Task.Delay(d, Clock!).ConfigureAwait(false);
            return answer;
        }
    }

    private static (Dispatcher D, OverlayQueue Q, FakeTimeProvider C, List<string> Log) Make()
    {
        var clock = new FakeTimeProvider();
        var q = new OverlayQueue(clock);
        var log = new List<string>();
        var d = new Dispatcher(q, clock, s => { lock (log) { log.Add(s); } });
        return (d, q, clock, log);
    }

    private static Trigger T(TriggerKind k) => new(k, Dev, "sess1", "user1", Item, 1234);

    private static string Layout(string id, string focus = "passive", string? parent = null, string? closeOn = null, string? suppress = null)
    {
        var o = new JsonObject
        {
            ["v"] = 1,
            ["id"] = id,
            ["focus"] = focus,
            ["root"] = new JsonObject { ["type"] = "text", ["text"] = "hi" },
        };
        if (parent is not null)
        {
            o["parent"] = parent;
        }

        if (closeOn is not null)
        {
            o["closeOn"] = JsonNode.Parse(closeOn);
        }

        if (suppress is not null)
        {
            o["suppressTriggers"] = JsonNode.Parse(suppress);
        }

        return o.ToJsonString();
    }

    private static JsonObject Evt(string layoutId, string type, string? action = null, string? reason = null)
    {
        var e = new JsonObject { ["deviceId"] = Dev, ["layoutId"] = layoutId, ["type"] = type };
        if (action is not null)
        {
            e["action"] = action;
            e["elementId"] = "btn";
            e["value"] = new JsonObject { ["v1"] = 3 };
        }

        if (reason is not null)
        {
            e["reason"] = reason;
        }

        return e;
    }

    [Fact]
    public async Task Trigger_FansOutToAll()
    {
        var (d, _, _, _) = Make();
        var a = new FakeClient();
        var b = new FakeClient();
        Assert.True(d.Register("a", "A", a.Handler));
        Assert.True(d.Register("b", "B", b.Handler));

        await d.OnTriggerAsync(T(TriggerKind.Pause));

        foreach (var c in new[] { a, b })
        {
            var e = Assert.Single(c.Events);
            Assert.Equal("trigger", (string?)e["type"]);
            Assert.Equal("pause", (string?)e["trigger"]);
            Assert.Equal(Dev, (string?)e["deviceId"]);
            Assert.Equal("sess1", (string?)e["sessionId"]);
            Assert.Equal("user1", (string?)e["userId"]);
            Assert.Equal(Item, Guid.Parse((string)e["itemId"]!));
            Assert.Equal(1234L, (long)e["positionTicks"]!);
        }
    }

    [Fact]
    public async Task SingleOffer_ChosenImmediately_NoChooser()
    {
        var (d, q, _, _) = Make();
        var a = new FakeClient { OfferId = "cast", OfferLabel = "Cast" };
        var b = new FakeClient();
        d.Register("a", "A", a.Handler);
        d.Register("b", "B", b.Handler);

        await d.OnTriggerAsync(T(TriggerKind.Pause));

        var chosen = Assert.Single(a.OfType("chosen"));
        Assert.Equal("cast", (string?)chosen["offerId"]);
        Assert.Equal(Dev, (string?)chosen["deviceId"]);
        Assert.Equal("sess1", (string?)chosen["sessionId"]);
        Assert.Equal(1234L, (long)chosen["positionTicks"]!);
        Assert.Equal(Item, Guid.Parse((string)chosen["itemId"]!));
        Assert.Empty(b.OfType("chosen"));
        Assert.Empty(q.Shown(Dev));
    }

    [Fact]
    public async Task TwoOffers_ShowChooserWithBothLabels()
    {
        var (d, q, _, _) = Make();
        d.Register("jellyxr", "JellyXR", new FakeClient { OfferId = "cast", OfferLabel = "Cast" }.Handler);
        d.Register("subsync", "SubSync", new FakeClient { OfferId = "sync", OfferLabel = "Sync subtitles" }.Handler);

        await d.OnTriggerAsync(T(TriggerKind.Pause));

        var chooser = Assert.Single(q.Shown(Dev));
        Assert.Equal("host/chooser", chooser.Id);
        Assert.Equal("interactive", chooser.Focus);
        Assert.Equal(new[] { "resume", "stop", "itemStart" }, chooser.CloseOn);
        Assert.Equal(0.4, (double)chooser.Json["dim"]!);
        var list = chooser.Json["root"]!.AsObject();
        Assert.Equal("list", (string?)list["type"]);
        Assert.Equal("vertical", (string?)list["orientation"]);
        var buttons = list["children"]!.AsArray().Select(n => n!.AsObject()).ToList();
        Assert.All(buttons, btn => Assert.Equal("button", (string?)btn["type"]));
        var pairs = buttons.Select(btn => ((string?)btn["label"], (string?)btn["action"])).OrderBy(p => p.Item1).ToList();
        Assert.Equal(("Cast", "choose:jellyxr:cast"), pairs[0]);
        Assert.Equal(("Sync subtitles", "choose:subsync:sync"), pairs[1]);
        Assert.Equal("host/chooser", d.HolderOf(Dev));
    }

    [Fact]
    public async Task ChooserPick_ClosesChooser_SendsChosen()
    {
        var (d, q, _, _) = Make();
        var a = new FakeClient { OfferId = "cast", OfferLabel = "Cast" };
        var b = new FakeClient { OfferId = "sync", OfferLabel = "Sync subtitles" };
        d.Register("a", "A", a.Handler);
        d.Register("b", "B", b.Handler);
        await d.OnTriggerAsync(T(TriggerKind.Pause));

        await d.OnAppEventAsync(Dev, Evt("host/chooser", "action", action: "choose:b:sync"));

        Assert.Empty(q.Shown(Dev));
        Assert.Null(d.HolderOf(Dev));
        Assert.Empty(a.OfType("chosen"));
        var chosen = Assert.Single(b.OfType("chosen"));
        Assert.Equal("sync", (string?)chosen["offerId"]);
        Assert.Equal("sess1", (string?)chosen["sessionId"]);
        Assert.Equal(1234L, (long)chosen["positionTicks"]!);
        Assert.Empty(a.OfType("action"));
        Assert.Empty(b.OfType("action"));
    }

    [Fact]
    public async Task ChooserBack_NoChosenEvent()
    {
        var (d, q, _, _) = Make();
        var a = new FakeClient { OfferId = "cast" };
        var b = new FakeClient { OfferId = "sync" };
        d.Register("a", "A", a.Handler);
        d.Register("b", "B", b.Handler);
        await d.OnTriggerAsync(T(TriggerKind.Pause));

        await d.OnAppEventAsync(Dev, Evt("host/chooser", "closed", reason: "back"));

        Assert.Empty(q.Shown(Dev));
        Assert.Null(d.HolderOf(Dev));
        Assert.Empty(a.OfType("chosen"));
        Assert.Empty(b.OfType("chosen"));

        // A pick after the chooser is gone does nothing.
        await d.OnAppEventAsync(Dev, Evt("host/chooser", "action", action: "choose:a:cast"));
        Assert.Empty(a.OfType("chosen"));
    }

    [Fact]
    public async Task LateOffer_Ignored()
    {
        var (d, q, clock, _) = Make();
        var fast = new FakeClient { OfferId = "cast" };
        var slow = new FakeClient { OfferId = "sync", Delay = TimeSpan.FromMilliseconds(600), Clock = clock };
        d.Register("fast", "Fast", fast.Handler);
        d.Register("slow", "Slow", slow.Handler);

        var task = d.OnTriggerAsync(T(TriggerKind.Pause));
        Assert.False(task.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(Dispatcher.OfferWindowMs));
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromMilliseconds(200));

        Assert.Single(fast.OfType("chosen"));
        Assert.Empty(slow.OfType("chosen"));
        Assert.Empty(q.Shown(Dev));
    }

    [Fact]
    public void Interactive_Rejected_WhenOtherClientHolds()
    {
        var (d, q, _, log) = Make();
        d.Register("a", "A", new FakeClient().Handler);
        d.Register("b", "B", new FakeClient().Handler);

        Assert.True(d.Show("a", Dev, Layout("menu", "interactive")));
        Assert.False(d.Show("b", Dev, Layout("other", "interactive")));
        Assert.Equal("a/menu", d.HolderOf(Dev));
        Assert.Single(q.Shown(Dev));
        Assert.NotEmpty(log);

        // Passive layouts from others are fine; the same client may stack another interactive one.
        Assert.True(d.Show("b", Dev, Layout("toast")));
        Assert.True(d.Show("a", Dev, Layout("menu2", "interactive")));
        Assert.Equal("a/menu2", d.HolderOf(Dev));
    }

    [Fact]
    public void Interactive_AllowedForChildOfHolder()
    {
        var (d, q, _, _) = Make();
        d.Register("a", "A", new FakeClient().Handler);
        d.Register("b", "B", new FakeClient().Handler);

        Assert.True(d.Show("a", Dev, Layout("menu", "interactive")));
        Assert.True(d.Show("b", Dev, Layout("detail", "interactive", parent: "a/menu")));
        Assert.Equal("b/detail", d.HolderOf(Dev));
        Assert.Equal("a/menu", q.Shown(Dev).Single(l => l.Id == "b/detail").Parent);

        // Grandchild through the chain.
        Assert.True(d.Show("b", Dev, Layout("deeper", "interactive", parent: "detail")));
        Assert.Equal("b/deeper", d.HolderOf(Dev));
    }

    [Fact]
    public async Task ChildInteractive_CoexistsAndHoldReturnsToParentOnClose()
    {
        var (d, q, _, _) = Make();
        var a = new FakeClient();
        var b = new FakeClient();
        d.Register("a", "A", a.Handler);
        d.Register("b", "B", b.Handler);

        Assert.True(d.Show("a", Dev, Layout("menu", "interactive")));
        Assert.True(d.Show("b", Dev, Layout("detail", "interactive", parent: "a/menu")));
        Assert.Equal(new[] { "a/menu", "b/detail" }, q.Shown(Dev).Select(l => l.Id));

        await d.OnAppEventAsync(Dev, Evt("b/detail", "closed", reason: "back"));
        Assert.Equal("a/menu", d.HolderOf(Dev));
        Assert.Single(b.OfType("closed"));
        Assert.Empty(a.OfType("closed"));

        // Client-initiated close works too.
        Assert.True(d.Show("b", Dev, Layout("detail", "interactive", parent: "a/menu")));
        d.Close("b", Dev, "detail");
        Assert.Equal("a/menu", d.HolderOf(Dev));

        // Once the parent is gone, an unrelated client may take the hold.
        d.Close("a", Dev, "a/menu");
        Assert.Null(d.HolderOf(Dev));
        Assert.True(d.Show("b", Dev, Layout("mine", "interactive")));
    }

    [Fact]
    public async Task Resume_ClosesLayoutsWithCloseOnResume()
    {
        var (d, q, _, _) = Make();
        var a = new FakeClient();
        var b = new FakeClient();
        d.Register("a", "A", a.Handler);
        d.Register("b", "B", b.Handler);
        Assert.True(d.Show("a", Dev, Layout("menu", "interactive", closeOn: "[\"resume\"]")));
        Assert.True(d.Show("b", Dev, Layout("keep", closeOn: "[\"stop\"]")));

        await d.OnTriggerAsync(T(TriggerKind.Resume));

        Assert.Null(d.HolderOf(Dev));
        Assert.Equal(new[] { "b/keep" }, q.Shown(Dev).Select(l => l.Id));
        var closed = Assert.Single(a.OfType("closed"));
        Assert.Equal("a/menu", (string?)closed["layoutId"]);
        Assert.Equal("resume", (string?)closed["reason"]);
        Assert.Equal(Dev, (string?)closed["deviceId"]);
        Assert.Empty(b.OfType("closed"));

        // closed reaches the owner before the trigger does.
        Assert.Equal(new[] { "closed", "trigger" }, a.Events.Select(e => (string?)e["type"]));
    }

    [Fact]
    public async Task AppAction_RoutedToOwnerOnly()
    {
        var (d, _, _, _) = Make();
        var a = new FakeClient();
        var b = new FakeClient();
        d.Register("a", "A", a.Handler);
        d.Register("b", "B", b.Handler);
        await d.OnTriggerAsync(T(TriggerKind.Pause));
        Assert.True(d.Show("a", Dev, Layout("menu", "interactive")));

        await d.OnAppEventAsync(Dev, Evt("a/menu", "action", action: "next"));

        var act = Assert.Single(a.OfType("action"));
        Assert.Equal("next", (string?)act["action"]);
        Assert.Equal("a/menu", (string?)act["layoutId"]);
        Assert.Equal("btn", (string?)act["elementId"]);
        Assert.Equal("sess1", (string?)act["sessionId"]);
        Assert.Equal(Dev, (string?)act["deviceId"]);
        Assert.Equal(3, (int)act["value"]!["v1"]!);
        Assert.Empty(b.OfType("action"));
    }

    [Fact]
    public async Task HandlerThrows_OtherClientsStillGetTrigger()
    {
        var (d, _, _, log) = Make();
        var thrower = new FakeClient { ThrowSync = true };
        var faulter = new FakeClient { Fault = true };
        var ok = new FakeClient { OfferId = "cast" };
        d.Register("t", "T", thrower.Handler);
        d.Register("f", "F", faulter.Handler);
        d.Register("ok", "OK", ok.Handler);

        await d.OnTriggerAsync(T(TriggerKind.Pause));

        Assert.Single(ok.OfType("trigger"));
        Assert.Single(ok.OfType("chosen"));
        Assert.True(log.Count >= 2);
    }

    [Fact]
    public async Task SuppressTriggers_DeliveredOnlyToOwner()
    {
        var (d, _, _, _) = Make();
        var a = new FakeClient { OfferId = "x" };
        var b = new FakeClient { OfferId = "y" };
        d.Register("a", "A", a.Handler);
        d.Register("b", "B", b.Handler);
        Assert.True(d.Show("a", Dev, Layout("sync", "interactive", suppress: "[\"pause\",\"resume\"]", closeOn: "[\"stop\"]")));

        await d.OnTriggerAsync(T(TriggerKind.Pause));

        Assert.Single(a.OfType("trigger"));
        Assert.Empty(b.OfType("trigger"));
        Assert.Single(a.OfType("chosen"));
        Assert.Empty(b.OfType("chosen"));

        // Not suppressed: everyone gets it.
        await d.OnTriggerAsync(T(TriggerKind.Seek));
        Assert.Equal(2, a.OfType("trigger").Count());
        Assert.Single(b.OfType("trigger"));

        // closeOn runs first: a layout closed by the trigger no longer suppresses it.
        Assert.True(d.Show("a", Dev, Layout("brief", suppress: "[\"resume\"]", closeOn: "[\"resume\"]")));
        d.Close("a", Dev, "sync");
        await d.OnTriggerAsync(T(TriggerKind.Resume));
        Assert.Single(b.OfType("trigger", "resume"));
    }

    [Fact]
    public async Task ConcurrentInteractiveShows_OnlyOneClientWins()
    {
        for (var round = 0; round < 50; round++)
        {
            var (d, q, _, _) = Make();
            d.Register("a", "A", new FakeClient().Handler);
            d.Register("b", "B", new FakeClient().Handler);
            using var go = new ManualResetEventSlim();
            var ta = Task.Run(() => { go.Wait(); return d.Show("a", Dev, Layout("m", "interactive")); });
            var tb = Task.Run(() => { go.Wait(); return d.Show("b", Dev, Layout("m", "interactive")); });
            go.Set();
            var results = await Task.WhenAll(ta, tb);
            Assert.Equal(1, results.Count(r => r));
            Assert.Single(q.Shown(Dev));
        }
    }

    [Fact]
    public void Register_RejectsReservedHostId()
    {
        var (d, _, _, _) = Make();
        Assert.False(d.Register("host", "Host", new FakeClient().Handler));
        Assert.False(d.Register(string.Empty, "x", new FakeClient().Handler));
        Assert.True(d.Register("a", "A", new FakeClient().Handler));
        Assert.True(d.Register("a", "A again", new FakeClient().Handler));
    }

    [Fact]
    public void Show_InvalidLayout_ReturnsFalse()
    {
        var (d, q, _, log) = Make();
        d.Register("a", "A", new FakeClient().Handler);
        Assert.False(d.Show("a", Dev, "{\"v\":2}"));
        Assert.False(d.Show("nobody", Dev, Layout("x")));
        Assert.Empty(q.Shown(Dev));
        Assert.Equal(2, log.Count);
    }
}
