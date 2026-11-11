namespace Jellyfin.Plugin.JellyXR.Overlay;

/// <summary>
/// Decides whether a finished cast build may still be shown on its device. Builds run in the background, so an
/// older one can finish after a newer one: only the build of the current generation is shown, and a passive panel
/// never replaces an interactive one this client has up. Thread-safe; no Jellyfin types.
/// </summary>
public sealed class CastShowGate
{
    private sealed class Device
    {
        public long Generation;
        public bool InteractiveUp;
    }

    private readonly Dictionary<string, Device> _devices = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Starts a build: older builds for the device become stale. Returns the build's generation.</summary>
    public long Begin(string deviceId)
    {
        lock (_gate)
        {
            return ++Get(deviceId).Generation;
        }
    }

    /// <summary>
    /// Playback moved on (resume, stop, item start): pending builds are stale, and the cast panel was closed by its
    /// closeOn triggers.
    /// </summary>
    public void Invalidate(string deviceId)
    {
        lock (_gate)
        {
            var d = Get(deviceId);
            d.Generation++;
            d.InteractiveUp = false;
        }
    }

    /// <summary>The cast panel on the device was closed (back, ttl, replaced, a closeOn trigger).</summary>
    public void Closed(string deviceId)
    {
        lock (_gate)
        {
            Get(deviceId).InteractiveUp = false;
        }
    }

    /// <summary>
    /// Calls <paramref name="show"/> when the build is still current and would not turn an interactive panel passive.
    /// Returns whether it was shown. The check and the show are one step, so two builds cannot both pass.
    /// </summary>
    public bool TryShow(string deviceId, long generation, bool interactive, Func<bool> show)
    {
        lock (_gate)
        {
            var d = Get(deviceId);
            if (generation != d.Generation || (!interactive && d.InteractiveUp))
            {
                return false;
            }

            if (!show())
            {
                return false;
            }

            d.InteractiveUp = interactive;
            return true;
        }
    }

    private Device Get(string deviceId)
    {
        if (!_devices.TryGetValue(deviceId, out var d))
        {
            _devices[deviceId] = d = new Device();
        }

        return d;
    }
}
