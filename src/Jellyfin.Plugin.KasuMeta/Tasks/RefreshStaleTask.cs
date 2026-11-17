using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Fetching;
using Jellyfin.Plugin.KasuMeta.Storage;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KasuMeta.Tasks;

/// <summary>
/// Re-fetches translations older than the configured refresh interval, and retries failed
/// fetches whatever their age (a missing or rejected key, a TMDB outage). Items with nothing to
/// fetch are left alone; a provider-id change re-queues them.
/// </summary>
public sealed class RefreshStaleTask : IScheduledTask
{
    /// <summary>Most items queued per run, so one run can't flood TMDB for hours.</summary>
    internal const int MaxPerRun = 5_000;

    private readonly ILocalizedMetadataStore _store;
    private readonly IFetchQueue _queue;
    private readonly IKasuMetaConfigProvider _config;
    private readonly TimeProvider _time;
    private readonly ILogger<RefreshStaleTask> _logger;

    public RefreshStaleTask(
        ILocalizedMetadataStore store,
        IFetchQueue queue,
        IKasuMetaConfigProvider config,
        TimeProvider time,
        ILogger<RefreshStaleTask> logger)
    {
        _store = store;
        _queue = queue;
        _config = config;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Refresh stale translations";

    /// <inheritdoc />
    public string Key => "KasuMetaRefreshStale";

    /// <inheritdoc />
    public string Description =>
        "Re-fetches translations older than the refresh interval and retries failed fetches.";

    /// <inheritdoc />
    public string Category => "KasuMeta";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var hours = Math.Max(1, _config.Current.RefreshIntervalHours);
        var before = _time.GetUtcNow().AddHours(-hours);
        var stale = await _store
            .GetStaleItemIdsAsync(before, MaxPerRun, cancellationToken, includeErrors: true)
            .ConfigureAwait(false);
        progress.Report(50);

        var queued = stale.Count(id => _queue.Enqueue(id, force: true));
        FetchLog.TaskQueued(_logger, Name, queued, stale.Count);
        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(24).Ticks },
    ];
}
