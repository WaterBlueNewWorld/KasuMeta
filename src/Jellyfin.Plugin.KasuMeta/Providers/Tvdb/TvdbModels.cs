using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.KasuMeta.Providers.Tvdb;

/// <summary>
/// One language's text for a TVDB record, normalized: a BCP-47 locale from TVDB's language id,
/// empty strings as null, aliases left out.
/// </summary>
/// <param name="Locale">
/// BCP-47 locale. TVDB has one entry per language, so this is usually a bare language
/// (<c>es</c>, <c>ja</c>); only Portuguese (<c>pt-BR</c>, <c>pt-PT</c>) and Chinese
/// (<c>zh-CN</c>, <c>zh-TW</c>, <c>yue</c>) carry more.
/// </param>
/// <param name="Title">The localized name, or null.</param>
/// <param name="Overview">The localized overview, or null.</param>
/// <param name="Tagline">The localized tagline (TVDB has them for movies only), or null.</param>
public sealed record TvdbTranslation(string Locale, string? Title, string? Overview, string? Tagline);

/// <summary>The best TVDB artwork of one type for one locale.</summary>
/// <param name="Locale">BCP-47 locale from the artwork's language.</param>
/// <param name="ImageType">Jellyfin's name for it: <c>Primary</c> (poster), <c>Logo</c>, <c>Backdrop</c>.</param>
/// <param name="Url">Absolute URL of the image.</param>
/// <param name="Width">Pixel width.</param>
/// <param name="Height">Pixel height.</param>
public sealed record TvdbImage(string Locale, string ImageType, string Url, int Width, int Height);

/// <summary>Everything one TVDB record gives KasuMeta.</summary>
/// <param name="Translations">Every language's text.</param>
/// <param name="Images">The best artwork per locale and type (empty for episodes).</param>
/// <param name="OriginalLanguage">The record's original language as BCP-47 (<c>ja</c>); movies and series only.</param>
/// <param name="Genres">The record's genres (TVDB names them in English); movies and series only.</param>
/// <param name="Tags">The record's tags (English); movies and series only.</param>
public sealed record TvdbTitle(
    IReadOnlyList<TvdbTranslation> Translations,
    IReadOnlyList<TvdbImage> Images,
    string? OriginalLanguage = null,
    IReadOnlyList<string>? Genres = null,
    IReadOnlyList<string>? Tags = null);

/// <summary>TVDB's envelope: <c>{"status": "success", "data": …}</c>.</summary>
/// <typeparam name="T">The data's type.</typeparam>
internal sealed class TvdbEnvelope<T>
    where T : class
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("data")]
    public T? Data { get; set; }
}

internal sealed class TvdbLoginData
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }
}

/// <summary>
/// A movie, series, season or episode extended record, with only the fields KasuMeta reads.
/// Series and movies call their artwork <c>artworks</c>, seasons <c>artwork</c>.
/// </summary>
internal sealed class TvdbRecord
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Gets or sets TVDB's language id of the original language (<c>jpn</c>); movies and series only.</summary>
    [JsonPropertyName("originalLanguage")]
    public string? OriginalLanguage { get; set; }

    /// <summary>
    /// Gets or sets the translations (<c>meta=translations</c>). Kept raw: live it's an object
    /// with <c>nameTranslations</c> and <c>overviewTranslations</c>, while the published schema
    /// says an array for seasons, so a mismatch must not fail the whole record.
    /// </summary>
    [JsonPropertyName("translations")]
    public JsonElement Translations { get; set; }

    [JsonPropertyName("artworks")]
    public List<TvdbArtwork>? Artworks { get; set; }

    [JsonPropertyName("artwork")]
    public List<TvdbArtwork>? Artwork { get; set; }

    [JsonPropertyName("seasons")]
    public List<TvdbSeason>? Seasons { get; set; }

    /// <summary>Gets or sets the id of the series' default season type (episode order).</summary>
    [JsonPropertyName("defaultSeasonType")]
    public int? DefaultSeasonType { get; set; }

    /// <summary>Gets or sets the genres (movies and series), named in English.</summary>
    [JsonPropertyName("genres")]
    public List<TvdbNamed>? Genres { get; set; }

    /// <summary>Gets or sets the tag options (movies and series), named in English.</summary>
    [JsonPropertyName("tags")]
    public List<TvdbNamed>? Tags { get; set; }
}

/// <summary>A genre or tag option: only its name is read.</summary>
internal sealed class TvdbNamed
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

internal sealed class TvdbTranslations
{
    [JsonPropertyName("nameTranslations")]
    public List<TvdbTranslationEntry>? NameTranslations { get; set; }

    [JsonPropertyName("overviewTranslations")]
    public List<TvdbTranslationEntry>? OverviewTranslations { get; set; }
}

internal sealed class TvdbTranslationEntry
{
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    [JsonPropertyName("tagline")]
    public string? Tagline { get; set; }

    /// <summary>Gets or sets a value indicating whether this is an alternative name, not the translation.</summary>
    [JsonPropertyName("isAlias")]
    public bool? IsAlias { get; set; }

    [JsonPropertyName("isPrimary")]
    public bool? IsPrimary { get; set; }
}

internal sealed class TvdbArtwork
{
    [JsonPropertyName("image")]
    public string? Image { get; set; }

    /// <summary>Gets or sets TVDB's language id, or null for textless art.</summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>Gets or sets the artwork type id (see <c>/artwork/types</c>).</summary>
    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("score")]
    public double Score { get; set; }
}

internal sealed class TvdbSeason
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("type")]
    public TvdbSeasonType? Type { get; set; }
}

internal sealed class TvdbSeasonType
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the order's key: <c>official</c> (aired), <c>dvd</c>, <c>absolute</c>, …</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

/// <summary>Data of <c>/series/{id}/episodes/{season-type}</c>.</summary>
internal sealed class TvdbEpisodePage
{
    [JsonPropertyName("episodes")]
    public List<TvdbEpisodeRef>? Episodes { get; set; }
}

internal sealed class TvdbEpisodeRef
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("seasonNumber")]
    public int? SeasonNumber { get; set; }

    [JsonPropertyName("number")]
    public int? Number { get; set; }
}

/// <summary>One hit of <c>/search/remoteid/{id}</c>: exactly one of its properties is set.</summary>
internal sealed class TvdbRemoteIdHit
{
    [JsonPropertyName("movie")]
    public TvdbIdOnly? Movie { get; set; }

    [JsonPropertyName("series")]
    public TvdbIdOnly? Series { get; set; }
}

internal sealed class TvdbIdOnly
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
}
