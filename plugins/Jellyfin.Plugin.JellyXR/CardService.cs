using Jellyfin.Plugin.JellyXR.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyXR;

/// <param name="Info">Appearance summary ("34 min on screen, next at 1:12:40"), when enabled and known.</param>
/// <param name="OnScreen">Seen around the pause position (scene data), as opposed to the rest of the cast.</param>
public readonly record struct Card(string Actor, string? Character, string? Info = null, bool OnScreen = false);

public readonly record struct CardSet(string Header, IReadOnlyList<Card> Cards);

/// <summary>Decides who to show for an item and pause position.</summary>
public sealed class CardService
{
    private readonly FaceScanner _scanner;
    private readonly TimelineStore _store;
    private readonly XrayarrClient _xrayarr;
    private readonly CastTimelineService _timelines;
    private readonly ILogger<CardService> _logger;

    public CardService(FaceScanner scanner, TimelineStore store, XrayarrClient xrayarr, CastTimelineService timelines, ILogger<CardService> logger)
    {
        _timelines = timelines;
        _scanner = scanner;
        _store = store;
        _xrayarr = xrayarr;
        _logger = PluginLog.Wrap(logger);
    }

    /// <summary>Scene data sources in the order they are tried.</summary>
    public static DataSource[] SourcesFor(Configuration.PluginConfiguration cfg) => cfg.DataSource switch
    {
        DataSource.Xrayarr => new[] { DataSource.Xrayarr },
        DataSource.XrayarrThenLocal => new[] { DataSource.Xrayarr, DataSource.Local },
        DataSource.LocalThenXrayarr => new[] { DataSource.Local, DataSource.Xrayarr },
        _ => new[] { DataSource.Local }
    };

    public static CastScope ScopeFor(BaseItem item, Configuration.PluginConfiguration cfg)
    {
        if (item is Episode ep && ep.SeriesId != Guid.Empty)
        {
            var rule = cfg.SeriesRules?.FirstOrDefault(r => Guid.TryParse(r.SeriesId, out var g) && g == ep.SeriesId);
            if (rule is not null && rule.Scope != CastScope.Default)
            {
                return rule.Scope;
            }
        }

        return cfg.Scope == CastScope.Default ? CastScope.Current : cfg.Scope;
    }

    public async Task<CardSet?> BuildAsync(BaseItem item, long positionTicks, Configuration.PluginConfiguration cfg, CancellationToken ct)
    {
        var scope = ScopeFor(item, cfg);
        if (scope == CastScope.Off)
        {
            _logger.LogDebug("Cards off for {Item} (scope Off)", item.Name);
            return null;
        }

        var cast = _scanner.CastOf(item);
        var sec = (int)TimeSpan.FromTicks(positionTicks).TotalSeconds;

        // Jellyfin's cast supplies the canonical name and role; Xrayarr people missing from it are kept with Xrayarr's character.
        Card? ToCard(string name, string? character, bool keepUnknown)
        {
            var p = cast.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (p is null)
            {
                return keepUnknown ? new Card(name, string.IsNullOrWhiteSpace(character) ? null : character) : null;
            }

            var role = string.IsNullOrWhiteSpace(p.Role) ? character : p.Role;
            return new Card(p.Name, string.IsNullOrWhiteSpace(role) ? null : role);
        }

        // First source that has scene data for this item wins (even if nobody is on screen at this position).
        async Task<List<Card>> SceneCards()
        {
            foreach (var source in SourcesFor(cfg))
            {
                if (source == DataSource.Local)
                {
                    var tl = _store.Load(item.Id);
                    if (tl is { Entries.Count: > 0 })
                    {
                        _logger.LogDebug("Scene data for {Item} from local face scan", item.Name);
                        return TimelineStore.PeopleBetween(tl, sec - cfg.SceneBackSeconds, sec + cfg.SceneForwardSeconds)
                            .Select(n => ToCard(n, null, false)).OfType<Card>().ToList();
                    }
                }
                else
                {
                    var xr = await _xrayarr.GetAsync(item, ct).ConfigureAwait(false);
                    if (xr is { Segments.Count: > 0 })
                    {
                        _logger.LogDebug("Scene data for {Item} from Xrayarr", item.Name);
                        var ms = (long)TimeSpan.FromTicks(positionTicks).TotalMilliseconds;
                        return xr.PeopleAround(ms, ms - (cfg.SceneBackSeconds * 1000L), ms + (cfg.SceneForwardSeconds * 1000L))
                            .Select(p => ToCard(p.Name, p.Character, true)).OfType<Card>().ToList();
                    }
                }
            }

            _logger.LogDebug("No scene data for {Item} (sources: {Sources})", item.Name, string.Join(", ", SourcesFor(cfg)));
            return new List<Card>();
        }

        List<Card> WholeCast() => cast.Select(c => ToCard(c.Name, null, false)).OfType<Card>().ToList();

        // 0 = no limit
        var max = cfg.MaxCards <= 0 ? int.MaxValue : cfg.MaxCards;
        CardSet? Make(string header, List<Card> cards) =>
            cards.Count == 0 ? null : new CardSet(header, cards.Take(max).ToList());

        // Current: people on screen first, then (optionally) everyone else in the cast
        async Task<CardSet?> Current()
        {
            var scene = (await SceneCards().ConfigureAwait(false)).Select(c => c with { OnScreen = true }).ToList();
            if (scene.Count == 0)
            {
                return Make("Cast", WholeCast());
            }

            if (!cfg.AppendRestOfCast)
            {
                return Make("On screen", scene);
            }

            var rest = WholeCast().Where(c => !scene.Any(s => string.Equals(s.Actor, c.Actor, StringComparison.OrdinalIgnoreCase)));
            return Make("On screen", scene.Concat(rest).ToList());
        }

        var set = scope switch
        {
            CastScope.Episode => Make("Cast", WholeCast()),
            CastScope.Scene => Make("On screen", (await SceneCards().ConfigureAwait(false)).Select(c => c with { OnScreen = true }).ToList()),
            _ => await Current().ConfigureAwait(false)
        };

        return set is { } s && cfg.ShowAppearance ? await WithAppearanceAsync(item, s, sec, cfg, ct).ConfigureAwait(false) : set;
    }

    private async Task<CardSet> WithAppearanceAsync(BaseItem item, CardSet set, int sec, Configuration.PluginConfiguration cfg, CancellationToken ct)
    {
        try
        {
            var view = await _timelines.GetAsync(item, cfg, ct).ConfigureAwait(false);
            if (view is null)
            {
                return set;
            }

            var cards = set.Cards
                .Select(c => view.Find(c.Actor) is { } a ? c with { Info = CastTimelineService.Describe(a, sec) } : c)
                .ToList();
            return set with { Cards = cards };
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Appearance info for {Item} failed: {Error}", item.Name, ex.Message);
            return set;
        }
    }

    public static string Format(Card card, bool showCharacter)
    {
        var who = showCharacter && card.Character is not null ? $"{card.Actor} as {card.Character}" : card.Actor;
        return card.Info is null ? who : $"{who} ({card.Info})";
    }
}
