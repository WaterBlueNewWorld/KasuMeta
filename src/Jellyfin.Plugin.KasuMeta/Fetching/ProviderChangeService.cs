using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KasuMeta.Fetching;

/// <summary>
/// Re-queues the whole library when an admin changes which providers each category comes from,
/// or sets up a provider a category lists (<see cref="SourceRules.Active"/>). The queue isn't
/// forced: the fetcher compares each item's snapshot, which carries the provider choice, so only
/// items fetched under another choice are fetched again. Saving the page without changing the
/// choice queues nothing.
/// </summary>
/// <remarks>
/// A real change costs one request per item and provider, as a first install does.
/// </remarks>
internal sealed class ProviderChangeService : IHostedService
{
    private readonly IConfigurationChanges _changes;
    private readonly IKasuMetaConfigProvider _config;
    private readonly ILibraryItemSource _library;
    private readonly IFetchQueue _queue;
    private readonly ILogger<ProviderChangeService> _logger;
    private readonly object _lock = new();
    private string? _order;

    public ProviderChangeService(
        IConfigurationChanges changes,
        IKasuMetaConfigProvider config,
        ILibraryItemSource library,
        IFetchQueue queue,
        ILogger<ProviderChangeService> logger)
    {
        _changes = changes;
        _config = config;
        _library = library;
        _queue = queue;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _order = Describe(_config.Current);
        _changes.Changed += OnChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _changes.Changed -= OnChanged;
        return Task.CompletedTask;
    }

    /// <summary>Queues every item if the order changed; returns how many were queued (tests call it directly).</summary>
    internal int Apply(PluginConfiguration config)
    {
        var order = Describe(config);
        lock (_lock)
        {
            if (order == _order)
            {
                return 0;
            }

            _order = order;
        }

        var ids = _library.ListAllIds();
        var queued = ids.Count(id => _queue.Enqueue(id));
        FetchLog.ProvidersChanged(_logger, order, queued, ids.Count);
        return queued;
    }

    private static string Describe(PluginConfiguration config) => SourceRules.Describe(SourceRules.Active(config));

    private void OnChanged(object? sender, PluginConfiguration config)
    {
        // Runs on the request that saved the page: list ids and queue them off that thread.
        _ = Task.Run(() =>
        {
            try
            {
                Apply(config);
            }
#pragma warning disable CA1031 // Fire-and-forget from an event: log, never throw into Jellyfin.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                FetchLog.ProvidersChangeFailed(_logger, ex);
            }
        });
    }
}
