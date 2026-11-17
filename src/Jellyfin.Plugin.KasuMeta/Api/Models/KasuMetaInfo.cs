namespace Jellyfin.Plugin.KasuMeta.Api.Models;

/// <summary>Response of <c>GET /KasuMeta/Info</c>.</summary>
/// <param name="Name">Always "KasuMeta".</param>
/// <param name="Version">The plugin's version.</param>
/// <param name="ApiVersion">The contract version; the client refuses a major it doesn't know.</param>
/// <param name="EnabledLocales">BCP-47 locales the server fetches and serves.</param>
/// <param name="FallbackToBaseLanguage">Whether the server falls back from `es-MX` to `es`.</param>
public sealed record KasuMetaInfo(
    string Name,
    string Version,
    int ApiVersion,
    IReadOnlyList<string> EnabledLocales,
    bool FallbackToBaseLanguage);
