namespace Jellyfin.Plugin.KasuMeta.Library;

/// <summary>The item kinds KasuMeta localizes.</summary>
public enum ItemKind
{
    Movie,
    Series,
    Season,
    Episode,
}

/// <summary>
/// What KasuMeta needs to know about a library item, taken from Jellyfin's <c>BaseItem</c> so the
/// fetch logic can be tested without Jellyfin's entity classes.
/// </summary>
/// <param name="Id">The Jellyfin item id.</param>
/// <param name="Kind">Movie, series, season or episode.</param>
/// <param name="ProviderIds">The item's own provider ids (<c>Tmdb → 9989</c>).</param>
/// <param name="SeriesProviderIds">For seasons and episodes, the series' provider ids; else empty.</param>
/// <param name="SeasonNumber">Season number (seasons and episodes).</param>
/// <param name="EpisodeNumber">Episode number (episodes).</param>
public sealed record LibraryItemInfo(
    Guid Id,
    ItemKind Kind,
    IReadOnlyDictionary<string, string> ProviderIds,
    IReadOnlyDictionary<string, string> SeriesProviderIds,
    int? SeasonNumber = null,
    int? EpisodeNumber = null)
{
    /// <summary>Gets the item's name in the library (Jellyfin's own, not localized), for display only.</summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets the inputs a fetch depends on, as saved in <c>fetch_state</c>: the item's provider ids,
    /// plus for seasons and episodes the series' TMDB id and the numbers. When this changes, the
    /// stored translations may belong to another title and must be fetched again.
    /// </summary>
    public IReadOnlyDictionary<string, string> FetchSnapshot()
    {
        var snapshot = new Dictionary<string, string>(ProviderIds, StringComparer.Ordinal);
        if (Kind is ItemKind.Season or ItemKind.Episode)
        {
            if (SeriesProviderIds.TryGetValue(ProviderNames.Tmdb, out var seriesTmdb))
            {
                snapshot["Series.Tmdb"] = seriesTmdb;
            }

            if (SeasonNumber is { } season)
            {
                snapshot["Season"] = season.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (EpisodeNumber is { } episode)
            {
                snapshot["Episode"] = episode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return snapshot;
    }
}

/// <summary>Provider id keys, as Jellyfin names them in <c>ProviderIds</c>.</summary>
public static class ProviderNames
{
    public const string Tmdb = "Tmdb";
    public const string Tvdb = "Tvdb";
    public const string Imdb = "Imdb";
    public const string AniList = "AniList";
}
