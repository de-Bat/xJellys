using System.Collections.Concurrent;

namespace Jellyfin.Plugin.Jellycap;

/// <summary>One text card for the JellyXR TV overlay. At / Seconds: when to show it (seconds).</summary>
public sealed record OverlayCard(string Title, string? Body, double At, double Seconds);

public enum OverlayMode
{
    /// <summary>Text recap: the app pauses the episode, shows the cards one after another, then resumes it.</summary>
    Pause,

    /// <summary>Clips-only recap video: the app shows each card over its clip, timed by the playback position.</summary>
    Timed
}

public sealed class OverlayRecap
{
    /// <summary>Pause mode states.</summary>
    public const int Waiting = 0, Held = 1, Released = 2;

    public required Guid ItemId { get; init; }

    public required string SessionId { get; init; }

    public bool Ready { get; set; }

    public OverlayMode Mode { get; set; }

    /// <summary>Pause mode: where the episode continues after the cards.</summary>
    public long ResumeTicks { get; set; }

    public IReadOnlyList<OverlayCard> Cards { get; set; } = Array.Empty<OverlayCard>();

    public DateTime Created { get; } = DateTime.UtcNow;

    /// <summary>Pause mode: <see cref="Waiting"/>, <see cref="Held"/> (episode paused for the cards) or <see cref="Released"/>.</summary>
    public int State;

    public double TotalSeconds => Cards.Count == 0 ? 0 : Cards.Max(c => c.At + c.Seconds);
}

/// <summary>Recap overlays waiting for (or shown by) the JellyXR TV app, one per playback device.</summary>
public sealed class OverlayStore
{
    private static readonly TimeSpan Expiry = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, OverlayRecap> _byDevice = new(StringComparer.Ordinal);

    public void Set(string deviceId, OverlayRecap recap) => _byDevice[deviceId] = recap;

    public void Remove(string deviceId, OverlayRecap recap) => _byDevice.TryRemove(new KeyValuePair<string, OverlayRecap>(deviceId, recap));

    public OverlayRecap? Get(string? deviceId, Guid itemId)
    {
        if (string.IsNullOrEmpty(deviceId) || !_byDevice.TryGetValue(deviceId, out var r))
        {
            return null;
        }

        if (DateTime.UtcNow - r.Created > Expiry)
        {
            Remove(deviceId, r);
            return null;
        }

        return r.ItemId == itemId ? r : null;
    }

    /// <summary>Text recap: every card (headings too) one after another from 0.</summary>
    public static IReadOnlyList<OverlayCard> Sequential(IEnumerable<RecapSegment> plan)
    {
        var list = new List<OverlayCard>();
        var t = 0.0;
        foreach (var c in plan.OfType<TextCardSpec>())
        {
            list.Add(new OverlayCard(c.Title, c.Body, t, c.Seconds));
            t += c.Seconds;
        }

        return list;
    }

    /// <summary>
    /// "Both" plan (card before each clip) played as the clips-only video: each card shows from the start
    /// of the clip after it, later when the card before it is still up, and is cut at the end of the video.
    /// </summary>
    public static IReadOnlyList<OverlayCard> OverClips(IEnumerable<RecapSegment> plan)
    {
        var segments = plan.ToList();
        var total = segments.OfType<ClipSpec>().Sum(c => c.DurationSeconds);
        var list = new List<OverlayCard>();
        var cursor = 0.0;
        var free = 0.0;
        foreach (var s in segments)
        {
            if (s is ClipSpec c)
            {
                cursor += c.DurationSeconds;
            }
            else if (s is TextCardSpec t)
            {
                var at = Math.Max(cursor, free);
                var secs = Math.Min(t.Seconds, total - at);
                if (secs >= 1.5)
                {
                    list.Add(new OverlayCard(t.Title, t.Body, at, secs));
                    free = at + secs;
                }
            }
        }

        return list;
    }
}
