using Jellyfin.Plugin.YouTube.Channels;
using Jellyfin.Plugin.YouTube.Providers;
using Jellyfin.Plugin.YouTube.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.YouTube;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient("YouTube");
        serviceCollection.AddHostedService<YouTubeStartupService>();
        serviceCollection.AddSingleton<YouTubePlaylistAndCollectionSyncService>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<YouTubePlaylistAndCollectionSyncService>());
        serviceCollection.AddSingleton<IChannel, YouTubeChannel>();
        serviceCollection.AddSingleton<IMediaSourceProvider, YouTubeMediaSourceProvider>();
        serviceCollection.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(options =>
        {
            options.Filters.Add<YouTubePlaybackFilter>();
        });
    }
}
