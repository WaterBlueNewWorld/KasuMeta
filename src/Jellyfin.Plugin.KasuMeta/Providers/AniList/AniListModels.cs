using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.KasuMeta.Providers.AniList;

/// <summary>One locale's text from AniList.</summary>
/// <param name="Locale">BCP-47 locale: <c>en-US</c>, the native one (<c>ja-JP</c>), or the romanized one (<c>ja-Latn</c>).</param>
/// <param name="Title">The title in that locale, if any.</param>
/// <param name="Overview">The synopsis (AniList only has English ones), if any.</param>
public sealed record AniListTranslation(string Locale, string? Title, string? Overview);

/// <summary>The GraphQL envelope: <c>{"data": {"Media": …}}</c>.</summary>
internal sealed class AniListResponse
{
    [JsonPropertyName("data")]
    public AniListData? Data { get; set; }
}

internal sealed class AniListData
{
    [JsonPropertyName("Media")]
    public AniListMediaEntry? Media { get; set; }
}

internal sealed class AniListMediaEntry
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets ISO 3166-1 alpha-2 (<c>JP</c>, <c>CN</c>, <c>KR</c>, <c>TW</c>).</summary>
    [JsonPropertyName("countryOfOrigin")]
    public string? CountryOfOrigin { get; set; }

    [JsonPropertyName("title")]
    public AniListTitleEntry? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

internal sealed class AniListTitleEntry
{
    [JsonPropertyName("romaji")]
    public string? Romaji { get; set; }

    [JsonPropertyName("english")]
    public string? English { get; set; }

    [JsonPropertyName("native")]
    public string? Native { get; set; }
}
