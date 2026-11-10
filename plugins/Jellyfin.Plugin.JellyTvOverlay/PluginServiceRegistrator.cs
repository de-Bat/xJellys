using Jellyfin.Plugin.JellyTvOverlay.Api;
using Jellyfin.Plugin.JellyTvOverlay.Host;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTvOverlay;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<OverlayRuntime>>();
            return new OverlayRuntime(TimeProvider.System, message => logger.LogInformation("{Message}", message));
        });
        serviceCollection.AddSingleton<OverlayApi>();
        serviceCollection.AddHostedService<SessionBridge>();
    }
}
