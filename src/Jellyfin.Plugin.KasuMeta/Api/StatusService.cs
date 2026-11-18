using System.Globalization;
using Jellyfin.Plugin.KasuMeta.Api.Models;
using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Fetching;
using Jellyfin.Plugin.KasuMeta.Library;
using Jellyfin.Plugin.KasuMeta.Providers;
using Jellyfin.Plugin.KasuMeta.Storage;

namespace Jellyfin.Plugin.KasuMeta.Api;

/// <summary>Builds the admin status: configuration health, fetch progress, recent errors, provider counters.</summary>
public sealed class StatusService
{
    /// <summary>How many recent failures the status lists.</summary>
    public const int RecentErrorCount = 10;

    private readonly ILocalizedMetadataStore _store;
    private readonly KasuMetaDatabase _database;
    private readonly IFetchQueue _queue;
    private readonly ProviderStats _stats;
    private readonly ILibraryItemSource _library;
    private readonly IKasuMetaConfigProvider _config;

    public StatusService(
        ILocalizedMetadataStore store,
        KasuMetaDatabase database,
        IFetchQueue queue,
        ProviderStats stats,
        ILibraryItemSource library,
        IKasuMetaConfigProvider config)
    {
        _store = store;
        _database = database;
        _queue = queue;
        _stats = stats;
        _library = library;
        _config = config;
    }

    /// <summary>Builds the status.</summary>
    public async Task<KasuMetaStatus> BuildAsync(CancellationToken cancellationToken)
    {
        var config = _config.Current;
        var summary = await _store.GetFetchSummaryAsync(RecentErrorCount, cancellationToken).ConfigureAwait(false);
        int Count(FetchStatus status) => summary.Counts.GetValueOrDefault(status);

        return new KasuMetaStatus
        {
            Version = _config.PluginVersion.ToString(),
            SchemaVersion = _database.SchemaVersion,
            SearchIndex = _database.SearchIndexReady,
            TmdbConfigured = !string.IsNullOrWhiteSpace(config.TmdbApiKey),
            AniListEnabled = config.AniListEnabled,
            Queued = _queue.Pending,
            Items = new ItemCounts(
                summary.Counts.Values.Sum(),
                Count(FetchStatus.Ok),
                Count(FetchStatus.Skipped),
                Count(FetchStatus.Error),
                Count(FetchStatus.Pending)),
            RecentErrors = summary.RecentErrors
                .Select(e => new FetchProblem(
                    e.ItemId.ToString("N", CultureInfo.InvariantCulture),
                    _library.GetItem(e.ItemId)?.Name,
                    e.Kind?.ToString(),
                    e.LastError,
                    e.LastFetch))
                .ToList(),
            Providers = _stats.Snapshot(),
        };
    }
}
