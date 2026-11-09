using System.Reflection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JellySubSync;

/// <summary>
/// Server APIs whose types changed between Jellyfin versions. A direct call binds to the compile-time (10.9)
/// signature and throws MissingMethodException / MissingFieldException on newer servers, so bind by name:
/// - User moved from Jellyfin.Data.Entities to Jellyfin.Database.Implementations.Entities (10.11+), and
///   BaseItem.IsVisible(User) became IsVisible(User, bool) (12.x).
/// - Video.PrimaryVersionId changed from string to Guid (12.x).
/// </summary>
internal static class ServerCompat
{
    private static MethodInfo? _getUser;
    private static MethodInfo? _isVisible;

    /// <summary>Whether the user may see <paramref name="item"/> (parental rating, library access, tags).</summary>
    public static bool IsVisibleTo(BaseItem item, IUserManager users, Guid userId)
    {
        _getUser ??= typeof(IUserManager).GetMethod("GetUserById", new[] { typeof(Guid) })
            ?? throw new MissingMethodException(typeof(IUserManager).FullName, "GetUserById");
        var user = _getUser.Invoke(users, new object[] { userId });
        if (user is null)
        {
            return false;
        }

        _isVisible ??= typeof(BaseItem).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "IsVisible" && m.ReturnType == typeof(bool))
            .Where(m => m.GetParameters() is { Length: >= 1 } p && p[0].ParameterType.IsInstanceOfType(user))
            .OrderBy(m => m.GetParameters().Length)
            .FirstOrDefault()
            ?? throw new MissingMethodException(typeof(BaseItem).FullName, "IsVisible");

        // extra parameters (12.x: skip the allowed-tags check) get their defaults: the full check
        var ps = _isVisible.GetParameters();
        var args = new object?[ps.Length];
        args[0] = user;
        for (var i = 1; i < ps.Length; i++)
        {
            args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue
                : ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null;
        }

        return (bool)_isVisible.Invoke(item, args)!;
    }

    /// <summary>Id of the primary version for an alternate version of a video, else Guid.Empty.</summary>
    public static Guid PrimaryVersionId(BaseItem item)
    {
        var value = item.GetType().GetProperty("PrimaryVersionId", BindingFlags.Public | BindingFlags.Instance)?.GetValue(item);
        return value switch
        {
            Guid g => g,
            string s when Guid.TryParse(s, out var g) => g,
            _ => Guid.Empty
        };
    }
}
