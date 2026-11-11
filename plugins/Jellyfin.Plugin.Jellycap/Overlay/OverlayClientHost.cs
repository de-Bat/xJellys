using System.Globalization;
using System.Text.Json;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xjellys.Overlay;

namespace Jellyfin.Plugin.Jellycap.Overlay;

/// <summary>
/// Jellycap as a client of the JellyTvOverlay host: recap text cards drawn by the JellyTvOverlay app. Without the
/// host or the app, <see cref="RecapCoordinator"/> keeps today's paths (JellyXR TV companion, then the ffmpeg video).
/// </summary>
public sealed class OverlayClientHost : IHostedService
{
    public const string ClientId = "Jellycap";
    public const string DisplayName = "Recap";

    private readonly ISessionManager _sessions;
    private readonly ILogger<OverlayClientHost> _logger;
    private readonly object _queueGate = new();
    private Task _tail = Task.CompletedTask;

    public OverlayClientHost(ISessionManager sessions, ILogger<OverlayClientHost> logger)
    {
        _sessions = sessions;
        _logger = PluginLog.Wrap(logger);
        Driver = new RecapOverlayDriver(SendAsync, TimeProvider.System) { Logger = _logger, IsPaused = IsPaused };
        AppPresent = d => EnsureBound()?.HasApp(d) == true;
    }

    public RecapOverlayDriver Driver { get; }

    // Seam for tests; the production value is set in the constructor.
    internal Func<string, bool> AppPresent { get; set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var bound = EnsureBound() is not null;
        _logger.LogInformation("JellyTvOverlay host {State}", bound ? "found: text cards can go through it" : "not found yet: recap videos until it loads");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Binds to the host (rescans at most every ~5 s while it is missing). The host may load after Jellycap.</summary>
    internal OverlayClient? EnsureBound() => OverlayClient.EnsureBound(ClientId, DisplayName, OnEventAsync);

    /// <summary>True when the host is bound and the device runs the JellyTvOverlay app.</summary>
    public bool HasApp(string? deviceId) => !string.IsNullOrEmpty(deviceId) && AppPresent(deviceId);

    /// <summary>Host event handler. Answers at once; the driver's work (playstate commands) runs after it returns.</summary>
    internal Task<string?> OnEventAsync(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var e = doc.RootElement;
            var deviceId = Str(e, "deviceId");
            if (string.IsNullOrEmpty(deviceId))
            {
                return Task.FromResult<string?>(null);
            }

            var type = Str(e, "type");
            if (type is "trigger" or "action" or "closed")
            {
                // read every field now: the document is disposed when this returns
                var trigger = Str(e, "trigger");
                var itemId = Str(e, "itemId");
                var ticks = Long(e, "positionTicks");
                var layoutId = Str(e, "layoutId");
                var action = Str(e, "action");
                var reason = Str(e, "reason");
                Enqueue(async () =>
                {
                    try
                    {
                        await (type switch
                        {
                            "trigger" => Driver.OnTrigger(deviceId, trigger, itemId, ticks),
                            "action" => Driver.OnAction(deviceId, layoutId, action),
                            _ => Driver.OnClosed(deviceId, layoutId, reason),
                        }).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Jellycap overlay event {Type} failed", type);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("JellyTvOverlay event failed: {Error}", ex.Message);
        }

        return Task.FromResult<string?>(null);
    }

    /// <summary>Runs host events one after another, in arrival order (pause then resume must not swap).</summary>
    private void Enqueue(Func<Task> work)
    {
        lock (_queueGate)
        {
            _tail = _tail.ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    /// <summary>Driver commands: Pause, Unpause, Seek:&lt;ticks&gt; (as the old Hold/Release endpoints send them).</summary>
    private async Task SendAsync(string sessionId, string command)
    {
        var req = command switch
        {
            RecapOverlayDriver.Pause => new PlaystateRequest { Command = PlaystateCommand.Pause },
            RecapOverlayDriver.Unpause => new PlaystateRequest { Command = PlaystateCommand.Unpause },
            _ when command.StartsWith(RecapOverlayDriver.SeekPrefix, StringComparison.Ordinal)
                   && long.TryParse(command.AsSpan(RecapOverlayDriver.SeekPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
                => new PlaystateRequest { Command = PlaystateCommand.Seek, SeekPositionTicks = ticks },
            _ => null,
        };
        if (req is null)
        {
            _logger.LogWarning("Unknown playstate command {Command}", command);
            return;
        }

        await _sessions.SendPlaystateCommand(null, sessionId, req, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Whether the session is paused on that item; null when it is gone or plays something else (or nothing yet).</summary>
    private bool? IsPaused(string sessionId, Guid itemId)
    {
        var s = _sessions.Sessions.FirstOrDefault(x => string.Equals(x.Id, sessionId, StringComparison.Ordinal));
        if (s?.NowPlayingItem is not { } item || item.Id != itemId)
        {
            return null;
        }

        return s.PlayState?.IsPaused;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v) ? v : 0;
}
