using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyTvOverlay.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;

    /// <summary>Device ids switched off on the settings page: no overlays there, clients fall back.</summary>
    public string[] DisabledDevices { get; set; } = Array.Empty<string>();
}
