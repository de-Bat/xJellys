using System.Collections.Concurrent;

namespace Jellyfin.Plugin.JellyTvOverlay.Host;

/// <summary>Devices whose overlay app sent a heartbeat within the last 90 s. Thread-safe.</summary>
public sealed class HeartbeatRegistry(TimeProvider clock)
{
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(90);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);

    public void Beat(string deviceId)
    {
        var now = clock.GetUtcNow();
        foreach (var (id, at) in _seen)
        {
            if (now - at >= Fresh)
            {
                _seen.TryRemove(id, out _);
            }
        }

        _seen[deviceId] = now;
    }

    public bool IsFresh(string deviceId)
        => !string.IsNullOrEmpty(deviceId) && _seen.TryGetValue(deviceId, out var at) && clock.GetUtcNow() - at < Fresh;

    public IReadOnlyList<string> FreshDevices()
    {
        var now = clock.GetUtcNow();
        return _seen.Where(kv => now - kv.Value < Fresh).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
    }
}
