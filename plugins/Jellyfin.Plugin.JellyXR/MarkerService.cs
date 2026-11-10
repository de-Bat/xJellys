using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyXR.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyXR;

/// <summary>
/// Marks movies and episodes that have X-Ray data with Jellyfin metadata the Android TV app shows as is:
/// a tag and (opt-in) a genre. Clients cannot run plugin UI, so this is the badge.
/// Earlier versions also added the items to an "X-Ray" collection; that collection is deleted on the first sync.
/// </summary>
public sealed class MarkerService
{
    private readonly ILibraryManager _library;
    private readonly TimelineStore _store;
    private readonly XrayarrClient _xrayarr;
    private readonly ILogger<MarkerService> _logger;
    private int _collectionRemoved;

    public MarkerService(ILibraryManager library, TimelineStore store, XrayarrClient xrayarr, ILogger<MarkerService> logger)
    {
        _library = library;
        _store = store;
        _xrayarr = xrayarr;
        _logger = PluginLog.Wrap(logger);
    }

    private static string MarkerName(PluginConfiguration cfg) =>
        string.IsNullOrWhiteSpace(cfg.MarkerName) ? "X-Ray" : cfg.MarkerName.Trim();

    /// <summary>True when a configured source has scene data for the item.</summary>
    public async Task<bool> HasDataAsync(BaseItem item, PluginConfiguration cfg, CancellationToken ct)
    {
        foreach (var source in CardService.SourcesFor(cfg))
        {
            if (source == DataSource.Local)
            {
                if (_store.Load(item.Id) is { Entries.Count: > 0 })
                {
                    return true;
                }
            }
            else if (await _xrayarr.GetAsync(item, ct).ConfigureAwait(false) is { Segments.Count: > 0 })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Bring the markers of one item up to date. Never throws.</summary>
    public async Task SyncItemAsync(BaseItem item, CancellationToken ct)
    {
        try
        {
            var cfg = Plugin.Instance!.Configuration;
            if (item is not (Movie or Episode))
            {
                return;
            }

            var has = cfg.MarkItems && await HasDataAsync(item, cfg, ct).ConfigureAwait(false);
            RemoveLegacyCollection(cfg);
            await ApplyTagsAsync(item, has, cfg, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update X-Ray markers of {Item}", item.Name);
        }
    }

    /// <summary>Fire and forget variant of <see cref="SyncItemAsync"/>.</summary>
    public void Queue(BaseItem item) => _ = Task.Run(() => SyncItemAsync(item, CancellationToken.None));

    /// <summary>Mark every movie and episode with X-Ray data, unmark the rest.</summary>
    public async Task SyncAllAsync(IEnumerable<BaseItem> items, IProgress<double> progress, CancellationToken ct)
    {
        var cfg = Plugin.Instance!.Configuration;
        RemoveLegacyCollection(cfg);
        var list = items.Where(i => i is Movie or Episode).ToList();
        var marked = new List<Guid>();
        var tagged = 0;
        for (var i = 0; i < list.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = list[i];
            try
            {
                var has = cfg.MarkItems && await HasDataAsync(item, cfg, ct).ConfigureAwait(false);
                if (has)
                {
                    marked.Add(item.Id);
                }

                if (await ApplyTagsAsync(item, has, cfg, ct).ConfigureAwait(false))
                {
                    tagged++;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not update X-Ray markers of {Item}", item.Name);
            }

            progress.Report(90.0 * (i + 1) / Math.Max(1, list.Count));
        }

        _logger.LogInformation("X-Ray markers: {Marked} of {Count} item(s) have X-Ray data, {Changed} item(s) retagged", marked.Count, list.Count, tagged);
        progress.Report(100);
    }

    /// <summary>Add or remove the tag and the genre. True when the item changed.</summary>
    private static async Task<bool> ApplyTagsAsync(BaseItem item, bool has, PluginConfiguration cfg, CancellationToken ct)
    {
        var name = MarkerName(cfg);
        var tags = Toggle(item.Tags, name, has);
        var genres = Toggle(item.Genres, name, has && cfg.MarkGenre);
        if (tags is null && genres is null)
        {
            return false;
        }

        if (tags is not null)
        {
            item.Tags = tags;
        }

        if (genres is not null)
        {
            item.Genres = genres;
        }

        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>The array with <paramref name="value"/> present or absent, or null when it already is.</summary>
    private static string[]? Toggle(string[]? values, string value, bool present)
    {
        values ??= Array.Empty<string>();
        var has = values.Contains(value, StringComparer.OrdinalIgnoreCase);
        if (has == present)
        {
            return null;
        }

        return present
            ? values.Append(value).ToArray()
            : values.Where(v => !string.Equals(v, value, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    /// <summary>
    /// Delete the locked "X-Ray" collection that versions 1.5.0.0 and older created. Runs once per server start;
    /// a collection the user made (not locked) is left alone. Never throws.
    /// </summary>
    private void RemoveLegacyCollection(PluginConfiguration cfg)
    {
        if (Interlocked.Exchange(ref _collectionRemoved, 1) == 1)
        {
            return;
        }

        try
        {
            var name = MarkerName(cfg);
            var sets = _library.ItemsOf(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.BoxSet },
                Recursive = true
            }).OfType<BoxSet>().Where(b => b.IsLocked && string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var set in sets)
            {
                _logger.LogInformation("Deleting collection \"{Name}\": X-Ray items are no longer put in a collection", set.Name);
                _library.DeleteItem(set, new DeleteOptions { DeleteFileLocation = true });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete the old X-Ray collection");
        }
    }
}
