using Jellyfin.Plugin.KasuMeta.Library;

namespace Jellyfin.Plugin.KasuMeta.Storage;

/// <summary>Text fields KasuMeta localizes. The values are what the store and the API use.</summary>
public static class MetadataFields
{
    public const string Name = "name";
    public const string Overview = "overview";
    public const string Tagline = "tagline";
    public const string SortName = "sort_name";
    public const string Genres = "genres";
    public const string Tags = "tags";

    /// <summary>Gets every known field.</summary>
    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Name, Overview, Tagline, SortName, Genres, Tags };

    /// <summary>
    /// Gets the fields whose value is a list of terms, stored as a JSON array of strings (see
    /// <see cref="ListValues"/>).
    /// </summary>
    public static IReadOnlySet<string> ListFields { get; } = new HashSet<string>(StringComparer.Ordinal) { Genres, Tags };
}

/// <summary>How list fields (<see cref="MetadataFields.ListFields"/>) are stored: a JSON array of strings.</summary>
public static class ListValues
{
    /// <summary>
    /// Serializes terms: trimmed, empty ones dropped, duplicates (ignoring case) dropped, order
    /// kept. Returns null when nothing is left.
    /// </summary>
    public static string? Serialize(IEnumerable<string?> values)
    {
        var clean = Clean(values);
        return clean.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(clean);
    }

    /// <summary>Reads a stored list; anything that isn't a JSON array of strings reads as empty.</summary>
    public static IReadOnlyList<string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return Clean(System.Text.Json.JsonSerializer.Deserialize<List<string?>>(json) ?? []);
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>Trims, drops empty values and case-insensitive duplicates, keeps order.</summary>
    public static IReadOnlyList<string> Clean(IEnumerable<string?> values)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var value in values)
        {
            var text = value?.Trim();
            if (!string.IsNullOrEmpty(text) && seen.Add(text))
            {
                list.Add(text);
            }
        }

        return list;
    }
}

/// <summary>Kinds of term the admin dictionary translates.</summary>
public static class TermKinds
{
    public const string Genre = "genre";
    public const string Tag = "tag";

    /// <summary>Gets every known kind.</summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal) { Genre, Tag };

    /// <summary>The list field whose terms a kind translates.</summary>
    public static string FieldOf(string kind) => kind == Genre ? MetadataFields.Genres : MetadataFields.Tags;
}

/// <summary>One dictionary entry: a provider's term and its translation in one locale.</summary>
/// <param name="Kind">One of <see cref="TermKinds"/>.</param>
/// <param name="Term">The term as providers send it (English).</param>
/// <param name="Locale">BCP-47 locale of the translation.</param>
/// <param name="Value">The translation.</param>
public sealed record TermTranslation(string Kind, string Term, string Locale, string Value);

/// <summary>A term the library uses, and how many items use it.</summary>
/// <param name="Term">The term as stored (English).</param>
/// <param name="Items">How many items carry it.</param>
public sealed record TermUse(string Term, int Items);

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

    /// <summary>A genre or tag list translated at serve time by the admin's dictionary; never stored.</summary>
    public const string Dictionary = "dictionary";
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
/// <param name="Url">Absolute URL of the image; empty for an uploaded file.</param>
/// <param name="Source">One of <see cref="MetadataSources"/>.</param>
/// <param name="Locked">Same rule as <see cref="LocalizedField.Locked"/>.</param>
/// <param name="Width">Pixel width of the image at <paramref name="Url"/>, if known.</param>
/// <param name="Height">Pixel height, if known.</param>
/// <param name="HasFile">
/// Whether the image is a file an admin uploaded (stored in the database). Set by the store on
/// reads; writes go through <see cref="ILocalizedMetadataStore.SetUploadedImageAsync"/>.
/// </param>
public sealed record LocalizedImage(
    Guid ItemId,
    string Locale,
    string ImageType,
    string Url,
    string Source,
    bool Locked = false,
    int? Width = null,
    int? Height = null,
    bool HasFile = false)
{
    /// <summary>Gets when the row was last written. Set by the store; ignored on writes.</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// An admin's edit of one item in one locale, applied in one transaction. Set rows always win,
/// locked or not; removed keys are deleted whatever their source.
/// </summary>
/// <param name="ItemId">The Jellyfin item id.</param>
/// <param name="Locale">The normalized BCP-47 locale every row belongs to.</param>
/// <param name="SetFields">Manual text values to write (source <see cref="MetadataSources.Manual"/>).</param>
/// <param name="SetImages">Manual images to write (source <see cref="MetadataSources.Manual"/>).</param>
/// <param name="RemoveFields">Fields (<see cref="MetadataFields"/>) whose stored value goes away.</param>
/// <param name="RemoveImages">Image types (<see cref="LocalizedImageTypes"/>) whose stored image goes away.</param>
public sealed record ManualEdit(
    Guid ItemId,
    string Locale,
    IReadOnlyCollection<LocalizedField> SetFields,
    IReadOnlyCollection<LocalizedImage> SetImages,
    IReadOnlyCollection<string> RemoveFields,
    IReadOnlyCollection<string> RemoveImages);

/// <summary>How the library's fetches stand, for the status page.</summary>
/// <param name="Counts">Items per fetch status; a status no item has is absent.</param>
/// <param name="RecentErrors">The newest failed fetches, newest first.</param>
public sealed record FetchSummary(
    IReadOnlyDictionary<FetchStatus, int> Counts,
    IReadOnlyList<FetchState> RecentErrors);

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
/// kind, 4 = adds AniList titles, 5 = adds genres, tags and the original language). An item
/// below the current version is fetched again, so new data reaches items fetched by older builds.
/// </param>
/// <param name="Kind">The item's kind, or null if not recorded yet.</param>
/// <param name="OriginalLanguage">
/// The item's original language (BCP-47, e.g. <c>ja</c>), the last-resort fallback when no
/// requested locale has a value; null if unknown.
/// </param>
public sealed record FetchState(
    Guid ItemId,
    IReadOnlyDictionary<string, string> ProviderIds,
    DateTimeOffset? LastFetch,
    FetchStatus Status,
    string? LastError = null,
    int FetchVersion = 1,
    ItemKind? Kind = null,
    string? OriginalLanguage = null);
