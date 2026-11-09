using Jellyfin.Plugin.JellySubSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.JellySubSync;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        PluginLog.Init(applicationPaths.LogDirectoryPath);
        DataDirectory = Path.Combine(applicationPaths.DataPath, "jellysubsync");
        Directory.CreateDirectory(DataDirectory);
    }

    public static Plugin? Instance { get; private set; }

    /// <summary>Sync entries and extracted original subtitles live here.</summary>
    public string DataDirectory { get; }

    public override string Name => "JellySubSync";

    public override Guid Id => Guid.Parse("a50a5959-ae3c-433d-9698-5a62df894ed6");

    public override string Description => "Shifts subtitles earlier or later from the JellyXR TV app on Android TV.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
        };
    }
}
