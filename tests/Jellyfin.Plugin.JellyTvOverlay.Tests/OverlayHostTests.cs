using System.Text.Json.Nodes;
using Jellyfin.Plugin.JellyTvOverlay.Api;
using Jellyfin.Plugin.JellyTvOverlay.Configuration;
using Jellyfin.Plugin.JellyTvOverlay.Host;
using Jellyfin.Plugin.JellyTvOverlay.Triggers;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.JellyTvOverlay.Tests;

/// <summary>OverlayHost is static: every test touching it lives in this class, so they run one at a time.</summary>
[Collection("OverlayHost")]
public sealed class OverlayHostTests : IDisposable
{
    private const string Dev = "dev1";
    private static readonly Guid Item = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Alice = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private readonly PluginConfiguration _cfg = new();
    private readonly FakeTimeProvider _clock = new();
    private readonly List<string> _events = new();

    public OverlayHostTests()
    {
        OverlayHost.ResetForTests();
        OverlayHost.ConfigSource = () => _cfg;
    }

    public void Dispose()
    {
        OverlayHost.ResetForTests();
    }

    private Task<string?> Handler(string json)
    {
        lock (_events)
        {
            _events.Add(json);
        }

        return Task.FromResult<string?>(null);
    }

    private OverlayRuntime Runtime() => new(_clock, null);

    private static string Layout(string id) => new JsonObject
    {
        ["v"] = 1,
        ["id"] = id,
        ["root"] = new JsonObject { ["type"] = "text", ["text"] = "hi" },
    }.ToJsonString();

    private static bool Anyone(string deviceId) => true;

    [Fact]
    public async Task Register_BeforeStart_AppliedOnStart()
    {
        Assert.True(OverlayHost.Register("c", "Client C", Handler));
        Assert.False(OverlayHost.Register("host", "reserved", Handler));
        Assert.False(OverlayHost.Show("c", Dev, Layout("x")));
        Assert.False(OverlayHost.HasApp(Dev));

        var rt = Runtime();
        OverlayHost.Attach(rt.Dispatcher, rt.Queue, _ => true);

        Assert.True(OverlayHost.Show("c", Dev, Layout("x")));
        Assert.Equal(new[] { "c/x" }, rt.Queue.Shown(Dev).Select(l => l.Id));
        OverlayHost.Close("c", Dev, "x");
        Assert.Empty(rt.Queue.Shown(Dev));

        await rt.Dispatcher.OnTriggerAsync(new Trigger(TriggerKind.Pause, Dev, "s", "u", Item, 0));
        for (var i = 0; i < 200 && _events.Count == 0; i++)
        {
            await Task.Delay(10);
        }

        Assert.Contains(_events, e => e.Contains("\"trigger\":\"pause\"", StringComparison.Ordinal));
        Assert.Contains(("c", "Client C"), OverlayHost.Registered());
    }

    [Fact]
    public void HasApp_FalseAfter90s()
    {
        var rt = Runtime();
        OverlayHost.Attach(rt.Dispatcher, rt.Queue, rt.Heartbeats.IsFresh);
        Assert.False(OverlayHost.HasApp(Dev));

        rt.Heartbeats.Beat(Dev);
        Assert.True(OverlayHost.HasApp(Dev));
        _clock.Advance(TimeSpan.FromSeconds(89));
        Assert.True(OverlayHost.HasApp(Dev));
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(OverlayHost.HasApp(Dev));
        Assert.DoesNotContain(Dev, rt.Heartbeats.FreshDevices());
    }

    [Fact]
    public void Show_Disabled_ReturnsFalse()
    {
        var rt = Runtime();
        OverlayHost.Register("c", "C", Handler);
        OverlayHost.Attach(rt.Dispatcher, rt.Queue, _ => true);
        _cfg.Enabled = false;

        Assert.False(OverlayHost.Show("c", Dev, Layout("x")));
        Assert.False(OverlayHost.HasApp(Dev));
        Assert.Empty(rt.Queue.Shown(Dev));

        var hb = new OverlayApi(rt).Heartbeat(Dev, Anyone);
        Assert.Equal(200, hb.Status);
        Assert.False((bool)JsonNode.Parse(hb.Json!)!["enabled"]!);
    }

    [Fact]
    public void DisabledDevice_HasAppFalse_ShowFalse()
    {
        var rt = Runtime();
        var api = new OverlayApi(rt);
        OverlayHost.Register("c", "C", Handler);
        OverlayHost.Attach(rt.Dispatcher, rt.Queue, rt.Heartbeats.IsFresh);
        _cfg.DisabledDevices = new[] { Dev };

        var hb = JsonNode.Parse(api.Heartbeat(Dev, Anyone).Json!)!;
        Assert.False((bool)hb["enabled"]!);
        Assert.Equal(1, (int)hb["protocol"]!);
        Assert.False(OverlayHost.HasApp(Dev));
        Assert.False(OverlayHost.Show("c", Dev, Layout("x")));
        Assert.Empty(rt.Queue.Shown(Dev));

        // The device still shows up in the settings list so it can be switched back on.
        Assert.Contains(Dev, rt.Heartbeats.FreshDevices());

        var other = JsonNode.Parse(api.Heartbeat("dev2", Anyone).Json!)!;
        Assert.True((bool)other["enabled"]!);
        Assert.True(OverlayHost.HasApp("dev2"));
        Assert.True(OverlayHost.Show("c", "dev2", Layout("x")));
    }

