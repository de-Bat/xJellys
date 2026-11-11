using System.Text.Json.Nodes;
using Jellyfin.Plugin.JellyTvOverlay.Configuration;
using Xjellys.Overlay;
using Xunit;

namespace Jellyfin.Plugin.JellyTvOverlay.Tests;

[Collection("OverlayHost")]
public sealed class OverlayClientTests : IDisposable
{
    private const string Dev = "dev1";

    public OverlayClientTests()
    {
        OverlayHost.ResetForTests();
        OverlayHost.ConfigSource = () => new PluginConfiguration();
        OverlayClient.ResetForTests();
    }

    public void Dispose()
    {
        OverlayHost.ResetForTests();
        OverlayClient.ResetForTests();
    }

    private static Task<string?> Handler(string json) => Task.FromResult<string?>(null);

    private static string Layout(string id) => new JsonObject
    {
        ["v"] = 1,
        ["id"] = id,
        ["root"] = new JsonObject { ["type"] = "text", ["text"] = "hi" },
    }.ToJsonString();

    public static class FakeV2Host
    {
        public const int ProtocolVersion = 2;

        public static bool Register(string a, string b, Func<string, Task<string?>> c) => true;

        public static bool Show(string a, string b, string c) => true;

        public static void Close(string a, string b, string c)
        {
        }

        public static bool HasApp(string a) => true;
    }

    [Fact]
    public void TryBind_FindsHost_RoundTripShow()
    {
        var client = OverlayClient.TryBind("c", "Client C", Handler);
        Assert.NotNull(client);
        Assert.Contains(("c", "Client C"), OverlayHost.Registered());

        // Host not started: Show false, same as the host API.
        Assert.False(client!.Show(Dev, Layout("x")));
        Assert.False(client.HasApp(Dev));

        var rt = new Host.OverlayRuntime(TimeProvider.System, null);
        OverlayHost.Attach(rt.Dispatcher, rt.Queue, _ => true);
        Assert.True(client.HasApp(Dev));
        Assert.True(client.Show(Dev, Layout("x")));
        Assert.Equal(new[] { "c/x" }, rt.Queue.Shown(Dev).Select(l => l.Id));
        client.Close(Dev, "x");
        Assert.Empty(rt.Queue.Shown(Dev));
    }

    [Fact]
    public void TryBind_ProtocolMismatch_Null()
    {
        Assert.Null(OverlayClient.TryBind("c", "C", Handler, () => typeof(FakeV2Host)));
    }

    [Fact]
    public void TryBind_NoHost_Null()
    {
        Assert.Null(OverlayClient.TryBind("c", "C", Handler, () => null));
    }

    [Fact]
    public void EnsureBound_RegistersOnce_AndThrottlesScans()
    {
        var calls = 0;
        Type? Missing()
        {
            calls++;
            return null;
        }

        Assert.Null(OverlayClient.EnsureBound("c", "C", Handler, Missing));
        Assert.Null(OverlayClient.EnsureBound("c", "C", Handler, Missing));
        Assert.Equal(1, calls); // second call within 5 s: no scan

        OverlayClient.ResetThrottleForTests();
        var bound = OverlayClient.EnsureBound("c", "C", Handler);
        Assert.NotNull(bound);
        Assert.Same(bound, OverlayClient.Current);
        Assert.Same(bound, OverlayClient.EnsureBound("c", "C", Handler));
        Assert.Single(OverlayHost.Registered());
    }
}
