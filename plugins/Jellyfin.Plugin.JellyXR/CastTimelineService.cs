using Jellyfin.Plugin.JellyXR.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyXR;

/// <summary>One stretch of time (seconds) an actor is on screen.</summary>
public sealed record AppearanceSpan(int Start, int End);

/// <summary>When one actor appears in an item.</summary>
public sealed class ActorAppearance
{
    public string Name { get; set; } = string.Empty;

    public string? Character { get; set; }

    /// <summary>Jellyfin person item, for the portrait. Null when the person is not in the library.</summary>
    public Guid? PersonId { get; set; }

    public bool HasImage { get; set; }

    public int TotalSeconds { get; set; }

    public List<AppearanceSpan> Spans { get; set; } = new();

    /// <summary>First span starting after the one at <paramref name="sec"/> (or after <paramref name="sec"/> when not on screen then).</summary>
    public AppearanceSpan? NextAfter(int sec)
    {
        var current = Spans.FirstOrDefault(s => s.Start <= sec && sec <= s.End);
        var from = current?.End ?? sec;
        return Spans.FirstOrDefault(s => s.Start > from);
    }
}

/// <summary>Cast appearance timeline of an item, per actor.</summary>
public sealed class CastTimelineView
{
    public Guid ItemId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>"Local" or "Xrayarr".</summary>
    public string Source { get; set; } = string.Empty;

    public int DurationSeconds { get; set; }

    public List<ActorAppearance> Actors { get; set; } = new();

    public ActorAppearance? Find(string name) =>
        Actors.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Turns face samples (local scan) or Xrayarr segments into per actor spans.</summary>
public sealed class CastTimelineService
{
    /// <summary>Spans closer than this many seconds are merged (missed detections, cuts).</summary>
    private const int XrayarrMergeGapSeconds = 3;

    private readonly FaceScanner _scanner;
    private readonly TimelineStore _store;
    private readonly XrayarrClient _xrayarr;
    private readonly ILibraryManager _library;
    private readonly ILogger<CastTimelineService> _logger;

    public CastTimelineService(FaceScanner scanner, TimelineStore store, XrayarrClient xrayarr, ILibraryManager library, ILogger<CastTimelineService> logger)
    {
        _scanner = scanner;
        _store = store;
        _xrayarr = xrayarr;
        _library = library;
        _logger = PluginLog.Wrap(logger);
    }

    /// <summary>Timeline from the first configured source that has data for the item, or null.</summary>
    public async Task<CastTimelineView?> GetAsync(BaseItem item, PluginConfiguration cfg, CancellationToken ct)
    {
        var cast = _scanner.CastOf(item);
        foreach (var source in CardService.SourcesFor(cfg))
        {
            Dictionary<string, (string? Character, List<AppearanceSpan> Spans)>? raw = null;
            if (source == DataSource.Local)
            {
                var tl = _store.Load(item.Id);
                if (tl is { Entries.Count: > 0 })
                {
                    raw = FromLocal(tl, cfg);
                }
            }
            else
            {
                var xr = await _xrayarr.GetAsync(item, ct).ConfigureAwait(false);
                if (xr is { Segments.Count: > 0 })
                {
                    raw = FromXrayarr(xr);
                }
            }

            if (raw is null)
            {
                continue;
            }

            var view = new CastTimelineView
            {
                ItemId = item.Id,
                Name = item.Name,
                Source = source.ToString(),
                DurationSeconds = (int)TimeSpan.FromTicks(item.RunTimeTicks ?? 0).TotalSeconds
            };

            foreach (var (name, (character, spans)) in raw)
            {
                // Jellyfin's cast supplies the canonical name and role
                var p = cast.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                var role = p is null || string.IsNullOrWhiteSpace(p.Role) ? character : p.Role;
                var actor = new ActorAppearance
                {
                    Name = p?.Name ?? name,
                    Character = string.IsNullOrWhiteSpace(role) ? null : role,
                    Spans = spans,
                    TotalSeconds = spans.Sum(s => s.End - s.Start)
                };
                AttachPerson(actor);
                view.Actors.Add(actor);
            }

            view.Actors = view.Actors.OrderByDescending(a => a.TotalSeconds).ThenBy(a => a.Name).ToList();
            if (view.DurationSeconds <= 0)
            {
                view.DurationSeconds = view.Actors.SelectMany(a => a.Spans).Select(s => s.End).DefaultIfEmpty(0).Max();
            }

            return view;
        }

        return null;
    }

