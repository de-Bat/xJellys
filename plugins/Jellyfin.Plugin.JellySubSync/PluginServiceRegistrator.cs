using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JellySubSync;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<SubSyncStore>();
        serviceCollection.AddSingleton<SubSyncService>();
        serviceCollection.AddSingleton<Overlay.OverlayClientHost>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<Overlay.OverlayClientHost>());
    }
}