    [Fact]
    public void DeviceAccess_OtherUsersDevice_Denied()
    {
        var sessions = new[] { (Dev, Bob), ("dev2", Alice) };
        Assert.False(DeviceAccess.CanUse(Alice, false, Dev, sessions));
        Assert.True(DeviceAccess.CanUse(Alice, false, "dev2", sessions));
        Assert.True(DeviceAccess.CanUse(Bob, false, Dev, sessions));
    }

    [Fact]
    public void DeviceAccess_Admin_Allowed()
    {
        var sessions = new[] { (Dev, Bob) };
        Assert.True(DeviceAccess.CanUse(Alice, true, Dev, sessions));
    }

    [Fact]
    public void DeviceAccess_NoSession_Denied()
    {
        var sessions = new[] { ("dev2", Alice) };
        Assert.False(DeviceAccess.CanUse(Alice, false, Dev, sessions));
        Assert.False(DeviceAccess.CanUse(Alice, true, Dev, sessions));
        Assert.False(DeviceAccess.CanUse(Alice, false, string.Empty, new[] { (string.Empty, Alice) }));
        Assert.False(DeviceAccess.CanUse(Guid.Empty, false, "dev2", new[] { ("dev2", Guid.Empty) }));
    }

    [Fact]
    public async Task Event_LayoutNotShown_Rejected()
    {
        var rt = Runtime();
        var api = new OverlayApi(rt);
        OverlayHost.Register("c", "C", Handler);
        OverlayHost.Attach(rt.Dispatcher, rt.Queue, _ => true);

        var body = new JsonObject { ["deviceId"] = Dev, ["layoutId"] = "c/x", ["type"] = "action", ["action"] = "go" }.ToJsonString();
        Assert.Equal(403, (await api.EventAsync(body, Anyone)).Status);
        Assert.Empty(_events);

        Assert.True(OverlayHost.Show("c", Dev, Layout("x")));
        Assert.Equal(403, (await api.EventAsync(body, _ => false)).Status);
        Assert.Empty(_events);

        Assert.Equal(204, (await api.EventAsync(body, Anyone)).Status);
        Assert.Contains(_events, e => e.Contains("\"action\":\"go\"", StringComparison.Ordinal));

        Assert.Equal(400, (await api.EventAsync("{not json", Anyone)).Status);
        Assert.Equal(400, (await api.EventAsync("{\"layoutId\":\"c/x\"}", Anyone)).Status);
    }

    [Fact]
    public async Task Poll_Forbidden_And_Snapshot()
    {
        var rt = Runtime();
        var api = new OverlayApi(rt);
        OverlayHost.Register("c", "C", Handler);
        OverlayHost.Attach(rt.Dispatcher, rt.Queue, _ => true);

        Assert.Equal(403, (await api.PollAsync(Dev, 0, _ => false, TimeSpan.Zero, CancellationToken.None)).Status);

        var empty = await api.PollAsync(Dev, 0, Anyone, TimeSpan.Zero, CancellationToken.None);
        Assert.Equal(200, empty.Status);
        Assert.Equal("{\"seq\":0,\"ops\":[]}", empty.Json);

        Assert.True(OverlayHost.Show("c", Dev, Layout("x")));
        var snap = JsonNode.Parse((await api.PollAsync(Dev, 0, Anyone, TimeSpan.Zero, CancellationToken.None)).Json!)!;
        Assert.Equal(1L, (long)snap["seq"]!);
        var ops = snap["ops"]!.AsArray();
        Assert.Equal(new[] { "reset", "show" }, ops.Select(o => (string?)o!["op"]));
        Assert.Equal("c/x", (string?)ops[1]!["layoutId"]);
        Assert.Equal("c", (string?)ops[1]!["layout"]!["owner"]);

        OverlayHost.Close("c", Dev, "x");
        var delta = JsonNode.Parse((await api.PollAsync(Dev, 1, Anyone, TimeSpan.Zero, CancellationToken.None)).Json!)!;
        Assert.Equal(2L, (long)delta["seq"]!);
        var close = Assert.Single(delta["ops"]!.AsArray());
        Assert.Equal("close", (string?)close!["op"]);
        Assert.Null(close["layout"]);
    }

    [Fact]
    public void Heartbeat_Forbidden_NotRecorded()
    {
        var rt = Runtime();
        var api = new OverlayApi(rt);
        OverlayHost.Attach(rt.Dispatcher, rt.Queue, rt.Heartbeats.IsFresh);

        Assert.Equal(403, api.Heartbeat(Dev, _ => false).Status);
        Assert.False(OverlayHost.HasApp(Dev));
        Assert.Empty(rt.Heartbeats.FreshDevices());
        Assert.Equal(400, api.Heartbeat(string.Empty, Anyone).Status);
    }

