using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.KasuMeta.Configuration;

/// <summary>
/// Settings an admin edits on the plugin's dashboard page. Serialized to XML by Jellyfin,
/// so keep properties public, settable and with defaults.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the BCP-47 locales to fetch and serve. The defaults cover English, Spanish
    /// (Spain and Mexico), Japanese and Simplified Chinese, spelled as TMDB expects them.
    /// </summary>
#pragma warning disable CA1819 // Jellyfin's XML serializer needs a settable array.
    public string[] EnabledLocales { get; set; } = ["en-US", "es-ES", "es-MX", "ja-JP", "zh-CN"];
#pragma warning restore CA1819

    /// <summary>Gets or sets a value indicating whether `es-MX` may fall back to `es` on the server.</summary>
    public bool FallbackToBaseLanguage { get; set; } = true;

    /// <summary>Gets or sets the TMDB API key (v3 key or v4 read token). Never sent to clients.</summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the TVDB API key (v4). Never sent to clients.</summary>
    public string TvdbApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the subscriber PIN that goes with a user-supported TVDB key; empty for a
    /// licensed (project) key. Never sent to clients.
    /// </summary>
    public string TvdbPin { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets which providers each category of metadata (titles, overviews, taglines,
    /// genres, tags, each image type) comes from, first choice first. For each locale, the first
    /// listed provider with a value wins and the next ones fill what it lacks. A provider is
    /// queried when some category lists it and it is set up (TMDB and TVDB need their key).
    /// Empty until the configuration page saves it: until then, and for any category missing
    /// here, the legacy settings below decide (see <see cref="SourceRules.Effective"/>).
    /// </summary>
#pragma warning disable CA1819 // Jellyfin's XML serializer needs a settable array.
    public ProviderRule[] ProviderRules { get; set; } = [];
#pragma warning restore CA1819

    /// <summary>
    /// Gets or sets a value indicating whether a field with no value in the requested locale (nor,
    /// with <see cref="FallbackToBaseLanguage"/>, in its language) is served in the item's original
    /// language: a Japanese series with no Spanish synopsis shows the Japanese one. With nothing in
    /// any of them, the field stays empty.
    /// </summary>
    public bool FallbackToOriginalLanguage { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether TMDB was turned on (1.1 setting). Read only to
    /// derive <see cref="ProviderRules"/> before an admin saves them.
    /// </summary>
    public bool TmdbEnabled { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether TVDB was turned on (1.1 setting; see <see cref="TmdbEnabled"/>).</summary>
    public bool TvdbEnabled { get; set; }

    /// <summary>Gets or sets TMDB's priority weight (1.1 setting; see <see cref="TmdbEnabled"/>).</summary>
    public int TmdbWeight { get; set; } = 100;

    /// <summary>Gets or sets TVDB's priority weight (1.1 setting; see <see cref="TmdbEnabled"/>).</summary>
    public int TvdbWeight { get; set; } = 50;

    /// <summary>
    /// Gets or sets a value indicating whether AniList was turned on (1.1 setting; see
    /// <see cref="TmdbEnabled"/>). AniList's romaji titles (<c>ja-Latn</c>) are stored for search
    /// whenever AniList is used for titles.
    /// </summary>
    public bool AniListEnabled { get; set; } = true;

    /// <summary>Gets or sets how old a fetched row may get before the refresh task re-fetches it.</summary>
    public int RefreshIntervalHours { get; set; } = 24 * 7;
}
