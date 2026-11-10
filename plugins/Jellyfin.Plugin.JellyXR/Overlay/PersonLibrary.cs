using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JellyXR.Overlay;

/// <summary>A Jellyfin person, as the actor detail shows it.</summary>
public sealed record PersonInfo(Guid Id, string Name, string? Bio, DateTime? Born, string? BirthPlace);

/// <summary>A movie or series in the user's library. Kind is "Movie" or "Series".</summary>
public sealed record LibraryTitle(Guid Id, string Name, int? Year, string Kind, long? RunTimeTicks, int? SeasonCount, string? Overview, DateTime? Premiere);

/// <summary>The other titles a person appears in, as visible to one user.</summary>
public static class PersonLibrary
{
    /// <summary>Most titles one detail lists (keeps the layout and its poster preloads bounded).</summary>
    public const int MaxTitles = 40;

    /// <summary>
    /// Movies, series and the series of episodes, one entry per id, without the current movie or the current
    /// episode's series, newest first (undated last).
    /// </summary>
    public static IReadOnlyList<LibraryTitle> Rollup(
        IEnumerable<LibraryTitle> movies,
        IEnumerable<(Guid SeriesId, LibraryTitle Series)> episodeSeries,
        IEnumerable<LibraryTitle> series,
        Guid currentItemId,
        Guid? currentSeriesId)
    {
        var seen = new HashSet<Guid>();
        var all = new List<LibraryTitle>();
        foreach (var t in movies.Concat(series).Concat(episodeSeries.Select(e => e.Series with { Id = e.SeriesId })))
        {
            if (t.Id == Guid.Empty || t.Id == currentItemId || t.Id == currentSeriesId || !seen.Add(t.Id))
            {
                continue;
            }

            all.Add(t);
        }

        return all
            .OrderBy(t => t.Premiere is null)
            .ThenByDescending(t => t.Premiere)
            .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The person and their other titles the user may see (the query carries the user: library access and
    /// parental rating apply). <paramref name="user"/> is the server's User entity, typed object because its
    /// type moved between Jellyfin versions (see <see cref="LibraryCompat"/>). Null when the person is unknown.
    /// </summary>
    public static Task<(PersonInfo Person, IReadOnlyList<LibraryTitle> Titles)?> LoadAsync(
        ILibraryManager lib, Guid personId, object user, Guid currentItemId, CancellationToken ct)
        => Task.Run(() => Load(lib, personId, user, currentItemId, ct), ct);

    private static (PersonInfo, IReadOnlyList<LibraryTitle>)? Load(ILibraryManager lib, Guid personId, object user, Guid currentItemId, CancellationToken ct)
    {
        if (lib.GetItemById(personId) is not Person person)
        {
            return null;
        }

        var info = new PersonInfo(
            person.Id,
            person.Name ?? string.Empty,
            string.IsNullOrWhiteSpace(person.Overview) ? null : person.Overview.Trim(),
            person.PremiereDate,
            person.ProductionLocations?.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)));

        var query = new InternalItemsQuery
        {
            PersonIds = new[] { personId },
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode },
            Recursive = true,
            IsVirtualItem = false,
        }.ForUser(user);
        var items = lib.ItemsOf(query).ToList();
        ct.ThrowIfCancellationRequested();

        var seriesTitles = new Dictionary<Guid, LibraryTitle?>();
        LibraryTitle? SeriesTitle(Guid id, Series? known)
        {
            if (!seriesTitles.TryGetValue(id, out var t))
            {
                var s = known ?? lib.GetItemById(id) as Series;
                seriesTitles[id] = t = s is null ? null : Title(s, SeasonCount(lib, s.Id, user));
            }

            return t;
        }

        var movies = items.OfType<Movie>().Select(m => Title(m, null));
        var series = items.OfType<Series>().Select(s => SeriesTitle(s.Id, s)).OfType<LibraryTitle>().ToList();
        var episodeSeries = items.OfType<Episode>()
            .Where(e => e.SeriesId != Guid.Empty)
            .Select(e => e.SeriesId)
            .Distinct()
            .Select(id => (id, SeriesTitle(id, null)))
            .Where(p => p.Item2 is not null)
            .Select(p => (p.id, p.Item2!))
            .ToList();
        var currentSeries = lib.GetItemById(currentItemId) is Episode cur && cur.SeriesId != Guid.Empty ? cur.SeriesId : (Guid?)null;

        var titles = Rollup(movies, episodeSeries, series, currentItemId, currentSeries);
        return (info, titles.Count > MaxTitles ? titles.Take(MaxTitles).ToList() : titles);
    }

    private static LibraryTitle Title(BaseItem item, int? seasons)
    {
        var year = item.ProductionYear ?? item.PremiereDate?.Year;
        var premiere = item.PremiereDate ?? (year is { } y and > 0 and < 10000 ? new DateTime(y, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null);
        return new LibraryTitle(
            item.Id,
            item.Name ?? string.Empty,
            year,
            item is Series ? "Series" : "Movie",
            item.RunTimeTicks is > 0 ? item.RunTimeTicks : null,
            seasons,
            string.IsNullOrWhiteSpace(item.Overview) ? null : item.Overview.Trim(),
            premiere);
    }

    /// <summary>Regular seasons (no specials) of a series the user may see.</summary>
    private static int? SeasonCount(ILibraryManager lib, Guid seriesId, object user)
    {
        var seasons = lib.ItemsOf(new InternalItemsQuery
        {
            ParentId = seriesId,
            IncludeItemTypes = new[] { BaseItemKind.Season },
            IsVirtualItem = false,
        }.ForUser(user)).Count(s => s.IndexNumber is not 0);
        return seasons > 0 ? seasons : null;
    }
}
