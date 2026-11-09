using System.Reflection;
using Jellyfin.Plugin.JellySubSync.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JellySubSync;

/// <summary>Decides whether a title gets subtitle sync, from the Scope setting.</summary>
public static class SyncScopeFilter
{
    private static MethodInfo? _collectionFolders;

    public static bool Applies(BaseItem item, PluginConfiguration cfg, ILibraryManager library)
    {
        switch (cfg.Scope)
        {
            case SyncScope.All:
                return true;
            case SyncScope.Selected:
                break;
            default:
                return false;
        }

        var media = new HashSet<Guid>(cfg.Media.Select(m => Guid.TryParse(m.Id, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty));
        // an alternate version (mediaSourceId) counts as its primary item
        var ids = new[] { item.Id, ServerCompat.PrimaryVersionId(item) };
        if (ids.Any(media.Contains))
        {
            return true;
        }

        if (item is Episode ep && media.Contains(ep.SeriesId))
        {
            return true;
        }

        if (cfg.LibraryIds.Length == 0)
        {
            return false;
        }

        var libs = new HashSet<Guid>(cfg.LibraryIds.Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty));
        return CollectionFolders(library, item).Any(f => libs.Contains(f.Id));
    }

    /// <summary>
    /// ILibraryManager.GetCollectionFolders(BaseItem): List&lt;Folder&gt; in 10.9/10.10, may be IReadOnlyList on newer
    /// servers. Bound by name so one build runs on both.
    /// </summary>
    private static IEnumerable<BaseItem> CollectionFolders(ILibraryManager library, BaseItem item)
    {
        _collectionFolders ??= typeof(ILibraryManager).GetMethods()
            .FirstOrDefault(m => m.Name == "GetCollectionFolders" && m.GetParameters() is [var p] && p.ParameterType == typeof(BaseItem))
            ?? throw new MissingMethodException(typeof(ILibraryManager).FullName, "GetCollectionFolders");
        return _collectionFolders.Invoke(library, new object[] { item }) as IEnumerable<BaseItem> ?? Enumerable.Empty<BaseItem>();
    }
}
