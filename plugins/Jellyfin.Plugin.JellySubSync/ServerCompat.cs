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

    /// <summary>Whether the user is an administrator (User.HasPermission(PermissionKind.IsAdministrator), bound by name).</summary>
    public static bool IsAdministrator(IUserManager users, Guid userId)
    {
        _getUser ??= typeof(IUserManager).GetMethod("GetUserById", new[] { typeof(Guid) })
            ?? throw new MissingMethodException(typeof(IUserManager).FullName, "GetUserById");
        var user = _getUser.Invoke(users, new object[] { userId });
        var has = user?.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "HasPermission" && m.ReturnType == typeof(bool) && m.GetParameters() is [var p] && p.ParameterType.IsEnum);
        if (has is null)
        {
            return false;
        }

        var kind = has.GetParameters()[0].ParameterType;
        return Enum.TryParse(kind, "IsAdministrator", out var admin) && (bool)has.Invoke(user, new[] { admin })!;
    }

    /// <summary>
    /// Whether the session lists a remote control command (SessionInfo.SupportedCommands, else
    /// Capabilities.SupportedCommands), compared by name so the enum's assembly does not matter.
    /// </summary>
    public static bool SupportsCommand(object session, string command)
    {
        var list = session.GetType().GetProperty("SupportedCommands")?.GetValue(session) as System.Collections.IEnumerable;
        if (list is null)
        {
            var caps = session.GetType().GetProperty("Capabilities")?.GetValue(session);
            list = caps?.GetType().GetProperty("SupportedCommands")?.GetValue(caps) as System.Collections.IEnumerable;
        }

        return list?.Cast<object?>().Any(c => string.Equals(c?.ToString(), command, StringComparison.Ordinal)) == true;
    }

    /// <summary>GeneralCommand SetSubtitleStreamIndex with argument Index (Arguments filled by name: its type may change).</summary>
    public static MediaBrowser.Model.Session.GeneralCommand SetSubtitleCommand(string index)
    {
        var cmd = new MediaBrowser.Model.Session.GeneralCommand { Name = MediaBrowser.Model.Session.GeneralCommandType.SetSubtitleStreamIndex };
        if (typeof(MediaBrowser.Model.Session.GeneralCommand).GetProperty("Arguments")?.GetValue(cmd) is IDictionary<string, string> args)
        {
            args["Index"] = index;
        }

        return cmd;
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
