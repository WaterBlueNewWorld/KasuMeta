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
