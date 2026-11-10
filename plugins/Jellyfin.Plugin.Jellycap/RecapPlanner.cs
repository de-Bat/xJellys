using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Jellycap.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.Jellycap;

/// <summary>Decides which clips and text cards make up a recap.</summary>
public class RecapPlanner
{
    /// <summary>Dialogue quoted per watched moment in a text-only recap.</summary>
    private const double TextMomentSeconds = 20;

    private readonly ILibraryManager _libraryManager;
    private readonly TextCardFactory _cards;
    private readonly EpisodeDialogue _dialogue;

    public RecapPlanner(ILibraryManager libraryManager, TextCardFactory cards, EpisodeDialogue dialogue)
    {
        _libraryManager = libraryManager;
        _cards = cards;
        _dialogue = dialogue;
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
            Recursive = true
        };

        // Sorted here, not via InternalItemsQuery.OrderBy: its SortOrder enum moved from
        // Jellyfin.Data.Enums to Jellyfin.Database.Implementations.Enums in 10.11, so
        // referencing it throws TypeLoadException on newer servers.
        return _libraryManager.ItemsOf(query)
            .OfType<Episode>()
            .OrderBy(e => e.ParentIndexNumber ?? int.MaxValue)
            .ThenBy(e => e.IndexNumber ?? int.MaxValue)
            .ThenBy(e => e.PremiereDate ?? DateTime.MaxValue)
            .ToList();
    }

    public Episode? GetNext(Episode episode)
    {
        var all = GetSeriesEpisodes(episode);
        var idx = IndexOf(all, episode);
        return idx >= 0 && idx + 1 < all.Count ? all[idx + 1] : null;
    }

    public async Task<IReadOnlyList<RecapSegment>> PlanAsync(Episode episode, RecapMode mode, TimeSpan resumePosition, RecapFormat fmt, PluginConfiguration cfg, CancellationToken ct)
    {
        var segments = new List<RecapSegment>();
        var withText = fmt != RecapFormat.Video;
        var withVideo = fmt != RecapFormat.Text;
        var all = GetSeriesEpisodes(episode);
        var idx = IndexOf(all, episode);

        if (mode != RecapMode.ResumeThisEpisode && idx > 0 && cfg.PreviousEpisodes > 0)
        {
            var isSpecial = (episode.ParentIndexNumber ?? 1) == 0;
            var previous = all.Take(idx)
                .Where(e => isSpecial || (e.ParentIndexNumber ?? 1) > 0)
                .Where(e => !withVideo || (!string.IsNullOrEmpty(e.Path) && File.Exists(e.Path)))
                .TakeLast(cfg.PreviousEpisodes)
                .ToList();

            if (withText && previous.Count > 0)
            {
                segments.Add(new TextCardSpec(Localization.Previously(cfg.TextLanguage, episode.SeriesName), null, 3));
            }

            foreach (var prev in previous)
            {
                if (withText)
                {
                    var card = await _cards.BuildAsync(prev, cfg, ct).ConfigureAwait(false);
                    if (card is not null)
                    {
                        segments.Add(card);
                    }
                }

                if (withVideo)
                {
                    var clip = TailClip(prev, cfg);
                    if (clip is not null)
                    {
                        segments.Add(clip);
                    }
                }
            }
        }

        if (mode is RecapMode.Resume or RecapMode.ResumeThisEpisode && !string.IsNullOrEmpty(episode.Path) && File.Exists(episode.Path))
        {
            var watched = await WatchedPartAsync(episode, resumePosition, withText, withVideo, cfg, ct).ConfigureAwait(false);
            if (watched.Count > 0)
            {
                if (withText)
                {
                    segments.Add(new TextCardSpec(Localization.EarlierInEpisode(cfg.TextLanguage), null, 2.5));
                }

                segments.AddRange(watched);
            }
        }

        // a lone heading card is not a recap
        return segments.Any(s => s is ClipSpec || s is TextCardSpec { Body: not null }) ? segments : Array.Empty<RecapSegment>();
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

    /// <summary>
    /// The watched part of a resumed episode, moment by moment: a card quoting the dialogue of the moment
    /// (text / both, from the episode's subtitles) and/or the clip itself (video / both).
    /// </summary>
    private async Task<IReadOnlyList<RecapSegment>> WatchedPartAsync(Episode episode, TimeSpan pos, bool withText, bool withVideo, PluginConfiguration cfg, CancellationToken ct)
    {
        // a text-only moment has no clip to watch, so it quotes a longer stretch of dialogue
        var len = withVideo ? (double?)null : Math.Max(TextMomentSeconds, cfg.ThisEpisodeClipSeconds);
        var moments = WatchedPartClips(episode.Path, pos, cfg, len).ToList();
        if (moments.Count == 0)
        {
            return Array.Empty<RecapSegment>();
        }

        var dialogue = withText ? await _dialogue.GetAsync(episode, cfg.TextLanguage, ct).ConfigureAwait(false) : null;
        var translate = dialogue is not null && !EpisodeDialogue.SameLanguage(dialogue.Language, cfg.TextLanguage);
        var code = string.Create(CultureInfo.InvariantCulture, $"S{episode.ParentIndexNumber ?? 0:00}E{episode.IndexNumber ?? 0:00}");
        var list = new List<RecapSegment>();
        foreach (var m in moments)
        {
            if (dialogue is not null)
            {
                var quote = EpisodeDialogue.Quote(dialogue.Cues, m.StartSeconds, m.StartSeconds + m.DurationSeconds);
                if (quote.Length > 0)
                {
                    if (translate)
                    {
                        quote = await _cards.TranslateAsync(quote, cfg, ct).ConfigureAwait(false);
                    }

                    list.Add(new TextCardSpec($"{code} · {Stamp(m.StartSeconds)}", quote, TextCardFactory.ReadingSeconds(quote)));
                }
            }

            if (withVideo)
            {
                list.Add(m);
            }
        }

        return list;
    }

    private static string Stamp(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>Evenly spaced clips over [0, resume]; the last one ends right at the resume point.</summary>
    public static IEnumerable<ClipSpec> WatchedPartClips(string path, TimeSpan pos, PluginConfiguration cfg, double? seconds = null)
    {
        var count = Math.Max(1, cfg.ThisEpisodeClips);
        var len = Math.Max(3, seconds ?? cfg.ThisEpisodeClipSeconds);
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
