namespace Jellyfin.Plugin.KasuMeta.Api.Models;

/// <summary>Response of <c>GET /KasuMeta/Info</c>.</summary>
/// <param name="Name">Always "KasuMeta".</param>
/// <param name="Version">The plugin's version.</param>
/// <param name="ApiVersion">The contract version; the client refuses a major it doesn't know.</param>
/// <param name="EnabledLocales">BCP-47 locales the server fetches and serves.</param>
/// <param name="FallbackToBaseLanguage">Whether the server falls back from `es-MX` to `es`.</param>
/// <param name="Features">
/// What this build serves: <c>text</c> (titles, overviews, taglines), <c>images</c>,
/// <c>search</c>, <c>overrides</c>, <c>genres</c>, <c>tags</c>, <c>uploads</c>, <c>terms</c>,
/// <c>original-language</c>. A client checks here before calling a route an older plugin may lack.
/// </param>
/// <param name="FallbackToOriginalLanguage">
/// Whether a field with nothing in the requested language is served in the item's original
/// language (its <c>ResolvedLocale</c> says which).
/// </param>
public sealed record KasuMetaInfo(
    string Name,
    string Version,
    int ApiVersion,
    IReadOnlyList<string> EnabledLocales,
    bool FallbackToBaseLanguage,
    IReadOnlyList<string> Features,
    bool FallbackToOriginalLanguage = false);
