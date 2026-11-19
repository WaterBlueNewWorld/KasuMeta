namespace Jellyfin.Plugin.KasuMeta.Api.Models;

/// <summary>One item found by <c>GET /KasuMeta/Search</c>.</summary>
/// <param name="ItemId">The Jellyfin item id, dashless.</param>
/// <param name="Type">The item's kind, as Jellyfin names it: <c>Movie</c>, <c>Series</c>, <c>Season</c>, <c>Episode</c>.</param>
/// <param name="MatchedName">
/// The localized name that matched, preferring the requested locale when the item matched in
/// several.
/// </param>
/// <param name="MatchedLocale">The locale of <paramref name="MatchedName"/>.</param>
public sealed record SearchResult(string ItemId, string Type, string MatchedName, string MatchedLocale);

/// <summary>One item found by a genre or tag.</summary>
/// <param name="ItemId">The item's id, dashless.</param>
/// <param name="Type">Movie, Series, Season or Episode.</param>
public sealed record TermItem(string ItemId, string Type);

/// <summary>Response of <c>GET /KasuMeta/Items/ByTerm</c>.</summary>
public sealed class TermItemsResponse
{
    /// <summary>Gets or sets the kind asked for: <c>genre</c> or <c>tag</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the genre or tag as the server read it (trimmed).</summary>
    public string Term { get; set; } = string.Empty;

    /// <summary>Gets or sets the requested locale, normalized.</summary>
    public string Locale { get; set; } = string.Empty;

    /// <summary>Gets or sets how many items the caller may see match, whatever the page.</summary>
    public int Total { get; set; }

    /// <summary>Gets or sets how many matches were skipped before <see cref="Results"/>.</summary>
    public int StartIndex { get; set; }

    /// <summary>
    /// Gets or sets one page of the matches, by Jellyfin's name for the item, only items the
    /// caller may see. The client fetches the items themselves from Jellyfin (<c>/Items?ids=</c>).
    /// </summary>
    public IReadOnlyList<TermItem> Results { get; set; } = [];
}

/// <summary>Response of <c>GET /KasuMeta/Search</c>.</summary>
public sealed class SearchResponse
{
    /// <summary>Gets or sets the query as the server read it (trimmed).</summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>Gets or sets the requested locale, normalized.</summary>
    public string Locale { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the matches, best first, one per item, only items the caller may see. The
    /// client fetches the items themselves from Jellyfin (<c>/Items?ids=</c>).
    /// </summary>
    public IReadOnlyList<SearchResult> Results { get; set; } = [];
}
