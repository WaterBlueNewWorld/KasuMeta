using Jellyfin.Plugin.KasuMeta.Library;
using Jellyfin.Plugin.KasuMeta.Search;

namespace Jellyfin.Plugin.KasuMeta.Storage;

/// <summary>Reads and writes KasuMeta's localized metadata.</summary>
public interface ILocalizedMetadataStore
{
    /// <summary>
    /// Inserts or replaces text values. An unlocked write skips rows that are locked.
    /// </summary>
    /// <returns>How many rows were actually written (skipped locked rows don't count).</returns>
    Task<int> UpsertFieldsAsync(IReadOnlyCollection<LocalizedField> fields, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces, in one transaction, every unlocked row this <paramref name="source"/> holds for
    /// the item with <paramref name="fields"/>. Rows the provider no longer has go away (they may
    /// belong to a title the item was identified as before); locked rows and other sources stay.
    /// </summary>
    /// <returns>How many rows were written.</returns>
    Task<int> ReplaceProviderFieldsAsync(
        Guid itemId,
        string source,
        IReadOnlyCollection<LocalizedField> fields,
        CancellationToken cancellationToken);

    /// <summary>Gets the stored text values of the given items, optionally only for some locales.</summary>
    Task<IReadOnlyList<LocalizedField>> GetFieldsAsync(
        IReadOnlyCollection<Guid> itemIds,
        IReadOnlyCollection<string>? locales,
        CancellationToken cancellationToken);

    /// <summary>Inserts or replaces images, with the same locking rule as fields.</summary>
    /// <returns>How many rows were actually written.</returns>
    Task<int> UpsertImagesAsync(IReadOnlyCollection<LocalizedImage> images, CancellationToken cancellationToken);

    /// <summary>Same as <see cref="ReplaceProviderFieldsAsync"/>, for images.</summary>
    /// <returns>How many rows were written.</returns>
    Task<int> ReplaceProviderImagesAsync(
        Guid itemId,
        string source,
        IReadOnlyCollection<LocalizedImage> images,
        CancellationToken cancellationToken);

    /// <summary>Gets the stored images of the given items, optionally only for some locales.</summary>
    Task<IReadOnlyList<LocalizedImage>> GetImagesAsync(
        IReadOnlyCollection<Guid> itemIds,
        IReadOnlyCollection<string>? locales,
        CancellationToken cancellationToken);

    /// <summary>Records an item's fetch state, replacing any earlier one.</summary>
    Task SetFetchStateAsync(FetchState state, CancellationToken cancellationToken);

    /// <summary>Gets an item's fetch state, or null if the item was never queued.</summary>
    Task<FetchState?> GetFetchStateAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets items never fetched or last fetched before <paramref name="fetchedBefore"/>, oldest
    /// first; with <paramref name="includeErrors"/>, failed items whatever their age. Skipped
    /// items (nothing to fetch) are never stale: a provider-id change re-queues them.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetStaleItemIdsAsync(
        DateTimeOffset fetchedBefore,
        int limit,
        CancellationToken cancellationToken,
        bool includeErrors = false);

    /// <summary>
    /// Gets which of the given items have a fetch state with at least
    /// <paramref name="minFetchVersion"/> (0: any state at all).
    /// </summary>
    Task<IReadOnlySet<Guid>> GetKnownItemIdsAsync(
        IReadOnlyCollection<Guid> itemIds,
        CancellationToken cancellationToken,
        int minFetchVersion = 0);

    /// <summary>
    /// Finds localized names matching the query, in any locale, best match first: through the
    /// full-text index when it exists, by substring otherwise. Only items of the given kinds, or
    /// whose kind isn't recorded yet. Several rows may belong to one item (one per matching
    /// locale); the caller ranks, de-duplicates and filters by visibility.
    /// </summary>
    Task<IReadOnlyList<NameHit>> SearchNamesAsync(
        SearchQuery query,
        IReadOnlySet<ItemKind> kinds,
        int maxCandidates,
        CancellationToken cancellationToken);

    /// <summary>Removes everything stored for an item (it left the library).</summary>
    Task DeleteItemAsync(Guid itemId, CancellationToken cancellationToken);
}
