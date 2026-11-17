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
