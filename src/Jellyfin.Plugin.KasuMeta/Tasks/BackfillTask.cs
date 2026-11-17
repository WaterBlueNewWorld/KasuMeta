using Jellyfin.Plugin.KasuMeta.Fetching;
using Jellyfin.Plugin.KasuMeta.Library;
using Jellyfin.Plugin.KasuMeta.Storage;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KasuMeta.Tasks;

/// <summary>
/// Queues every library item KasuMeta has never seen: the whole library on first install (the
/// library events only cover items added later), and whatever slipped by while the server was
/// down. Cheap when there is nothing new, so it also runs at startup. Jellyfin discovers it by
/// type; it shows under Dashboard › Scheduled Tasks › KasuMeta.
/// </summary>
public sealed class BackfillTask : IScheduledTask
{
    private readonly ILibraryItemSource _library;
    private readonly ILocalizedMetadataStore _store;
    private readonly IFetchQueue _queue;
    private readonly ILogger<BackfillTask> _logger;

    public BackfillTask(ILibraryItemSource library, ILocalizedMetadataStore store, IFetchQueue queue, ILogger<BackfillTask> logger)
    {
        _library = library;
        _store = store;
        _queue = queue;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Fetch missing translations";

    /// <inheritdoc />
    public string Key => "KasuMetaBackfill";

    /// <inheritdoc />
    public string Description =>
        "Queues every movie, series, season and episode KasuMeta has not fetched yet. The fetching itself continues in the background.";

    /// <inheritdoc />
    public string Category => "KasuMeta";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var ids = _library.ListAllIds();
        progress.Report(25);
        var known = await _store.GetKnownItemIdsAsync(ids, cancellationToken).ConfigureAwait(false);
        progress.Report(50);

        var queued = 0;
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!known.Contains(id) && _queue.Enqueue(id))
            {
                queued++;
            }
        }

        FetchLog.TaskQueued(_logger, Name, queued, ids.Count);
        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
        new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(24).Ticks },
    ];
}
