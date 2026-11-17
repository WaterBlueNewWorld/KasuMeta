using System.Net.Mime;
using System.Security.Claims;
using Jellyfin.Plugin.KasuMeta.Api.Models;
using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Library;
using Jellyfin.Plugin.KasuMeta.Localization;
using Jellyfin.Plugin.KasuMeta.Search;
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

    /// <summary>Search results returned when the request names no limit.</summary>
    public const int DefaultSearchLimit = 20;

    /// <summary>Most search results per request.</summary>
    public const int MaxSearchLimit = 100;

    /// <summary>
    /// What this build can do beyond the Info route, for clients to switch on. The API grows by
    /// adding routes and optional properties (ApiVersion stays), so "is search there?" is asked
    /// here rather than guessed from a version number.
    /// </summary>
    internal static readonly string[] Features = ["text", "images", "search"];

    private static readonly IReadOnlySet<ItemKind> DefaultSearchKinds =
        new HashSet<ItemKind> { ItemKind.Movie, ItemKind.Series, ItemKind.Episode };

    // Claim types Jellyfin's authentication handler sets (Jellyfin.Api.Constants.InternalClaimTypes,
    // not on NuGet, hence the literals).
    internal const string UserIdClaim = "Jellyfin-UserId";
    internal const string IsApiKeyClaim = "Jellyfin-IsApiKey";

    private readonly IKasuMetaConfigProvider _config;
    private readonly OverlayService _overlays;
    private readonly SearchService _search;
    private readonly ILibraryItemSource _library;

    public KasuMetaController(
        IKasuMetaConfigProvider config,
        OverlayService overlays,
        SearchService search,
        ILibraryItemSource library)
    {
        _config = config;
        _overlays = overlays;
        _search = search;
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
            FallbackToBaseLanguage: config.FallbackToBaseLanguage,
            Features: Features);
    }

    /// <summary>Localized overlays (text and images) for many items at once (one call per grid page).</summary>
    /// <response code="200">Overlays for the items that have one; the rest are listed in Missing.</response>
    /// <response code="400">Bad locale, unknown field or image type, or more than <see cref="MaxBatchSize"/> ids.</response>
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

        if (!TryParseImageTypes(request.Images, out var imageTypes, out var unknownImage))
        {
            return BadRequest(
                $"Unknown image type '{unknownImage}'. Known: {string.Join(", ", LocalizedImageTypes.All.Order(StringComparer.Ordinal))}.");
        }

        if (!TryVisible(request.ItemIds, out var visible))
        {
            return Forbid();
        }

        return await _overlays
            .BuildAsync(request.ItemIds, visible, locale, fields, imageTypes, cancellationToken)
            .ConfigureAwait(false);
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
            .BuildAsync([itemId], visible, normalized, MetadataFields.All, LocalizedImageTypes.All, cancellationToken)
            .ConfigureAwait(false);
        return batch.Items.Values.FirstOrDefault() is { } overlay ? overlay : NotFound();
    }

    /// <summary>
    /// Finds items by their localized names, in any stored locale: names in the caller's locale
    /// rank first. Case and accents don't matter ("cabana" finds "La cabaña del terror"), the last
    /// word matches as a prefix, and text in scripts written without spaces matches anywhere.
    /// </summary>
    /// <param name="q">What the user typed.</param>
    /// <param name="locale">The caller's BCP-47 locale.</param>
    /// <param name="limit">Most results (1 to <see cref="MaxSearchLimit"/>; default <see cref="DefaultSearchLimit"/>).</param>
    /// <param name="types">Comma-separated kinds: Movie, Series, Season, Episode. Default: all but Season.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <response code="200">The matches the caller may see, best first, one per item.</response>
    /// <response code="400">No searchable text in q, bad locale, limit out of range, or an unknown type.</response>
    [HttpGet("Search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SearchResponse>> Search(
        [FromQuery] string? q,
        [FromQuery] string? locale,
        [FromQuery] int? limit,
        [FromQuery] string? types,
        CancellationToken cancellationToken)
    {
        var query = SearchQuery.Parse(q);
        if (query.IsEmpty)
        {
            return BadRequest("q must contain at least one letter or digit.");
        }

        if (!LocaleChain.TryNormalize(locale, out var normalized))
        {
            return BadRequest("Locale must be a BCP-47 code such as es-MX.");
        }

        var count = limit ?? DefaultSearchLimit;
        if (count is < 1 or > MaxSearchLimit)
        {
            return BadRequest($"limit must be between 1 and {MaxSearchLimit}.");
        }

        if (!TryParseKinds(types, out var kinds, out var unknown))
        {
            return BadRequest($"Unknown type '{unknown}'. Known: {string.Join(", ", Enum.GetNames<ItemKind>())}.");
        }

        if (!TryGetCaller(out var userId))
        {
            return Forbid();
        }

        return await _search.SearchAsync(query, normalized, count, kinds, userId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Parses the comma-separated kinds; null or blank means movies, series and episodes.</summary>
    internal static bool TryParseKinds(string? requested, out IReadOnlySet<ItemKind> kinds, out string? unknown)
    {
        unknown = null;
        if (string.IsNullOrWhiteSpace(requested))
        {
            kinds = DefaultSearchKinds;
            return true;
        }

        var set = new HashSet<ItemKind>();
        foreach (var raw in requested.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Names only: Enum.TryParse would also take "2".
            if (!Enum.GetNames<ItemKind>().Any(n => string.Equals(n, raw, StringComparison.OrdinalIgnoreCase)))
            {
                unknown = raw;
                kinds = set;
                return false;
            }

            set.Add(Enum.Parse<ItemKind>(raw, ignoreCase: true));
        }

        kinds = set;
        return true;
    }

    internal static bool TryParseFields(IReadOnlyList<string>? requested, out IReadOnlySet<string> fields, out string? unknown)
    {
        unknown = null;
        if (requested is null)
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

    /// <summary>Parses the image filter: null means all types, an empty list means none.</summary>
    internal static bool TryParseImageTypes(IReadOnlyList<string>? requested, out IReadOnlySet<string> types, out string? unknown)
    {
        unknown = null;
        if (requested is null)
        {
            types = LocalizedImageTypes.All;
            return true;
        }

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in requested)
        {
            var match = LocalizedImageTypes.All.FirstOrDefault(t =>
                string.Equals(t, (raw ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                unknown = raw;
                types = set;
                return false;
            }

            set.Add(match);
        }

        types = set;
        return true;
    }

    /// <summary>
    /// Filters ids to what the caller may see. A user sees what their library access allows; an
    /// API key (admin-issued, no user) sees everything. Anything else is refused.
    /// </summary>
    private bool TryVisible(IReadOnlyCollection<Guid> ids, out IReadOnlyCollection<Guid> visible)
    {
        if (!TryGetCaller(out var userId))
        {
            visible = [];
            return false;
        }

        visible = userId is { } id ? _library.FilterVisible(id, ids).ToHashSet() : ids.ToHashSet();
        return true;
    }

    /// <summary>
    /// Identifies the caller from Jellyfin's claims: a user id, or null for an API key (which
    /// sees everything). False when the request carries neither.
    /// </summary>
    private bool TryGetCaller(out Guid? userId)
    {
        var principal = User ?? new ClaimsPrincipal();
        if (Guid.TryParse(principal.FindFirst(UserIdClaim)?.Value, out var id) && id != Guid.Empty)
        {
            userId = id;
            return true;
        }

        userId = null;
        return bool.TryParse(principal.FindFirst(IsApiKeyClaim)?.Value, out var isApiKey) && isApiKey;
    }
}
