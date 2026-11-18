using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.KasuMeta.Configuration;

/// <summary>
/// Settings an admin edits on the plugin's dashboard page. Serialized to XML by Jellyfin,
/// so keep properties public, settable and with defaults.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the BCP-47 locales to fetch and serve. The defaults are the UI languages
    /// the client app ships (lib/l10n/l10n.dart: en, es, es_MX, ja, zh), spelled as TMDB expects them.
    /// </summary>
#pragma warning disable CA1819 // Jellyfin's XML serializer needs a settable array.
    public string[] EnabledLocales { get; set; } = ["en-US", "es-ES", "es-MX", "ja-JP", "zh-CN"];
#pragma warning restore CA1819

    /// <summary>Gets or sets a value indicating whether `es-MX` may fall back to `es` on the server.</summary>
    public bool FallbackToBaseLanguage { get; set; } = true;

    /// <summary>Gets or sets the TMDB API key (v3 key or v4 read token). Never sent to clients.</summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the TVDB API key. Never sent to clients.</summary>
    public string TvdbApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether items with an AniList id also get AniList's titles
    /// (English, native, and romaji as <c>ja-Latn</c>, which is stored for search even when it
    /// isn't an enabled locale). AniList needs no key. TMDB's values win
    /// where both have one.
    /// </summary>
    public bool AniListEnabled { get; set; } = true;

    /// <summary>Gets or sets how old a fetched row may get before the refresh task re-fetches it.</summary>
    public int RefreshIntervalHours { get; set; } = 24 * 7;
}
