using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTvOverlay.Host;

/// <summary>Jellyfin session events → OverlayRuntime (TriggerDetector → Dispatcher). Attaches OverlayHost and ticks the queue every 500 ms.</summary>
public sealed class SessionBridge : IHostedService, IDisposable
{
    private static readonly TimeSpan TickEvery = TimeSpan.FromMilliseconds(500);

    private readonly ISessionManager _sessions;
    private readonly OverlayRuntime _rt;
    private readonly ILogger<SessionBridge> _logger;
    private ITimer? _timer;

    public SessionBridge(ISessionManager sessions, OverlayRuntime rt, ILogger<SessionBridge> logger)
    {
        _sessions = sessions;
        _rt = rt;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        OverlayHost.Attach(_rt.Dispatcher, _rt.Queue, _rt.Heartbeats.IsFresh);
        _sessions.PlaybackStart += OnSample;
        _sessions.PlaybackProgress += OnSample;
        _sessions.PlaybackStopped += OnStopped;
        _timer = _rt.Clock.CreateTimer(_ => Tick(), null, TickEvery, TickEvery);
        _logger.LogInformation("JellyTvOverlay host started (enabled: {Enabled}, protocol {Protocol})", OverlayHost.IsEnabled(), OverlayHost.ProtocolVersion);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart -= OnSample;
        _sessions.PlaybackProgress -= OnSample;
        _sessions.PlaybackStopped -= OnStopped;
        _timer?.Dispose();
        _timer = null;
        OverlayHost.Detach();
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();

    private void Tick()
    {
        try
        {
            _rt.Queue.Tick();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JellyTvOverlay queue tick failed");
        }
    }

    private void OnSample(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            if (Read(e) is not { } s)
            {
                return;
            }

            _rt.OnSample(s.DeviceId, s.SessionId, s.UserId, s.ItemId, e.IsPaused, e.PlaybackPositionTicks ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JellyTvOverlay playback sample handling failed");
        }
    }

    private void OnStopped(object? sender, PlaybackStopEventArgs e)
    {
        try
        {
            if (Read(e) is not { } s)
            {
                return;
            }

            _rt.OnStopped(s.DeviceId, s.SessionId, s.UserId, s.ItemId, e.PlaybackPositionTicks ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JellyTvOverlay playback stop handling failed");
        }
    }

    // Null when the plugin is switched off or the event lacks what the detector needs.
    private static (string DeviceId, string SessionId, Guid UserId, Guid ItemId)? Read(PlaybackProgressEventArgs e)
    {
        if (!OverlayHost.IsEnabled() || e.Session is not { } session || e.Item is not { } item)
        {
            return null;
        }

        var deviceId = string.IsNullOrEmpty(e.DeviceId) ? session.DeviceId : e.DeviceId;
        if (string.IsNullOrEmpty(deviceId))
        {
            return null;
        }

        return (deviceId, session.Id, session.UserId, item.Id);
    }
}