    [Fact]
    public void Bridge_IgnoresSamplesForStoppedItemFor3s()
    {
        var rt = Runtime();
        Assert.Equal(TriggerKind.ItemStart, Assert.Single(rt.OnSample(Dev, "s", Alice, Item, false, 0)).Kind);
        Assert.Equal(TriggerKind.Stop, Assert.Single(rt.OnStopped(Dev, "s", Alice, Item, 10)).Kind);

        // A trailing progress report for the stopped item is not a new start.
        _clock.Advance(TimeSpan.FromSeconds(2.9));
        Assert.Empty(rt.OnSample(Dev, "s", Alice, Item, false, 10));

        // Another item starts at once.
        var other = Guid.NewGuid();
        Assert.Equal(TriggerKind.ItemStart, Assert.Single(rt.OnSample(Dev, "s", Alice, other, false, 0)).Kind);
        Assert.Single(rt.OnStopped(Dev, "s", Alice, other, 0));

        _clock.Advance(TimeSpan.FromSeconds(3));
        var again = Assert.Single(rt.OnSample(Dev, "s", Alice, other, false, 0));
        Assert.Equal(TriggerKind.ItemStart, again.Kind);
        Assert.Equal(Alice.ToString("N"), again.UserId);
    }

    [Fact]
    public void DebugShow_Off_404_NothingShown()
    {
        var rt = Runtime();
        var api = new OverlayApi(rt);
        Assert.False(_cfg.DebugEndpoints); // default off

        Assert.Equal(404, api.DebugShow(Dev, Layout("x"), Anyone).Status);
        Assert.Empty(rt.Queue.Shown(Dev));
        Assert.False(rt.Dispatcher.IsRegistered(OverlayApi.DebugClient));
        Assert.DoesNotContain(OverlayHost.Registered(), r => r.Id == "debug");
    }

    [Fact]
    public void DebugShow_RequiresElevation()
    {
        var m = typeof(OverlayController).GetMethod(nameof(OverlayController.DebugShow))!;
        var auth = m.GetCustomAttributes(true).Where(a => a.GetType().Name == "AuthorizeAttribute").ToList();
        var policy = Assert.Single(auth).GetType().GetProperty("Policy")!.GetValue(auth[0]);
        Assert.Equal("RequiresElevation", policy);
    }

    [Fact]
    public async Task DebugClient_SwitchedOff_HearsNothing()
    {
        var logs = new List<string>();
        void Log(string m)
        {
            lock (logs)
            {
                logs.Add(m);
            }
        }

        int DebugLogs()
        {
            lock (logs)
            {
                return logs.Count(l => l.Contains("debug client event", StringComparison.Ordinal));
            }
        }

        _cfg.DebugEndpoints = true;
        var rt = new OverlayRuntime(_clock, Log);
        Assert.Equal(200, new OverlayApi(rt).DebugShow(Dev, Layout("x"), Anyone).Status);
        Assert.True(rt.Dispatcher.IsRegistered(OverlayApi.DebugClient));

        await rt.Dispatcher.OnTriggerAsync(new Trigger(TriggerKind.Pause, Dev, "s", "u", Item, 0));
        for (var i = 0; i < 200 && DebugLogs() == 0; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(1, DebugLogs()); // on: the trigger is logged

        _cfg.DebugEndpoints = false;
        await rt.Dispatcher.OnTriggerAsync(new Trigger(TriggerKind.Resume, Dev, "s", "u", Item, 0));
        await Task.Delay(300);
        Assert.Equal(1, DebugLogs()); // off: nothing more
    }

    [Fact]
    public void DebugShow_On_ShowsAsDebugOwner()
    {
        _cfg.DebugEndpoints = true;
        var rt = Runtime();
        var api = new OverlayApi(rt);

        var r = api.DebugShow(Dev, Layout("x"), Anyone);
        Assert.Equal(200, r.Status);
        Assert.True(JsonNode.Parse(r.Json!)!["shown"]!.GetValue<bool>());
        Assert.Equal(new[] { "debug/x" }, rt.Queue.Shown(Dev).Select(l => l.Id));

        // invalid layout: not shown, still 200 with shown:false
        var bad = api.DebugShow(Dev, "{\"v\":1}", Anyone);
        Assert.Equal(200, bad.Status);
        Assert.False(JsonNode.Parse(bad.Json!)!["shown"]!.GetValue<bool>());

        Assert.Equal(400, api.DebugShow(string.Empty, Layout("x"), Anyone).Status);
        Assert.Equal(403, api.DebugShow(Dev, Layout("y"), _ => false).Status);

        // switched-off plugin: nothing shown
        _cfg.Enabled = false;
        Assert.False(JsonNode.Parse(api.DebugShow(Dev, Layout("z"), Anyone).Json!)!["shown"]!.GetValue<bool>());
    }
}
