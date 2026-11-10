using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyTvOverlay.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;
}
