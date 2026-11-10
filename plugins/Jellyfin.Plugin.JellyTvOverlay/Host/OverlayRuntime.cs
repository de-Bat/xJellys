using System.Collections.Concurrent;
using Jellyfin.Plugin.JellyTvOverlay.Delivery;
using Jellyfin.Plugin.JellyTvOverlay.Triggers;

namespace Jellyfin.Plugin.JellyTvOverlay.Host;

/// <summary>
/// The host's singletons (queue, dispatcher, trigger detector, heartbeats) and the playback feed that turns
/// session samples into dispatched triggers. No Jellyfin types; SessionBridge adapts Jellyfin events to it.
/// </summary>
public sealed class OverlayRuntime
{
    /// <summary>After a stop, samples for the same device and item are ignored this long (a trailing progress report is no new start).</summary>
    public static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(3);

    private readonly TimeProvider _clock;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<string, (Guid ItemId, DateTimeOffset At)> _stopped = new(StringComparer.Ordinal);

    public OverlayRuntime(TimeProvider clock, Action<string>? log)
    {
        _clock = clock;
        _log = log;
        Queue = new OverlayQueue(clock);
        Dispatcher = new Dispatcher(Queue, clock, log);
        Detector = new TriggerDetector(clock);
        Heartbeats = new HeartbeatRegistry(clock);
    }

    public OverlayQueue Queue { get; }

    public Dispatcher Dispatcher { get; }

    public TriggerDetector Detector { get; }

    public HeartbeatRegistry Heartbeats { get; }

    public TimeProvider Clock => _clock;

    /// <summary>Feeds a PlaybackStart/PlaybackProgress sample; dispatches and returns the resulting triggers.</summary>
    public IReadOnlyList<Trigger> OnSample(string deviceId, string sessionId, Guid userId, Guid itemId, bool isPaused, long positionTicks)
    {
        if (_stopped.TryGetValue(deviceId, out var s))
        {
            if (s.ItemId == itemId && _clock.GetUtcNow() - s.At < StopGrace)
            {
                return Array.Empty<Trigger>();
            }

            _stopped.TryRemove(KeyValuePair.Create(deviceId, s));
        }

        return Dispatch(Detector.OnSample(deviceId, sessionId, userId.ToString("N"), itemId, isPaused, positionTicks));
    }

    /// <summary>Feeds a PlaybackStopped event; dispatches and returns the resulting triggers.</summary>
    public IReadOnlyList<Trigger> OnStopped(string deviceId, string sessionId, Guid userId, Guid itemId, long positionTicks)
    {
        var now = _clock.GetUtcNow();
        foreach (var (id, e) in _stopped)
        {
            if (now - e.At >= StopGrace)
            {
                _stopped.TryRemove(KeyValuePair.Create(id, e));
            }
        }

        _stopped[deviceId] = (itemId, now);
        return Dispatch(Detector.OnStopped(deviceId, sessionId, userId.ToString("N"), itemId, positionTicks));
    }

    private IReadOnlyList<Trigger> Dispatch(IReadOnlyList<Trigger> triggers)
    {
        foreach (var t in triggers)
        {
            Log($"trigger {t.Kind} on device {t.DeviceId} (item {t.ItemId:N})");

            // Called directly: the dispatcher's bookkeeping runs synchronously in event order; the offer window does not block.
            _ = Dispatcher.OnTriggerAsync(t).ContinueWith(
                tk => Log($"trigger {t.Kind} on {t.DeviceId} failed: {tk.Exception?.GetBaseException().Message}"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return triggers;
    }

    private void Log(string message)
    {
        try
        {
            _log?.Invoke("JellyTvOverlay: " + message);
        }
        catch
        {
            // Logging must never break playback handling.
        }
    }
}
