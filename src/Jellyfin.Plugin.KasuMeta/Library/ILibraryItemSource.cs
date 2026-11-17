namespace Jellyfin.Plugin.KasuMeta.Library;

/// <summary>KasuMeta's view of Jellyfin's library.</summary>
public interface ILibraryItemSource
{
    /// <summary>Gets an item, or null if it doesn't exist, is virtual, or isn't a kind KasuMeta handles.</summary>
    LibraryItemInfo? GetItem(Guid itemId);

    /// <summary>Gets the ids of every real movie, series, season and episode.</summary>
    IReadOnlyList<Guid> ListAllIds();

    /// <summary>
    /// Keeps only the ids the user may see (library access, parental rating, tags). Unknown ids
    /// are dropped too, so a caller can't tell "hidden" from "doesn't exist".
    /// </summary>
    IReadOnlyList<Guid> FilterVisible(Guid userId, IReadOnlyCollection<Guid> itemIds);
}
