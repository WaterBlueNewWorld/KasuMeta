using Jellyfin.Data.Enums;
using Jellyfin.Plugin.KasuMeta.Localization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.KasuMeta.Library;

/// <summary><see cref="ILibraryItemSource"/> over Jellyfin's <see cref="ILibraryManager"/>.</summary>
/// <remarks>
/// Uses only APIs present in both Jellyfin 10.11 and 12.0, so a 10.11 build stays possible, except
/// <c>BaseItem.OriginalLanguage</c> (12.0), kept to <see cref="OriginalLanguage"/>.
/// </remarks>
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

    public IReadOnlyList<LibraryItemInfo> GetVisibleItems(Guid? userId, IReadOnlyCollection<Guid> itemIds)
    {
        var user = userId is { } id ? _userManager.GetUserById(id) : null;
        if (userId is not null && user is null)
        {
            return [];
        }

        var visible = new List<LibraryItemInfo>(itemIds.Count);
        foreach (var itemId in itemIds)
        {
            var item = _libraryManager.GetItemById(itemId);
            if (item is null || (user is not null && !item.IsVisibleStandalone(user)))
            {
                continue;
            }

            if (FromItem(item) is { } info)
            {
                visible.Add(info);
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

        var info = item switch
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
        if (info is null)
        {
            return null;
        }

        // Seasons and episodes rarely carry a language of their own: their series' stands in.
        var parent = item switch
        {
            Season season => season.Series,
            Episode episode => episode.Series,
            _ => null,
        };
        return info with
        {
            Name = item.Name,
            OriginalLanguage = OriginalLanguage(item) ?? OriginalLanguage(parent),
        };
    }

    /// <summary>
    /// The item's original language, normalized (<c>ja</c>), or null. <c>BaseItem.OriginalLanguage</c>
    /// is new in Jellyfin 12 (PLAN.md §8): this is its only use.
    /// </summary>
    private static string? OriginalLanguage(BaseItem? item) =>
        LocaleChain.TryNormalize(item?.OriginalLanguage, out var locale) ? locale : null;
}
