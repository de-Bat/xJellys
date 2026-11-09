using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyXR.Configuration;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyXR;

/// <summary>
/// Marks movies and episodes that have X-Ray data with Jellyfin metadata the Android TV app shows as is:
/// a tag, membership in one collection and (opt-in) a genre. Clients cannot run plugin UI, so this is the badge.
/// </summary>
public sealed class MarkerService
{
    private readonly ILibraryManager _library;
    private readonly ICollectionManager _collections;
    private readonly TimelineStore _store;
    private readonly XrayarrClient _xrayarr;
    private readonly ILogger<MarkerService> _logger;
    private readonly SemaphoreSlim _collectionLock = new(1, 1);

    public MarkerService(ILibraryManager library, ICollectionManager collections, TimelineStore store, XrayarrClient xrayarr, ILogger<MarkerService> logger)
    {
        _library = library;
        _collections = collections;
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
            await ApplyTagsAsync(item, has, cfg, ct).ConfigureAwait(false);

            var boxSet = await CollectionAsync(cfg, create: has).ConfigureAwait(false);
            if (boxSet is null)
            {
                return;
            }

            var inSet = boxSet.LinkedChildren.Any(c => c.ItemId == item.Id);
            if (has && !inSet)
            {
                await _collections.AddToCollectionAsync(boxSet.Id, new[] { item.Id }).ConfigureAwait(false);
            }
            else if (!has && inSet)
            {
                await _collections.RemoveFromCollectionAsync(boxSet.Id, new[] { item.Id }).ConfigureAwait(false);
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
    }

    /// <summary>Fire and forget variant of <see cref="SyncItemAsync"/>.</summary>
    public void Queue(BaseItem item) => _ = Task.Run(() => SyncItemAsync(item, CancellationToken.None));

    /// <summary>Mark every movie and episode with X-Ray data, unmark the rest.</summary>
    public async Task SyncAllAsync(IEnumerable<BaseItem> items, IProgress<double> progress, CancellationToken ct)
    {
        var cfg = Plugin.Instance!.Configuration;
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

        var boxSet = await CollectionAsync(cfg, create: marked.Count > 0).ConfigureAwait(false);
        if (boxSet is not null)
        {
            var current = boxSet.LinkedChildren.Where(c => c.ItemId.HasValue).Select(c => c.ItemId!.Value).ToHashSet();
            var want = marked.ToHashSet();
            var add = want.Where(id => !current.Contains(id)).ToList();
            var remove = current.Where(id => !want.Contains(id)).ToList();
            if (add.Count > 0)
            {
                await _collections.AddToCollectionAsync(boxSet.Id, add).ConfigureAwait(false);
            }

            if (remove.Count > 0)
            {
                await _collections.RemoveFromCollectionAsync(boxSet.Id, remove).ConfigureAwait(false);
            }

            _logger.LogInformation("X-Ray collection \"{Name}\": {Add} added, {Remove} removed", boxSet.Name, add.Count, remove.Count);
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

    /// <summary>The X-Ray collection, created when missing and <paramref name="create"/> is set.</summary>
    private async Task<BoxSet?> CollectionAsync(PluginConfiguration cfg, bool create)
    {
        var name = MarkerName(cfg);
        await _collectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var existing = _library.ItemsOf(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.BoxSet },
                Recursive = true
            }).OfType<BoxSet>().FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null || !create || !cfg.MarkItems)
            {
                return existing;
            }

            _logger.LogInformation("Creating collection \"{Name}\" for items with X-Ray data", name);
            return await _collections.CreateCollectionAsync(new CollectionCreationOptions { Name = name, IsLocked = true }).ConfigureAwait(false);
        }
        finally
        {
            _collectionLock.Release();
        }
    }
}
