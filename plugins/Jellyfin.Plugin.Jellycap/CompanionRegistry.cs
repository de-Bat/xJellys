using System.Collections.Concurrent;

namespace Jellyfin.Plugin.Jellycap;

/// <summary>
/// Playback devices watched by a running JellyXR TV companion app. The app sends a heartbeat every 30 s
/// naming the device it watches; while that is fresh, Jellycap shows text cards through the app's overlay
/// instead of burning them into the recap video.
/// </summary>
public static class CompanionRegistry
{
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(90);
    private static readonly ConcurrentDictionary<string, DateTime> Seen = new(StringComparer.Ordinal);

    public static void Beat(string deviceId)
    {
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            Seen[deviceId] = DateTime.UtcNow;
        }
    }

    public static bool IsActive(string? deviceId) =>
        !string.IsNullOrEmpty(deviceId)
        && Seen.TryGetValue(deviceId, out var at)
        && DateTime.UtcNow - at < Fresh;
}
