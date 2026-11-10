using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.JellyTvOverlay.Host;

namespace Jellyfin.Plugin.JellyTvOverlay.Api;

/// <summary>Status code and JSON body (null = none).</summary>
public sealed record ApiResult(int Status, string? Json);

/// <summary>
/// Endpoint logic behind OverlayController, free of ASP.NET types. <c>canUse</c> is the caller's DeviceAccess check;
/// it runs before anything touches per-device state, so foreign or unknown device ids create none.
/// </summary>
public sealed class OverlayApi(OverlayRuntime rt)
{
    private static readonly JsonSerializerOptions RelaxedJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static readonly TimeSpan PollHold = TimeSpan.FromSeconds(25);

    /// <summary>App action handlers get this long before the Event request answers anyway.</summary>
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(2);

    public async Task<ApiResult> PollAsync(string deviceId, long since, Func<string, bool> canUse, TimeSpan hold, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(deviceId) || since < 0)
        {
            return new ApiResult(400, null);
        }

        if (!canUse(deviceId))
        {
            return new ApiResult(403, null);
        }

        var ops = await rt.Queue.WaitAsync(deviceId, since, hold, ct).ConfigureAwait(false);
        var arr = new JsonArray();
        var seq = since;
        foreach (var op in ops)
        {
            var o = new JsonObject { ["seq"] = op.Seq, ["op"] = op.Op, ["layoutId"] = op.LayoutId };
            if (op.Layout is not null)
            {
                o["layout"] = op.Layout.DeepClone();
            }

            arr.Add(o);
            seq = op.Seq;
        }

        // non-ASCII text (Hebrew) as UTF-8 rather than \uXXXX escapes: about a third of the size. JSON body, never HTML.
        return new ApiResult(200, new JsonObject { ["seq"] = seq, ["ops"] = arr }.ToJsonString(RelaxedJson));
    }

    public ApiResult Heartbeat(string deviceId, Func<string, bool> canUse)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return new ApiResult(400, null);
        }

        if (!canUse(deviceId))
        {
            return new ApiResult(403, null);
        }

        // Recorded even when switched off, so the settings page lists the device and can switch it back on.
        rt.Heartbeats.Beat(deviceId);
        var enabled = OverlayHost.IsEnabled() && OverlayHost.IsDeviceEnabled(deviceId);
        return new ApiResult(200, new JsonObject { ["enabled"] = enabled, ["protocol"] = OverlayHost.ProtocolVersion }.ToJsonString());
    }

    public async Task<ApiResult> EventAsync(string body, Func<string, bool> canUse)
    {
        JsonObject evt;
        try
        {
            if (JsonNode.Parse(body) is not JsonObject o)
            {
                return new ApiResult(400, null);
            }

            evt = o;
            _ = evt.Count; // materializes properties: duplicate keys throw here
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return new ApiResult(400, null);
        }

        var deviceId = Str(evt["deviceId"]);
        var layoutId = Str(evt["layoutId"]);
        if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(layoutId))
        {
            return new ApiResult(400, null);
        }

        if (!canUse(deviceId) || !rt.Queue.Shown(deviceId).Any(l => l.Id == layoutId))
        {
            return new ApiResult(403, null);
        }

        var task = rt.Dispatcher.OnAppEventAsync(deviceId, evt);
        await Task.WhenAny(task, Task.Delay(EventWait)).ConfigureAwait(false);
        if (task.IsFaulted)
        {
            // Malformed nested JSON (e.g. duplicate keys inside "value") surfaces only when the dispatcher reads it.
            _ = task.Exception;
            return new ApiResult(400, null);
        }

        _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return new ApiResult(204, null);
    }

    /// <summary>Client id the debug endpoint shows as (layout ids become "debug/...").</summary>
    public const string DebugClient = "debug";

    /// <summary>
    /// Debug aid behind <c>POST JellyTvOverlay/Debug/Show</c>: 404 unless <c>DebugEndpoints</c> is on; then shows the layout
    /// as client "debug" (registered on first use; its handler only logs the events it gets). <c>{"shown":bool}</c>.
    /// </summary>
    public ApiResult DebugShow(string deviceId, string layoutJson, Func<string, bool> canUse)
    {
        if (!DebugEnabled())
        {
            return new ApiResult(404, null);
        }

        if (string.IsNullOrEmpty(deviceId))
        {
            return new ApiResult(400, null);
        }

        if (!canUse(deviceId))
        {
            return new ApiResult(403, null);
        }

        if (!rt.Dispatcher.IsRegistered(DebugClient))
        {
            // logs what the app sends back (actions, closes), so a manual check can see it in the server log
            rt.Dispatcher.Register(DebugClient, "Debug", json =>
            {
                // switched off later: the client stays registered but hears nothing (no logging, no offers)
                if (DebugEnabled())
                {
                    rt.Log("debug client event: " + json);
                }

                return Task.FromResult<string?>(null);
            });
        }

        var shown = OverlayHost.IsEnabled() && OverlayHost.IsDeviceEnabled(deviceId)
            && rt.Dispatcher.Show(DebugClient, deviceId, layoutJson ?? string.Empty);
        return new ApiResult(200, new JsonObject { ["shown"] = shown }.ToJsonString());
    }

    private static bool DebugEnabled() => OverlayHost.ConfigSource()?.DebugEndpoints == true;

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
