using Jellyfin.Plugin.KasuMeta.Library;
using Jellyfin.Plugin.KasuMeta.Storage;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KasuMeta.Fetching;

/// <summary>
/// Follows the library: new and updated items are queued (the fetcher then skips updates that
/// didn't change the provider ids), removed items lose their rows.
/// </summary>
/// <remarks>
/// ItemAdded often fires before Jellyfin has fetched the item's metadata, so the first fetch may
/// find no TMDB id and record "skipped"; the ItemUpdated that follows the metadata refresh
/// changes the ids and queues it again.
/// </remarks>
internal sealed class LibraryEventsService : IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IFetchQueue _queue;
    private readonly ILocalizedMetadataStore _store;
    private readonly ILogger<LibraryEventsService> _logger;

    public LibraryEventsService(
        ILibraryManager libraryManager,
        IFetchQueue queue,
        ILocalizedMetadataStore store,
        ILogger<LibraryEventsService> logger)
    {
        _libraryManager = libraryManager;
        _queue = queue;
        _store = store;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        _libraryManager.ItemRemoved += OnItemRemoved;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
        _libraryManager.ItemRemoved -= OnItemRemoved;
        return Task.CompletedTask;
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        // Runs on Jellyfin's thread: only cheap work here.
        if (JellyfinLibraryItemSource.FromItem(e.Item) is { } item)
        {
            _queue.Enqueue(item.Id);
        }
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is { } item)
        {
            _ = DeleteAsync(item.Id);
        }
    }

    private async Task DeleteAsync(Guid itemId)
    {
        try
        {
            await _store.DeleteItemAsync(itemId, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Fire-and-forget from an event: log, never throw into Jellyfin.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            FetchLog.DeleteFailed(_logger, ex, itemId);
        }
    }
}
