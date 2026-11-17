using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.KasuMeta.Library;

/// <summary><see cref="ILibraryItemSource"/> over Jellyfin's <see cref="ILibraryManager"/>.</summary>
/// <remarks>Uses only APIs present in both Jellyfin 10.11 and 12.0, so a 10.11 build stays possible.</remarks>
internal sealed class JellyfinLibraryItemSource : ILibraryItemSource
{
    private static readonly BaseItemKind[] Kinds =
        [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode];

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    public JellyfinLibraryItemSource(ILibraryManager libraryManager, IUserManager userManager)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
    }

    public LibraryItemInfo? GetItem(Guid itemId) => FromItem(_libraryManager.GetItemById(itemId));

    public IReadOnlyList<Guid> ListAllIds()
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = Kinds,
            IsVirtualItem = false,
            Recursive = true,
        });
        return items.Select(i => i.Id).ToList();
    }

    public IReadOnlyList<Guid> FilterVisible(Guid userId, IReadOnlyCollection<Guid> itemIds)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return [];
        }

        var visible = new List<Guid>(itemIds.Count);
        foreach (var id in itemIds)
        {
            var item = _libraryManager.GetItemById(id);
            if (item is not null && item.IsVisibleStandalone(user))
            {
                visible.Add(id);
            }
        }

        return visible;
    }

    /// <summary>Maps a Jellyfin item, or returns null for kinds KasuMeta doesn't handle.</summary>
    internal static LibraryItemInfo? FromItem(BaseItem? item)
    {
        if (item is null || item.IsVirtualItem)
        {
            return null;
        }

        IReadOnlyDictionary<string, string> Ids(BaseItem? source) =>
            source?.ProviderIds is { } ids
                ? new Dictionary<string, string>(ids, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);

        return item switch
        {
            Movie movie => new LibraryItemInfo(movie.Id, ItemKind.Movie, Ids(movie), Ids(null)),
            Series series => new LibraryItemInfo(series.Id, ItemKind.Series, Ids(series), Ids(null)),
            Season season => new LibraryItemInfo(
                season.Id, ItemKind.Season, Ids(season), Ids(season.Series), SeasonNumber: season.IndexNumber),
            Episode episode => new LibraryItemInfo(
                episode.Id,
                ItemKind.Episode,
                Ids(episode),
                Ids(episode.Series),
                SeasonNumber: episode.ParentIndexNumber ?? episode.Season?.IndexNumber,
                EpisodeNumber: episode.IndexNumber),
            _ => null,
        };
    }
}
