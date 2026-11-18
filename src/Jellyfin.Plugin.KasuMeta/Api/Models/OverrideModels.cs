namespace Jellyfin.Plugin.KasuMeta.Api.Models;

/// <summary>Body of <c>PUT /KasuMeta/Items/{itemId}/{locale}</c> (admins only).</summary>
/// <remarks>
/// Only the keys present are touched. A value sets a manual override; <c>null</c> removes the
/// stored value for that locale (manual or not) and re-fetches the item, so the provider's value
/// comes back if it has one.
/// </remarks>
public sealed class ItemOverridesRequest
{
    /// <summary>
    /// Gets or sets text values keyed by field (<c>Name</c>, <c>Overview</c>, <c>Tagline</c>,
    /// <c>SortName</c>; any casing, <c>sort_name</c> too).
    /// </summary>
    public IReadOnlyDictionary<string, ManualFieldValue?>? Fields { get; set; }

    /// <summary>
    /// Gets or sets images keyed by type (<c>Primary</c>, <c>Logo</c>, <c>Backdrop</c>,
    /// <c>Thumb</c>; any casing).
    /// </summary>
    public IReadOnlyDictionary<string, ManualImageValue?>? Images { get; set; }
}

/// <summary>A manual text value.</summary>
public sealed class ManualFieldValue
{
    /// <summary>Gets or sets the text; leading and trailing whitespace is trimmed.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether provider refreshes must leave the value alone. Default true. False
    /// sets a quick correction that the next provider fetch may replace.
    /// </summary>
    public bool Locked { get; set; } = true;
}

/// <summary>A manual image.</summary>
public sealed class ManualImageValue
{
    /// <summary>Gets or sets the absolute http(s) URL of the image.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets whether provider refreshes must leave the image alone. Default true.</summary>
    public bool Locked { get; set; } = true;
}

/// <summary>Where an item's last provider fetch stands.</summary>
/// <param name="Status"><c>pending</c>, <c>ok</c>, <c>error</c> or <c>skipped</c> (nothing to fetch, e.g. no TMDB id).</param>
/// <param name="LastFetch">When the last fetch finished, if it ever did.</param>
/// <param name="LastError">Why the last fetch failed or was skipped.</param>
public sealed record ItemFetchInfo(string Status, DateTimeOffset? LastFetch, string? LastError);

/// <summary>
/// Response of <c>GET /KasuMeta/Items/{itemId}/All</c> and <c>PUT /KasuMeta/Items/{itemId}/{locale}</c>
/// (admins only): every stored locale of one item, without fallback.
/// </summary>
public sealed class ItemTranslations
{
    /// <summary>Gets or sets the dashless Jellyfin item id.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the stored values keyed by locale, each as an overlay whose
    /// <c>ResolvedLocale</c>s are that locale. Empty when nothing is stored.
    /// </summary>
    public IReadOnlyDictionary<string, ItemOverlay> Locales { get; set; } = new Dictionary<string, ItemOverlay>();

    /// <summary>Gets or sets the item's fetch state; null (omitted) if it was never queued.</summary>
    public ItemFetchInfo? Fetch { get; set; }
}
