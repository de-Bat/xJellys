using System.Runtime.CompilerServices;
using Jellyfin.Plugin.JellyTvOverlay.Configuration;
using Jellyfin.Plugin.JellyTvOverlay.Delivery;
using Jellyfin.Plugin.JellyTvOverlay.Host;

[assembly: InternalsVisibleTo("Jellyfin.Plugin.JellyTvOverlay.Tests")]

namespace Jellyfin.Plugin.JellyTvOverlay;

/// <summary>
/// The public host API other plugins call (BCL types only; this is the third-party contract).
/// Before the session bridge starts, registrations are kept and applied on start; Show returns false.
/// </summary>
public static class OverlayHost
{
    public const int ProtocolVersion = 1;

    private sealed record Registration(string DisplayName, Func<string, Task<string?>> OnEvent);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Registration> Registrations = new(StringComparer.Ordinal);
    private static Dispatcher? _dispatcher;
    private static Func<string, bool>? _hasApp;

    /// <summary>Settings source; tests replace it.</summary>
    internal static Func<PluginConfiguration?> ConfigSource { get; set; } = () => Plugin.Instance?.Configuration;

    public static bool Register(string clientId, string displayName, Func<string, Task<string?>> onEvent)
    {
        if (!Dispatcher.IsValidClientId(clientId) || onEvent is null)
        {
            return false;
        }

        lock (Gate)
        {
            Registrations[clientId] = new Registration(displayName ?? clientId, onEvent);
            return _dispatcher?.Register(clientId, displayName ?? clientId, onEvent) ?? true;
        }
    }

    public static bool Show(string clientId, string deviceId, string layoutJson)
    {
        var d = Volatile.Read(ref _dispatcher);
        if (d is null || string.IsNullOrEmpty(deviceId) || !IsEnabled() || !IsDeviceEnabled(deviceId))
        {
            return false;
        }

        return d.Show(clientId, deviceId, layoutJson);
    }

    public static void Close(string clientId, string deviceId, string layoutId)
    {
        var d = Volatile.Read(ref _dispatcher);
        if (d is null || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(layoutId))
        {
            return;
        }

        d.Close(clientId, deviceId, layoutId);
    }

    /// <summary>True when the overlay app on the device sent a heartbeat within 90 s and the device is switched on.</summary>
    public static bool HasApp(string deviceId)
    {
        var has = Volatile.Read(ref _hasApp);
        return has is not null && !string.IsNullOrEmpty(deviceId) && IsEnabled() && IsDeviceEnabled(deviceId) && has(deviceId);
    }

    internal static void Attach(Dispatcher d, OverlayQueue q, Func<string, bool> hasApp)
    {
        ArgumentNullException.ThrowIfNull(q);
        lock (Gate)
        {
            foreach (var (id, r) in Registrations)
            {
                d.Register(id, r.DisplayName, r.OnEvent);
            }

            _hasApp = hasApp;
            _dispatcher = d;
        }
    }

    internal static void Detach()
    {
        lock (Gate)
        {
            _dispatcher = null;
            _hasApp = null;
        }
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Detach();
            Registrations.Clear();
            ConfigSource = () => Plugin.Instance?.Configuration;
        }
    }

    internal static IReadOnlyList<(string Id, string DisplayName)> Registered()
    {
        lock (Gate)
        {
            return Registrations.Select(kv => (kv.Key, kv.Value.DisplayName)).OrderBy(r => r.Key, StringComparer.Ordinal).ToList();
        }
    }

    internal static bool IsEnabled() => ConfigSource()?.Enabled ?? true;

    internal static bool IsDeviceEnabled(string deviceId)
        => ConfigSource()?.DisabledDevices is not { } off || !off.Contains(deviceId, StringComparer.Ordinal);
}