    private static Dictionary<string, (string?, List<AppearanceSpan>)> FromLocal(Timeline tl, PluginConfiguration cfg)
    {
        var interval = Math.Max(1, tl.Interval > 0 ? tl.Interval : cfg.ScanIntervalSeconds);
        // a face missed in up to two samples in a row still counts as the same stretch
        var gap = interval * 3;
        var times = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in tl.Entries.OrderBy(e => e.T))
        {
            foreach (var n in e.P.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!times.TryGetValue(n, out var list))
                {
                    times[n] = list = new List<int>();
                }

                list.Add(e.T);
            }
        }

        var result = new Dictionary<string, (string?, List<AppearanceSpan>)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, ts) in times)
        {
            var spans = new List<AppearanceSpan>();
            int start = ts[0], last = ts[0];
            foreach (var t in ts.Skip(1))
            {
                if (t - last > gap)
                {
                    spans.Add(new AppearanceSpan(start, last + interval));
                    start = t;
                }

                last = t;
            }

            spans.Add(new AppearanceSpan(start, last + interval));
            result[name] = (null, spans);
        }

        return result;
    }

    private static Dictionary<string, (string?, List<AppearanceSpan>)> FromXrayarr(XrayarrTimeline xr)
    {
        var raw = new Dictionary<string, (string? Character, List<AppearanceSpan> Spans)>(StringComparer.OrdinalIgnoreCase);
        foreach (var seg in xr.Segments.OrderBy(s => s.StartMs))
        {
            foreach (var p in seg.Cast.Where(p => !string.IsNullOrWhiteSpace(p.Name)))
            {
                if (!raw.TryGetValue(p.Name, out var entry))
                {
                    entry = (p.Character, new List<AppearanceSpan>());
                    raw[p.Name] = entry;
                }

                entry.Spans.Add(new AppearanceSpan((int)(seg.StartMs / 1000), (int)Math.Ceiling(seg.EndMs / 1000.0)));
            }
        }

        var result = new Dictionary<string, (string?, List<AppearanceSpan>)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, (character, spans)) in raw)
        {
            var merged = new List<AppearanceSpan>();
            foreach (var s in spans.OrderBy(s => s.Start))
            {
                if (merged.Count > 0 && s.Start - merged[^1].End <= XrayarrMergeGapSeconds)
                {
                    merged[^1] = merged[^1] with { End = Math.Max(merged[^1].End, s.End) };
                }
                else
                {
                    merged.Add(s);
                }
            }

            result[name] = (character, merged);
        }

        return result;
    }

    private void AttachPerson(ActorAppearance actor)
    {
        try
        {
            var person = _library.GetPerson(actor.Name);
            if (person is not null)
            {
                actor.PersonId = person.Id;
                actor.HasImage = person.HasImage(ImageType.Primary);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("No person item for {Name}: {Error}", actor.Name, ex.Message);
        }
    }

    /// <summary>"34 min on screen, next at 1:12:40".</summary>
    public static string Describe(ActorAppearance a, int atSec)
    {
        var total = a.TotalSeconds < 60 ? $"{a.TotalSeconds} s" : $"{(int)Math.Round(a.TotalSeconds / 60.0)} min";
        var next = a.NextAfter(atSec);
        return next is null ? $"{total} on screen" : $"{total} on screen, next at {Clock(next.Start)}";
    }

    public static string Clock(int sec)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, sec));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}
