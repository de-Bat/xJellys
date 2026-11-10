using System.Collections.Concurrent;

namespace Jellyfin.Plugin.JellyTvOverlay.Triggers;

public enum TriggerKind { ItemStart, Pause, Resume, DoublePause, Seek, Stop }

public sealed record Trigger(TriggerKind Kind, string DeviceId, string SessionId, string UserId, Guid ItemId, long PositionTicks);

/// <summary>
/// Turns per-device playback samples into discrete triggers. Pure: no Jellyfin types; time comes from the injected clock.
/// </summary>
public sealed class TriggerDetector(TimeProvider clock)
{
    private const long DoublePauseMs = 4000;
    private const long SeekToleranceTicks = 5 * TimeSpan.TicksPerSecond;

    private sealed class State
    {
        public bool Active;
        public Guid ItemId;
        public bool Paused;
        public long PositionTicks;
        public long SampleAtMs;
        public long? ResumedAtMs;
    }

    private readonly ConcurrentDictionary<string, State> _devices = new();

    public IReadOnlyList<Trigger> OnSample(string deviceId, string sessionId, string userId, Guid itemId, bool isPaused, long positionTicks)
    {
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var state = _devices.GetOrAdd(deviceId, _ => new State());
        lock (state)
        {
            var result = new List<Trigger>(1);
            Trigger T(TriggerKind k) => new(k, deviceId, sessionId, userId, itemId, positionTicks);

            // The session id is not part of the state: a reconnect (new session, same item) is not a new item.
            if (!state.Active || state.ItemId != itemId)
            {
                state.Active = true;
                state.ItemId = itemId;
                state.ResumedAtMs = null;
                result.Add(T(TriggerKind.ItemStart));
            }
            else if (isPaused && !state.Paused)
            {
                var dbl = state.ResumedAtMs is { } r && now - r <= DoublePauseMs;
                state.ResumedAtMs = null;
                result.Add(T(dbl ? TriggerKind.DoublePause : TriggerKind.Pause));
            }
            else if (!isPaused && state.Paused)
            {
                state.ResumedAtMs = now;
                result.Add(T(TriggerKind.Resume));
            }
            else if (!isPaused)
            {
                var dPos = positionTicks - state.PositionTicks;
                var dWall = (now - state.SampleAtMs) * TimeSpan.TicksPerMillisecond;
                if (Math.Abs(dPos - dWall) > SeekToleranceTicks)
                {
                    result.Add(T(TriggerKind.Seek));
                }
            }

            state.Paused = isPaused;
            state.PositionTicks = positionTicks;
            state.SampleAtMs = now;
            return result;
        }
    }

    public IReadOnlyList<Trigger> OnStopped(string deviceId, string sessionId, string userId, Guid itemId, long positionTicks)
    {
        if (!_devices.TryRemove(deviceId, out var state))
        {
            return Array.Empty<Trigger>();
        }

        lock (state)
        {
            state.Active = false; // a racing OnSample holding this instance restarts cleanly
        }

        return new[] { new Trigger(TriggerKind.Stop, deviceId, sessionId, userId, itemId, positionTicks) };
    }
}
