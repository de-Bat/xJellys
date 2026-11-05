using Jellyfin.Plugin.JellyXR.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.JellyXR;

public readonly record struct Card(string Actor, string? Character);

public readonly record struct CardSet(string Header, IReadOnlyList<Card> Cards);

/// <summary>Decides who to show for an item and pause position.</summary>
public sealed class CardService
{
    private readonly FaceScanner _scanner;
    private readonly TimelineStore _store;

    public CardService(FaceScanner scanner, TimelineStore store)
    {
        _scanner = scanner;
        _store = store;
    }

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

    public CardSet? Build(BaseItem item, long positionTicks, Configuration.PluginConfiguration cfg)
    {
        var scope = ScopeFor(item, cfg);
        if (scope == CastScope.Off)
        {
            return null;
        }

        var cast = _scanner.CastOf(item);
        if (cast.Count == 0)
        {
            return null;
        }

        string[] SceneNames()
        {
            var tl = _store.Load(item.Id);
            if (tl is null)
            {
                return Array.Empty<string>();
            }

            var sec = (int)TimeSpan.FromTicks(positionTicks).TotalSeconds;
            return TimelineStore.PeopleBetween(tl, sec - cfg.SceneBackSeconds, sec + cfg.SceneForwardSeconds).ToArray();
        }

        IReadOnlyList<Card> ToCards(IEnumerable<string> names)
        {
            var list = new List<Card>();
            foreach (var n in names)
            {
                var p = cast.FirstOrDefault(c => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase));
                if (p is not null)
                {
                    list.Add(new Card(p.Name, string.IsNullOrWhiteSpace(p.Role) ? null : p.Role));
                }
            }

            return list;
        }

        var max = Math.Max(1, cfg.MaxCards);
        switch (scope)
        {
            case CastScope.Episode:
                return new CardSet("Cast", ToCards(cast.Select(c => c.Name)).Take(max).ToList());
            case CastScope.Scene:
            {
                var cards = ToCards(SceneNames()).Take(max).ToList();
                return cards.Count == 0 ? null : new CardSet("On screen", cards);
            }

            default: // Current
            {
                var cards = ToCards(SceneNames()).Take(max).ToList();
                return cards.Count > 0
                    ? new CardSet("On screen", cards)
                    : new CardSet("Cast", ToCards(cast.Select(c => c.Name)).Take(max).ToList());
            }
        }
    }

    public static string Format(Card card, bool showCharacter) =>
        showCharacter && card.Character is not null ? $"{card.Actor} as {card.Character}" : card.Actor;
}
