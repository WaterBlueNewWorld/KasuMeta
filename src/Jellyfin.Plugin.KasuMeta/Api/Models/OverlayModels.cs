namespace Jellyfin.Plugin.KasuMeta.Api.Models;

/// <summary>Body of <c>POST /KasuMeta/Items/Batch</c>.</summary>
public sealed class BatchRequest
{
    /// <summary>Gets or sets the Jellyfin item ids (at most <see cref="KasuMetaController.MaxBatchSize"/>).</summary>
    public IReadOnlyList<Guid> ItemIds { get; set; } = [];

    /// <summary>Gets or sets the BCP-47 locale wanted, e.g. <c>es-MX</c>.</summary>
    public string Locale { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets which fields to return (<c>name</c>, <c>overview</c>, <c>tagline</c>,
    /// <c>sort_name</c>; any casing). Null means all; an empty list means none (images only).
    /// </summary>
    public IReadOnlyList<string>? Fields { get; set; }

    /// <summary>
    /// Gets or sets which image types to return (<c>Primary</c>, <c>Logo</c>, <c>Backdrop</c>,
    /// <c>Thumb</c>; any casing). Null means all; an empty list means none (text only).
    /// </summary>
    public IReadOnlyList<string>? Images { get; set; }
}

/// <summary>One localized image.</summary>
/// <param name="Url">
/// Absolute URL of the original-size file. TMDB serves smaller copies at the same path with
/// <c>/original/</c> replaced by a width such as <c>/w342/</c>, <c>/w500/</c> or <c>/w780/</c>.
/// </param>
/// <param name="Width">Pixel width of the original, if known.</param>
/// <param name="Height">Pixel height of the original, if known.</param>
/// <param name="Source">Who provided it.</param>
/// <param name="ResolvedLocale">The stored locale served (see <see cref="LocalizedValue.ResolvedLocale"/>).</param>
/// <param name="Locked">Whether an admin locked it against provider updates.</param>
public sealed record LocalizedImageValue(string Url, int? Width, int? Height, string Source, string ResolvedLocale, bool Locked);

/// <summary>One localized value, with where it came from.</summary>
/// <param name="Value">The text.</param>
/// <param name="Source">Who provided it: <c>tmdb</c>, <c>manual</c>, …</param>
/// <param name="ResolvedLocale">
/// The stored locale actually served. It differs from the request when the server fell back
/// (asked <c>es-MX</c>, served <c>es-ES</c>).
/// </param>
/// <param name="Locked">Whether an admin locked it against provider updates.</param>
public sealed record LocalizedValue(string Value, string Source, string ResolvedLocale, bool Locked);

/// <summary>The localized overlay of one item. Fields KasuMeta has nothing for are null.</summary>
public sealed class ItemOverlay
{
    /// <summary>Gets or sets the localized title.</summary>
    public LocalizedValue? Name { get; set; }

    /// <summary>Gets or sets the localized overview.</summary>
    public LocalizedValue? Overview { get; set; }

    /// <summary>Gets or sets the localized tagline.</summary>
    public LocalizedValue? Tagline { get; set; }

    /// <summary>Gets or sets the localized sort name (manual only so far).</summary>
    public LocalizedValue? SortName { get; set; }

    /// <summary>
    /// Gets or sets the localized images keyed by Jellyfin image type (<c>Primary</c>,
    /// <c>Logo</c>, …), or null (omitted) when there are none.
    /// </summary>
#pragma warning disable CA2227 // A DTO: the serializer sets it.
    public Dictionary<string, LocalizedImageValue>? Images { get; set; }
#pragma warning restore CA2227

    /// <summary>Gets or sets when the newest value served here was written (cache validation).</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Response of <c>POST /KasuMeta/Items/Batch</c>.</summary>
public sealed class BatchResponse
{
    /// <summary>Gets or sets the requested locale, normalized (<c>es_mx</c> → <c>es-MX</c>).</summary>
    public string Locale { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the overlays, keyed by item id in Jellyfin's dashless form (as
    /// <c>BaseItemDto.Id</c> sends it).
    /// </summary>
    public IReadOnlyDictionary<string, ItemOverlay> Items { get; set; } = new Dictionary<string, ItemOverlay>();

    /// <summary>
    /// Gets or sets the ids with nothing to serve: not fetched yet, no translation in any
    /// fallback locale, unknown, or not visible to the caller (indistinguishable on purpose).
    /// The client shows Jellyfin's own fields for these.
    /// </summary>
    public IReadOnlyList<string> Missing { get; set; } = [];
}
