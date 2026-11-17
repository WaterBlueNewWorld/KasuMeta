using Jellyfin.Plugin.KasuMeta.Providers.Tmdb;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KasuMeta.Fetching;

/// <summary>
/// Drains the fetch queue for the life of the server, as many items at a time as the TMDB
/// client lets through (it throttles itself).
/// </summary>
internal sealed class FetchWorker : BackgroundService
{
    private readonly IFetchQueue _queue;
    private readonly MetadataFetcher _fetcher;
    private readonly ILogger<FetchWorker> _logger;

    public FetchWorker(IFetchQueue queue, MetadataFetcher fetcher, ILogger<FetchWorker> logger)
    {
        _queue = queue;
        _fetcher = fetcher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = TmdbClient.MaxConcurrency,
            CancellationToken = stoppingToken,
        };

        try
        {
            await Parallel.ForEachAsync(_queue.ReadAllAsync(stoppingToken), options, ProcessAsync).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Server shutting down; the queue is rebuilt by the backfill task on the next start.
        }
    }

    private async ValueTask ProcessAsync((Guid ItemId, bool Force) work, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _fetcher.FetchAsync(work.ItemId, work.Force, cancellationToken).ConfigureAwait(false);
            FetchLog.Fetched(_logger, work.ItemId, result, _queue.Pending);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // One bad item (e.g. a database hiccup) must not stop the worker.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            FetchLog.Crashed(_logger, ex, work.ItemId);
        }
    }
}
