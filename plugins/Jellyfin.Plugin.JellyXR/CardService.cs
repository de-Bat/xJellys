using Jellyfin.Plugin.JellyXR.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyXR;

/// <param name="Info">Appearance summary ("34 min on screen, next at 1:12:40"), when enabled and known.</param>
/// <param name="OnScreen">Seen around the pause position (scene data), as opposed to the rest of the cast.</param>
/// <param name="PersonId">Jellyfin person item, when the actor is in the item's people.</param>
/// <param name="ImageUrl">Server-relative portrait URL, when the person has a primary image.</param>
/// <param name="Canonical">Jellyfin's name for the person when <paramref name="Actor"/> was translated; person lookups use it.</param>
/// <param name="TmdbId">TMDB person id, when known (from Xrayarr or Jellyfin).</param>
public readonly record struct Card(string Actor, string? Character, string? Info = null, bool OnScreen = false, Guid? PersonId = null, string? ImageUrl = null, string? Canonical = null, int? TmdbId = null);

public readonly record struct CardSet(string Header, IReadOnlyList<Card> Cards);

/// <summary>Decides who to show for an item and pause position.</summary>
public sealed class CardService
{
    private readonly FaceScanner _scanner;
    private readonly TimelineStore _store;
    private readonly XrayarrClient _xrayarr;
    private readonly CastTimelineService _timelines;
    private readonly TmdbLocalizer _localizer;
    private readonly ILogger<CardService> _logger;

    public CardService(FaceScanner scanner, TimelineStore store, XrayarrClient xrayarr, CastTimelineService timelines, TmdbLocalizer localizer, ILogger<CardService> logger)
    {
        _localizer = localizer;
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
        // TMDB id of each cast member, looked up once and only when Xrayarr hands us ids to compare with
        Dictionary<string, int?>? castTmdb = null;
        int? TmdbOf(PersonInfo p)
        {
            castTmdb ??= new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
            if (!castTmdb.TryGetValue(p.Name, out var id))
            {
                castTmdb[p.Name] = id = _timelines.TmdbIdOf(p.Name);
            }

            return id;
        }

        // The id is the best key: it survives a different spelling or language between Jellyfin and Xrayarr. Name is the fallback.
        Card? ToCard(string name, string? character, bool keepUnknown, int? tmdbPersonId = null)
        {
            var p = tmdbPersonId is { } want ? cast.FirstOrDefault(c => TmdbOf(c) == want) : null;
            p ??= cast.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (p is null)
            {
                return keepUnknown ? new Card(name, string.IsNullOrWhiteSpace(character) ? null : character, TmdbId: tmdbPersonId) : null;
            }

            var role = string.IsNullOrWhiteSpace(p.Role) ? character : p.Role;
            return new Card(p.Name, string.IsNullOrWhiteSpace(role) ? null : role, TmdbId: tmdbPersonId ?? (castTmdb is null ? null : TmdbOf(p)));
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
                            .Select(p => ToCard(p.Name, p.Character, true, p.TmdbPersonId)).OfType<Card>().ToList();
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

        var result = set is { } s && cfg.ShowAppearance ? await WithAppearanceAsync(item, s, sec, cfg, ct).ConfigureAwait(false) : set;
        return result is { } r ? await LocalizeAsync(item, r, cfg, ct).ConfigureAwait(false) : result;
    }

    /// <summary>Swaps actor and character for the configured language; whatever TMDB lacks stays as it was.</summary>
    private async Task<CardSet> LocalizeAsync(BaseItem item, CardSet set, Configuration.PluginConfiguration cfg, CancellationToken ct)
    {
        if (!TmdbLocalizer.Active(cfg))
        {
            return set;
        }

        try
        {
            var credits = await _localizer.GetAsync(item, cfg, ct).ConfigureAwait(false);
            if (credits.Count == 0)
            {
                return set;
            }

            var cards = set.Cards.Select(c =>
            {
                var id = c.TmdbId ?? _timelines.TmdbIdOf(c.Actor);
                if (id is not { } tmdb || !credits.TryGetValue(tmdb, out var l))
                {
                    return c;
                }

                var name = string.IsNullOrWhiteSpace(l.Name) ? c.Actor : l.Name!;
                var role = string.IsNullOrWhiteSpace(l.Character) ? c.Character : l.Character;
                return c with { Actor = name, Character = role, Canonical = c.Actor, TmdbId = tmdb };
            }).ToList();
            return set with { Cards = cards };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Localizing cards for {Item} failed: {Error}", item.Name, ex.Message);
            return set;
        }
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

    /// <summary>
    /// Adds the Jellyfin person id and portrait URL to cards whose actor is in the item's people (matched by name).
    /// Only the TV overlay panel needs them, so the toast path does not pay for the lookups.
    /// </summary>
    public CardSet WithPeople(BaseItem item, CardSet set)
    {
        var cast = _scanner.CastOf(item);
        var cards = set.Cards.Select(c =>
        {
            var name = c.Canonical ?? c.Actor;
            if (!cast.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return c;
            }

            var (id, hasImage) = _timelines.PersonOf(name);
            return id is { } g ? c with { PersonId = g, ImageUrl = hasImage ? PortraitUrl(g) : null } : c;
        }).ToList();
        return set with { Cards = cards };
    }

    public static string PortraitUrl(Guid personId) => $"/Items/{personId:N}/Images/Primary?maxHeight=128";

    public static string Format(Card card, bool showCharacter)
    {
        var who = showCharacter && card.Character is not null ? $"{card.Actor} as {card.Character}" : card.Actor;
        return card.Info is null ? who : $"{who} ({card.Info})";
    }
}
