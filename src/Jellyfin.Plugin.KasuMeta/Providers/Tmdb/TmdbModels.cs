using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.KasuMeta.Providers.Tmdb;

/// <summary>
/// One language's text for a TMDB title, normalized: BCP-47 locale, empty strings as null,
/// and <c>title</c> (movies) or <c>name</c> (TV, seasons, episodes) folded into Title.
/// </summary>
/// <param name="Locale">BCP-47 locale built from TMDB's <c>iso_639_1</c> and <c>iso_3166_1</c>.</param>
/// <param name="Title">The localized title, or null if TMDB has none.</param>
/// <param name="Overview">The localized overview, or null.</param>
/// <param name="Tagline">The localized tagline (movies and series only), or null.</param>
public sealed record TmdbTranslation(string Locale, string? Title, string? Overview, string? Tagline);

/// <summary>The best TMDB image of one type for one locale.</summary>
/// <param name="Locale">BCP-47 locale from the image's <c>iso_639_1</c> and <c>iso_3166_1</c>.</param>
/// <param name="ImageType">Jellyfin's name for it: <c>Primary</c> (poster), <c>Logo</c>, <c>Backdrop</c>.</param>
/// <param name="Url">Absolute URL of the original-size file.</param>
/// <param name="Width">Pixel width of the original.</param>
/// <param name="Height">Pixel height of the original.</param>
public sealed record TmdbImage(string Locale, string ImageType, string Url, int Width, int Height);

/// <summary>A TMDB genre: its id, and its name in the language asked for.</summary>
/// <param name="Id">TMDB's genre id (the same in every language).</param>
/// <param name="Name">The name.</param>
public sealed record TmdbGenre(int Id, string Name);

/// <summary>Everything one TMDB request returns for a title.</summary>
/// <param name="Translations">Every language's text.</param>
/// <param name="Images">The best image per locale and type (empty for episodes).</param>
/// <param name="OriginalLanguage">The title's original language as BCP-47 (<c>ja</c>); movies and series only.</param>
/// <param name="Genres">The title's genres, in English; movies and series only.</param>
/// <param name="Keywords">The title's keywords (TMDB has them in English only); movies and series only.</param>
public sealed record TmdbTitle(
    IReadOnlyList<TmdbTranslation> Translations,
    IReadOnlyList<TmdbImage> Images,
    string? OriginalLanguage = null,
    IReadOnlyList<TmdbGenre>? Genres = null,
    IReadOnlyList<string>? Keywords = null);

/// <summary>Raw appended <c>images</c> (seasons only have posters).</summary>
internal sealed class TmdbImagesResponse
{
    [JsonPropertyName("posters")]
    public List<TmdbImageEntry> Posters { get; set; } = [];

    [JsonPropertyName("logos")]
    public List<TmdbImageEntry> Logos { get; set; } = [];

    [JsonPropertyName("backdrops")]
    public List<TmdbImageEntry> Backdrops { get; set; } = [];
}

/// <summary>Raw image entry.</summary>
internal sealed class TmdbImageEntry
{
    [JsonPropertyName("iso_639_1")]
    public string? Language { get; set; }

    [JsonPropertyName("iso_3166_1")]
    public string? Region { get; set; }

    [JsonPropertyName("file_path")]
    public string? FilePath { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("vote_average")]
    public double VoteAverage { get; set; }

    [JsonPropertyName("vote_count")]
    public int VoteCount { get; set; }
}

/// <summary>
/// Raw movie or series details with <c>append_to_response=translations</c>. Only the fields
/// KasuMeta needs: the original language and title fill the gap TMDB leaves in /translations,
/// whose entry for the original language has an empty title (seen live: Antitrust en-US,
/// Magia Record ja-JP).
/// </summary>
internal sealed class TmdbDetailsResponse
{
    [JsonPropertyName("original_language")]
    public string? OriginalLanguage { get; set; }

    /// <summary>Gets or sets the original title (movies).</summary>
    [JsonPropertyName("original_title")]
    public string? OriginalTitle { get; set; }

    /// <summary>Gets or sets the original name (series).</summary>
    [JsonPropertyName("original_name")]
    public string? OriginalName { get; set; }

    [JsonPropertyName("translations")]
    public TmdbTranslationsResponse? Translations { get; set; }

    [JsonPropertyName("images")]
    public TmdbImagesResponse? Images { get; set; }

    [JsonPropertyName("genres")]
    public List<TmdbGenreEntry>? Genres { get; set; }

    [JsonPropertyName("keywords")]
    public TmdbKeywordsResponse? Keywords { get; set; }
}

/// <summary>Raw genre or keyword: in a title's details, or in <c>/genre/{movie|tv}/list</c>.</summary>
internal sealed class TmdbGenreEntry
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

/// <summary>Raw <c>/genre/{movie|tv}/list</c> response.</summary>
internal sealed class TmdbGenreListResponse
{
    [JsonPropertyName("genres")]
    public List<TmdbGenreEntry> Genres { get; set; } = [];
}

/// <summary>Raw appended <c>keywords</c>: movies list them under <c>keywords</c>, series under <c>results</c>.</summary>
internal sealed class TmdbKeywordsResponse
{
    [JsonPropertyName("keywords")]
    public List<TmdbGenreEntry>? Keywords { get; set; }

    [JsonPropertyName("results")]
    public List<TmdbGenreEntry>? Results { get; set; }
}

/// <summary>Raw <c>/translations</c> response (movie, tv, season and episode share it).</summary>
internal sealed class TmdbTranslationsResponse
{
    [JsonPropertyName("translations")]
    public List<TmdbTranslationEntry> Translations { get; set; } = [];
}

/// <summary>Raw translation entry.</summary>
internal sealed class TmdbTranslationEntry
{
    [JsonPropertyName("iso_639_1")]
    public string? Language { get; set; }

    [JsonPropertyName("iso_3166_1")]
    public string? Region { get; set; }

    [JsonPropertyName("data")]
    public TmdbTranslationData? Data { get; set; }
}

/// <summary>Raw translation data.</summary>
internal sealed class TmdbTranslationData
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    [JsonPropertyName("tagline")]
    public string? Tagline { get; set; }
}
