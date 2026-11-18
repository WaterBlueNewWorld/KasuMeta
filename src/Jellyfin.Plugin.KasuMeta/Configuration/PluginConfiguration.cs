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

    /// <summary>Gets or sets a value indicating whether titles, overviews and artwork come from TMDB.</summary>
    public bool TmdbEnabled { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether titles, overviews and artwork come from TVDB.</summary>
    public bool TvdbEnabled { get; set; }

    /// <summary>
    /// Gets or sets TMDB's priority weight when both TMDB and TVDB are on: for each field (or
    /// image type) and locale, the provider with the higher weight wins, and the other fills
    /// what it lacks. A tie goes to TMDB. See <see cref="ProviderPriority"/>.
    /// </summary>
    public int TmdbWeight { get; set; } = ProviderPriority.DefaultTmdbWeight;

    /// <summary>Gets or sets TVDB's priority weight (see <see cref="TmdbWeight"/>).</summary>
    public int TvdbWeight { get; set; } = ProviderPriority.DefaultTvdbWeight;

    /// <summary>
    /// Gets or sets a value indicating whether items with an AniList id also get AniList's titles
    /// (English, native, and romaji as <c>ja-Latn</c>, which is stored for search even when it
    /// isn't an enabled locale). AniList needs no key. TMDB's and TVDB's values win
    /// where they have one.
    /// </summary>
    public bool AniListEnabled { get; set; } = true;

    /// <summary>Gets or sets how old a fetched row may get before the refresh task re-fetches it.</summary>
    public int RefreshIntervalHours { get; set; } = 24 * 7;
}
