using Jellyfin.Plugin.KasuMeta.Api;
using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Fetching;
using Jellyfin.Plugin.KasuMeta.Library;
using Jellyfin.Plugin.KasuMeta.Providers.Tmdb;
using Jellyfin.Plugin.KasuMeta.Storage;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KasuMeta;

/// <summary>Registers KasuMeta's services in Jellyfin's container.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <summary>
    /// The database location under the server's data folder (<c>/config/data/kasumeta</c> in the
    /// Docker image). It isn't the plugin's own folder: Jellyfin names that folder after the plugin
    /// version, so the file would be lost on every update.
    /// </summary>
    internal static string DatabasePath(IServerApplicationPaths paths) =>
        Path.Combine(paths.DataPath, "kasumeta", "kasumeta.db");

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.TryAddSingleton(TimeProvider.System);
        serviceCollection.AddSingleton<IKasuMetaConfigProvider, PluginConfigProvider>();

        serviceCollection.AddSingleton(sp => new KasuMetaDatabase(
            DatabasePath(sp.GetRequiredService<IServerApplicationPaths>()),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<KasuMetaDatabase>>()));
        serviceCollection.AddSingleton<ILocalizedMetadataStore, SqliteLocalizedMetadataStore>();
        serviceCollection.AddHostedService<StoreStartupService>();

        // Providers. HttpClient comes from Jellyfin's own IHttpClientFactory (NamedClient.Default).
        serviceCollection.AddSingleton<ITmdbClient, TmdbClient>();

        // Library, fetching, API. The scheduled tasks (Tasks/) need no registration: Jellyfin
        // finds IScheduledTask types in plugin assemblies and builds them from this container.
        serviceCollection.AddSingleton<ILibraryItemSource, JellyfinLibraryItemSource>();
        serviceCollection.AddSingleton<IFetchQueue, FetchQueue>();
        serviceCollection.AddSingleton<MetadataFetcher>();
        serviceCollection.AddHostedService<FetchWorker>();
        serviceCollection.AddHostedService<LibraryEventsService>();
        serviceCollection.AddSingleton<OverlayService>();
    }
}
