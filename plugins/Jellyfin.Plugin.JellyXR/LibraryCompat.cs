using System.Collections.Concurrent;
using System.Reflection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JellyXR;

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

    private static MethodInfo? _getUserById;
    private static MethodInfo? _setUser;
    private static PropertyInfo? _userProperty;

    /// <summary>
    /// The server's User entity for <paramref name="userId"/>, or null. Typed object: the User type moved from
    /// Jellyfin.Data.Entities (10.9/10.10) to Jellyfin.Database.Implementations.Entities (10.11).
    /// </summary>
    public static object? UserById(this IUserManager users, Guid userId)
    {
        _getUserById ??= typeof(IUserManager).GetMethod("GetUserById", new[] { typeof(Guid) })
            ?? throw new MissingMethodException(typeof(IUserManager).FullName, "GetUserById");
        return _getUserById.Invoke(users, new object[] { userId });
    }

    /// <summary>
    /// Scopes the query to the user (library access, parental rating, blocked tags), as new InternalItemsQuery(user)
    /// does: SetUser(user), then the User property (library access). Throws when either does not bind, so a query
    /// never runs unscoped.
    /// </summary>
    public static InternalItemsQuery ForUser(this InternalItemsQuery query, object user)
    {
        ArgumentNullException.ThrowIfNull(user);
        _setUser ??= typeof(InternalItemsQuery).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "SetUser" && m.GetParameters() is [var p] && p.ParameterType.IsInstanceOfType(user));
        _userProperty ??= typeof(InternalItemsQuery).GetProperty("User", BindingFlags.Public | BindingFlags.Instance);
        if (_setUser is null)
        {
            // without SetUser the parental rating and tag limits would not apply: fail closed
            throw new MissingMemberException(typeof(InternalItemsQuery).FullName, "SetUser");
        }

        if (_userProperty is null || !_userProperty.CanWrite || !_userProperty.PropertyType.IsInstanceOfType(user))
        {
            throw new MissingMemberException(typeof(InternalItemsQuery).FullName, "User");
        }

        _setUser.Invoke(query, new[] { user });
        _userProperty.SetValue(query, user);
        return query;
    }

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
