namespace Jellyfin.Plugin.KasuMeta.Storage;

/// <summary>Reads and writes KasuMeta's localized metadata.</summary>
public interface ILocalizedMetadataStore
{
    /// <summary>
    /// Inserts or replaces text values. An unlocked write skips rows that are locked.
    /// </summary>
    /// <returns>How many rows were actually written (skipped locked rows don't count).</returns>
    Task<int> UpsertFieldsAsync(IReadOnlyCollection<LocalizedField> fields, CancellationToken cancellationToken);

    /// <summary>Gets the stored text values of the given items, optionally only for some locales.</summary>
    Task<IReadOnlyList<LocalizedField>> GetFieldsAsync(
        IReadOnlyCollection<Guid> itemIds,
        IReadOnlyCollection<string>? locales,
        CancellationToken cancellationToken);

    /// <summary>Inserts or replaces images, with the same locking rule as fields.</summary>
    /// <returns>How many rows were actually written.</returns>
    Task<int> UpsertImagesAsync(IReadOnlyCollection<LocalizedImage> images, CancellationToken cancellationToken);

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
    /// Gets items never fetched or last fetched before <paramref name="fetchedBefore"/>,
    /// oldest first.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetStaleItemIdsAsync(
        DateTimeOffset fetchedBefore,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>Removes everything stored for an item (it left the library).</summary>
    Task DeleteItemAsync(Guid itemId, CancellationToken cancellationToken);
}
