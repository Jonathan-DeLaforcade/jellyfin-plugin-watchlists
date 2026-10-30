using Jellyfin.Plugin.Watchlists.Data;
using Jellyfin.Plugin.Watchlists.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jellyfin.Plugin.Watchlists
{
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.TryAddSingleton<IWatchlistsSettings, PluginSettings>();
            serviceCollection.AddSingleton<WatchlistStore>();
            serviceCollection.AddSingleton<LibraryIndex>();
            serviceCollection.AddSingleton<SeerrClient>();
            serviceCollection.AddSingleton<TagSyncService>();
            serviceCollection.AddHostedService(sp => sp.GetRequiredService<TagSyncService>());
            serviceCollection.AddSingleton<WatchlistService>();
            serviceCollection.AddHostedService<SeenWatcher>();
            // TagSyncTask (IScheduledTask) est découvert automatiquement par Jellyfin dans l'assembly.
            serviceCollection.AddHostedService<PluginPagesRegistration>();
        }
    }
}
