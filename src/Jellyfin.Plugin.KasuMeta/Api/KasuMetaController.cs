using System.Net.Mime;
using System.Security.Claims;
using Jellyfin.Plugin.KasuMeta.Api.Models;
using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Library;
using Jellyfin.Plugin.KasuMeta.Localization;
using Jellyfin.Plugin.KasuMeta.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.KasuMeta.Api;

/// <summary>
/// KasuMeta's HTTP API. Every route sits behind Jellyfin's own auth, so the client app sends the
/// token it already has. Contract: api/openapi.yaml (PascalCase, as Jellyfin serializes).
/// </summary>
[ApiController]
[Authorize]
[Route("KasuMeta")]
[Produces(MediaTypeNames.Application.Json)]
public class KasuMetaController : ControllerBase
{
    /// <summary>Bumped only on a breaking change to the routes or DTOs.</summary>
    public const int ApiVersion = 1;

    /// <summary>Most ids per batch request.</summary>
    public const int MaxBatchSize = 200;

    // Claim types Jellyfin's authentication handler sets (Jellyfin.Api.Constants.InternalClaimTypes,
    // not on NuGet, hence the literals).
    internal const string UserIdClaim = "Jellyfin-UserId";
    internal const string IsApiKeyClaim = "Jellyfin-IsApiKey";

    private readonly IKasuMetaConfigProvider _config;
    private readonly OverlayService _overlays;
    private readonly ILibraryItemSource _library;

    public KasuMetaController(IKasuMetaConfigProvider config, OverlayService overlays, ILibraryItemSource library)
    {
        _config = config;
        _overlays = overlays;
        _library = library;
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

    /// <summary>Localized overlays for many items at once (one call per grid page).</summary>
    /// <response code="200">Overlays for the items that have one; the rest are listed in Missing.</response>
    /// <response code="400">Bad locale, unknown field, or more than <see cref="MaxBatchSize"/> ids.</response>
    [HttpPost("Items/Batch")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<BatchResponse>> GetBatch([FromBody] BatchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ItemIds.Count > MaxBatchSize)
        {
            return BadRequest($"At most {MaxBatchSize} item ids per request.");
        }

        if (!LocaleChain.TryNormalize(request.Locale, out var locale))
        {
            return BadRequest("Locale must be a BCP-47 code such as es-MX.");
        }

        if (!TryParseFields(request.Fields, out var fields, out var unknown))
        {
            return BadRequest($"Unknown field '{unknown}'. Known: {string.Join(", ", MetadataFields.All.Order(StringComparer.Ordinal))}.");
        }

        if (!TryVisible(request.ItemIds, out var visible))
        {
            return Forbid();
        }

        return await _overlays.BuildAsync(request.ItemIds, visible, locale, fields, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The full localized overlay of one item (detail page).</summary>
    /// <response code="200">The overlay.</response>
    /// <response code="400">Bad locale.</response>
    /// <response code="404">Nothing to serve: not fetched, no translation, unknown or not visible.</response>
    [HttpGet("Items/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ItemOverlay>> GetItem(
        [FromRoute] Guid itemId,
        [FromQuery] string? locale,
        CancellationToken cancellationToken)
    {
        if (!LocaleChain.TryNormalize(locale, out var normalized))
        {
            return BadRequest("Locale must be a BCP-47 code such as es-MX.");
        }

        if (!TryVisible([itemId], out var visible))
        {
            return Forbid();
        }

        var batch = await _overlays
            .BuildAsync([itemId], visible, normalized, MetadataFields.All, cancellationToken)
            .ConfigureAwait(false);
        return batch.Items.Values.FirstOrDefault() is { } overlay ? overlay : NotFound();
    }

    internal static bool TryParseFields(IReadOnlyList<string>? requested, out IReadOnlySet<string> fields, out string? unknown)
    {
        unknown = null;
        if (requested is null || requested.Count == 0)
        {
            fields = MetadataFields.All;
            return true;
        }

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in requested)
        {
            // Accept the wire's PascalCase too: "SortName" → "sort_name".
            var key = (raw ?? string.Empty).Trim().Replace("_", string.Empty, StringComparison.Ordinal);
            var match = MetadataFields.All.FirstOrDefault(f =>
                string.Equals(f.Replace("_", string.Empty, StringComparison.Ordinal), key, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                unknown = raw;
                fields = set;
                return false;
            }

            set.Add(match);
        }

        fields = set;
        return true;
    }

    /// <summary>
    /// Filters ids to what the caller may see. A user sees what their library access allows; an
    /// API key (admin-issued, no user) sees everything. Anything else is refused.
    /// </summary>
    private bool TryVisible(IReadOnlyCollection<Guid> ids, out IReadOnlyCollection<Guid> visible)
    {
        var principal = User ?? new ClaimsPrincipal();
        if (Guid.TryParse(principal.FindFirst(UserIdClaim)?.Value, out var userId) && userId != Guid.Empty)
        {
            visible = _library.FilterVisible(userId, ids).ToHashSet();
            return true;
        }

        if (bool.TryParse(principal.FindFirst(IsApiKeyClaim)?.Value, out var isApiKey) && isApiKey)
        {
            visible = ids.ToHashSet();
            return true;
        }

        visible = [];
        return false;
    }
}
