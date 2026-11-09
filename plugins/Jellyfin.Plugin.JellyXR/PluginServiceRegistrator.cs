using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JellyXR;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ModelManager>();
        serviceCollection.AddSingleton<FaceEngine>();
        serviceCollection.AddSingleton<CastIndex>();
        serviceCollection.AddSingleton<TimelineStore>();
        serviceCollection.AddSingleton<XrayarrClient>();
        serviceCollection.AddSingleton<MarkerService>();
        serviceCollection.AddSingleton<FaceScanner>();
        serviceCollection.AddSingleton<CastTimelineService>();
        serviceCollection.AddSingleton<CardService>();
        serviceCollection.AddHostedService<PauseCoordinator>();
        serviceCollection.AddHostedService<WebInjector>();
    }
}
