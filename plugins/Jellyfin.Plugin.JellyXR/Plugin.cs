using Jellyfin.Plugin.JellyXR.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.JellyXR;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        PluginLog.Init(applicationPaths.LogDirectoryPath);
        DataDirectory = Path.Combine(applicationPaths.DataPath, "jellyxr");
        Directory.CreateDirectory(DataDirectory);
    }

    public static Plugin? Instance { get; private set; }

    /// <summary>Models, cast reference embeddings and face timelines live here.</summary>
    public string DataDirectory { get; }

    public override string Name => "JellyXR";

    public override Guid Id => Guid.Parse("8f3c1d52-4b7e-4a0e-9d1a-6c2e5b7a9f10");

    public override string Description => "X-Ray style cast cards on pause for remote-controllable clients such as Android TV.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
        };
    }
}
