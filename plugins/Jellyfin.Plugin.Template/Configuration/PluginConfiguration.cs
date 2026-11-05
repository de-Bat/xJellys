using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Template.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public string Message { get; set; } = "Hello";
}
