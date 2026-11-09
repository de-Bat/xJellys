using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Jellycap;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<TextTranslator>();
        serviceCollection.AddSingleton<TextCardFactory>();
        serviceCollection.AddSingleton<RecapPlanner>();
        serviceCollection.AddSingleton<FfmpegRecapBuilder>();
        serviceCollection.AddSingleton<RecapService>();
        serviceCollection.AddSingleton<OverlayStore>();
        serviceCollection.AddHostedService<RecapCoordinator>();
    }
}
