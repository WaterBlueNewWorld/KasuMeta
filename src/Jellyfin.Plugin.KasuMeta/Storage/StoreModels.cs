using Jellyfin.Plugin.KasuMeta.Library;

namespace Jellyfin.Plugin.KasuMeta.Storage;

/// <summary>Text fields KasuMeta localizes. The values are what the store and the API use.</summary>
public static class MetadataFields
{
    public const string Name = "name";
    public const string Overview = "overview";
    public const string Tagline = "tagline";
    public const string SortName = "sort_name";

    /// <summary>Gets every known field.</summary>
    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Name, Overview, Tagline, SortName };
}

/// <summary>Image types KasuMeta localizes, named as Jellyfin's <c>ImageType</c> names them.</summary>
public static class LocalizedImageTypes
{
    public const string Primary = "Primary";
    public const string Logo = "Logo";
    public const string Backdrop = "Backdrop";
    public const string Thumb = "Thumb";

    /// <summary>Gets every known image type.</summary>
    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Primary, Logo, Backdrop, Thumb };
}

/// <summary>Where a value came from.</summary>
public static class MetadataSources
{
    public const string Tmdb = "tmdb";
    public const string Tvdb = "tvdb";
    public const string AniList = "anilist";
    public const string Manual = "manual";
}

/// <summary>One localized text value of one item.</summary>
/// <param name="ItemId">The Jellyfin item id.</param>
/// <param name="Locale">BCP-47 locale, e.g. <c>es-MX</c>.</param>
/// <param name="Field">One of <see cref="MetadataFields"/>.</param>
/// <param name="Value">The localized text.</param>
/// <param name="Source">One of <see cref="MetadataSources"/>.</param>
/// <param name="Locked">
/// A manual override. Provider writes never replace a locked row; writing with
/// <c>Locked = true</c> always does.
/// </param>
public sealed record LocalizedField(
    Guid ItemId,
    string Locale,
    string Field,
    string Value,
    string Source,
    bool Locked = false)
{
    /// <summary>Gets when the row was last written. Set by the store; ignored on writes.</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>One localized image of one item.</summary>
/// <param name="ItemId">The Jellyfin item id.</param>
/// <param name="Locale">BCP-47 locale.</param>
/// <param name="ImageType">One of <see cref="LocalizedImageTypes"/>.</param>
/// <param name="Url">Absolute URL of the image.</param>
/// <param name="Source">One of <see cref="MetadataSources"/>.</param>
/// <param name="Locked">Same rule as <see cref="LocalizedField.Locked"/>.</param>
/// <param name="Width">Pixel width of the image at <paramref name="Url"/>, if known.</param>
/// <param name="Height">Pixel height, if known.</param>
public sealed record LocalizedImage(
    Guid ItemId,
    string Locale,
    string ImageType,
    string Url,
    string Source,
    bool Locked = false,
    int? Width = null,
    int? Height = null)
{
    /// <summary>Gets when the row was last written. Set by the store; ignored on writes.</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>A localized name that matched a search.</summary>
/// <param name="ItemId">The Jellyfin item id.</param>
/// <param name="Locale">The locale of the matching name.</param>
/// <param name="Name">The matching name.</param>
/// <param name="Score">FTS5's BM25 rank (lower is better); 0 for substring matches.</param>
/// <param name="Kind">The item's kind, or null if its last fetch predates kinds being stored.</param>
public sealed record NameHit(Guid ItemId, string Locale, string Name, double Score, ItemKind? Kind = null);

/// <summary>Outcome of the last fetch for an item.</summary>
public enum FetchStatus
{
    /// <summary>Queued, not fetched yet.</summary>
    Pending,

    /// <summary>Fetched successfully.</summary>
    Ok,

    /// <summary>The last fetch failed; see <see cref="FetchState.LastError"/>.</summary>
    Error,

    /// <summary>
    /// Nothing to fetch: no TMDB id, or TMDB doesn't know the id (see
    /// <see cref="FetchState.LastError"/>). Retried when the provider ids change.
    /// </summary>
    Skipped,
}

/// <summary>What KasuMeta knows about fetching an item.</summary>
/// <param name="ItemId">The Jellyfin item id.</param>
/// <param name="ProviderIds">
/// The item's provider ids at fetch time (e.g. <c>Tmdb → 129</c>). A change means a re-fetch.
/// </param>
/// <param name="LastFetch">When the last fetch finished, or null if never.</param>
/// <param name="Status">The outcome of that fetch.</param>
/// <param name="LastError">The error message when <paramref name="Status"/> is Error.</param>
/// <param name="FetchVersion">
/// Which fetch logic produced this state (1 = text only, 2 = adds images, 3 = adds the item's
/// kind). An item below the current version is fetched again, so new data reaches items fetched
/// by older builds.
/// </param>
/// <param name="Kind">The item's kind, or null if not recorded yet.</param>
public sealed record FetchState(
    Guid ItemId,
    IReadOnlyDictionary<string, string> ProviderIds,
    DateTimeOffset? LastFetch,
    FetchStatus Status,
    string? LastError = null,
    int FetchVersion = 1,
    ItemKind? Kind = null);
