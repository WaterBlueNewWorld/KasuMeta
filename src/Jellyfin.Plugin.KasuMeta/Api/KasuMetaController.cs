using System.Net.Mime;
using Jellyfin.Plugin.KasuMeta.Api.Models;
using Jellyfin.Plugin.KasuMeta.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.KasuMeta.Api;

/// <summary>
/// KasuMeta's HTTP API. Every route sits behind Jellyfin's own auth, so the client app sends the
/// token it already has. Contract: api/openapi.yaml.
/// </summary>
[ApiController]
[Authorize]
[Route("KasuMeta")]
[Produces(MediaTypeNames.Application.Json)]
public class KasuMetaController : ControllerBase
{
    /// <summary>Bumped only on a breaking change to the routes or DTOs.</summary>
    public const int ApiVersion = 1;

    private readonly IKasuMetaConfigProvider _config;

    public KasuMetaController(IKasuMetaConfigProvider config)
    {
        _config = config;
    }

    /// <summary>
    /// Capability check: the client app calls this after sign-in. A 404 means the plugin isn't installed,
    /// and the app keeps to stock Jellyfin fields.
    /// </summary>
    /// <response code="200">The plugin is installed and these locales are served.</response>
    [HttpGet("Info")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<KasuMetaInfo> GetInfo()
    {
        var config = _config.Current;
        return new KasuMetaInfo(
            Name: "KasuMeta",
            Version: _config.PluginVersion.ToString(),
            ApiVersion: ApiVersion,
            EnabledLocales: config.EnabledLocales,
            FallbackToBaseLanguage: config.FallbackToBaseLanguage);
    }
}
