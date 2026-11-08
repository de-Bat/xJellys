using System.Collections.Concurrent;
using System.Reflection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Jellycap;

/// <summary>
/// ILibraryManager calls whose return type changed between Jellyfin versions
/// (List&lt;T&gt; in 10.9/10.10, IReadOnlyList&lt;T&gt; in 10.11). A direct call binds to the
/// compile-time signature and throws MissingMethodException on other servers, so bind by name.
/// </summary>
internal static class LibraryCompat
{
    private static readonly ConcurrentDictionary<string, MethodInfo?> Methods = new();

    public static IEnumerable<BaseItem> ItemsOf(this ILibraryManager library, InternalItemsQuery query)
        => Invoke<BaseItem>(library, "GetItemList", query);

    public static IEnumerable<PersonInfo> GetPeopleOf(this ILibraryManager library, BaseItem item)
        => Invoke<PersonInfo>(library, "GetPeople", item);

    private static IEnumerable<T> Invoke<T>(ILibraryManager library, string name, object arg)
    {
        var argType = arg is BaseItem ? typeof(BaseItem) : arg.GetType();
        var method = Methods.GetOrAdd(name + ":" + argType.FullName, _ => typeof(ILibraryManager).GetMethods()
            .FirstOrDefault(m => m.Name == name && m.GetParameters() is [var p] && p.ParameterType == argType));
        if (method is null)
        {
            throw new MissingMethodException(typeof(ILibraryManager).FullName, name);
        }

        return method.Invoke(library, new[] { arg }) as IEnumerable<T> ?? Enumerable.Empty<T>();
    }
}
