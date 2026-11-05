using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Jellycap.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.Jellycap;

/// <summary>Decides which clips make up a recap.</summary>
public class RecapPlanner
{
    private readonly ILibraryManager _libraryManager;

    public RecapPlanner(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    public IReadOnlyList<Episode> GetSeriesEpisodes(Episode episode)
    {
        if (episode.SeriesId == Guid.Empty)
        {
            return Array.Empty<Episode>();
        }

        var query = new InternalItemsQuery
        {
            AncestorIds = new[] { episode.SeriesId },
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            IsVirtualItem = false,
            Recursive = true,
            OrderBy = new[]
            {
                (ItemSortBy.ParentIndexNumber, SortOrder.Ascending),
                (ItemSortBy.IndexNumber, SortOrder.Ascending)
            }
        };

        return _libraryManager.GetItemList(query).OfType<Episode>().ToList();
    }

    public Episode? GetNext(Episode episode)
    {
        var all = GetSeriesEpisodes(episode);
        var idx = IndexOf(all, episode);
        return idx >= 0 && idx + 1 < all.Count ? all[idx + 1] : null;
    }

    public IReadOnlyList<ClipSpec> Plan(Episode episode, RecapMode mode, TimeSpan resumePosition, PluginConfiguration cfg)
    {
        var clips = new List<ClipSpec>();
        var all = GetSeriesEpisodes(episode);
        var idx = IndexOf(all, episode);

        if (idx > 0 && cfg.PreviousEpisodes > 0)
        {
            var isSpecial = (episode.ParentIndexNumber ?? 1) == 0;
            var previous = all.Take(idx)
                .Where(e => isSpecial || (e.ParentIndexNumber ?? 1) > 0)
                .Where(e => !string.IsNullOrEmpty(e.Path) && File.Exists(e.Path))
                .TakeLast(cfg.PreviousEpisodes);

            foreach (var prev in previous)
            {
                var clip = TailClip(prev, cfg);
                if (clip is not null)
                {
                    clips.Add(clip);
                }
            }
        }

        if (mode == RecapMode.Resume && !string.IsNullOrEmpty(episode.Path) && File.Exists(episode.Path))
        {
            clips.AddRange(WatchedPartClips(episode.Path, resumePosition, cfg));
        }

        return clips;
    }

    private static int IndexOf(IReadOnlyList<Episode> all, Episode episode)
    {
        for (var i = 0; i < all.Count; i++)
        {
            if (all[i].Id == episode.Id)
            {
                return i;
            }
        }

        return -1;
    }

    private static ClipSpec? TailClip(Episode prev, PluginConfiguration cfg)
    {
        if (prev.RunTimeTicks is not > 0)
        {
            return null;
        }

        var total = TimeSpan.FromTicks(prev.RunTimeTicks.Value).TotalSeconds;
        var dur = Math.Max(5, cfg.PreviousClipSeconds);
        var end = total - Math.Max(0, cfg.CreditsOffsetSeconds);
        if (end < dur)
        {
            // short episode: just take the last chunk
            end = total;
        }

        var start = Math.Max(0, end - dur);
        return new ClipSpec(prev.Path, start, Math.Min(dur, total - start));
    }

    /// <summary>Evenly spaced clips over [0, resume]; the last one ends right at the resume point.</summary>
    public static IEnumerable<ClipSpec> WatchedPartClips(string path, TimeSpan pos, PluginConfiguration cfg)
    {
        var count = Math.Max(1, cfg.ThisEpisodeClips);
        var len = Math.Max(3, cfg.ThisEpisodeClipSeconds);
        var end = pos.TotalSeconds;
        if (end < len * 2)
        {
            yield break;
        }

        var lastStart = end - len;
        for (var i = 0; i < count; i++)
        {
            var start = count == 1 ? lastStart : lastStart * i / (count - 1);
            yield return new ClipSpec(path, Math.Max(0, start), len);
        }
    }
}
