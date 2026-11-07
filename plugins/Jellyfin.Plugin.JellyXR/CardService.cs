using Jellyfin.Plugin.JellyXR.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyXR;

public readonly record struct Card(string Actor, string? Character);

public readonly record struct CardSet(string Header, IReadOnlyList<Card> Cards);

/// <summary>Decides who to show for an item and pause position.</summary>
public sealed class CardService
{
    private readonly FaceScanner _scanner;
    private readonly TimelineStore _store;
    private readonly XrayarrClient _xrayarr;
    private readonly ILogger<CardService> _logger;

    public CardService(FaceScanner scanner, TimelineStore store, XrayarrClient xrayarr, ILogger<CardService> logger)
    {
        _scanner = scanner;
        _store = store;
        _xrayarr = xrayarr;
        _logger = logger;
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

        var max = Math.Max(1, cfg.MaxCards);
        CardSet? Make(string header, List<Card> cards) =>
            cards.Count == 0 ? null : new CardSet(header, cards.Take(max).ToList());

        switch (scope)
        {
            case CastScope.Episode:
                return Make("Cast", WholeCast());
            case CastScope.Scene:
                return Make("On screen", await SceneCards().ConfigureAwait(false));
            default: // Current
                return Make("On screen", await SceneCards().ConfigureAwait(false)) ?? Make("Cast", WholeCast());
        }
    }

    public static string Format(Card card, bool showCharacter) =>
        showCharacter && card.Character is not null ? $"{card.Actor} as {card.Character}" : card.Actor;
}
