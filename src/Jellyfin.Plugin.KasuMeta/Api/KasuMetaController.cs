using System.Globalization;
using System.Net.Mime;
using System.Security.Claims;
using Jellyfin.Plugin.KasuMeta.Api.Models;
using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Fetching;
using Jellyfin.Plugin.KasuMeta.Library;
using Jellyfin.Plugin.KasuMeta.Localization;
using Jellyfin.Plugin.KasuMeta.Search;
using Jellyfin.Plugin.KasuMeta.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.KasuMeta.Api;

/// <summary>
/// KasuMeta's HTTP API. Every route sits behind Jellyfin's own auth, so a client sends the
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

    /// <summary>Longest manual text value, in UTF-16 units.</summary>
    public const int MaxTextLength = 10_000;

    /// <summary>Longest manual image URL.</summary>
    public const int MaxUrlLength = 2_048;

    /// <summary>Largest uploaded image file: 10 MiB.</summary>
    public const int MaxUploadBytes = 10 * 1024 * 1024;

    /// <summary>Most terms in one manual genre or tag list.</summary>
    public const int MaxListValues = 100;

    /// <summary>Longest genre or tag (and dictionary term or translation), in UTF-16 units.</summary>
    public const int MaxTermLength = 200;

    /// <summary>Room for the multipart envelope around an upload of <see cref="MaxUploadBytes"/>.</summary>
    private const int MultipartOverhead = 64 * 1024;

    /// <summary>
    /// Jellyfin's administrators-only policy (Jellyfin.Api.Constants.Policies.RequiresElevation,
    /// not on NuGet, hence the literal). An admin user or an API key passes it.
    /// </summary>
    internal const string ElevationPolicy = "RequiresElevation";

    /// <summary>
    /// What this build can do beyond the Info route, for clients to switch on. The API grows by
    /// adding routes and optional properties (ApiVersion stays), so "is search there?" is asked
    /// here rather than guessed from a version number.
    /// </summary>
    internal static readonly string[] Features =
        ["text", "images", "search", "overrides", "genres", "tags", "uploads", "terms", "original-language"];

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
    private readonly ILocalizedMetadataStore _store;
    private readonly IFetchQueue _queue;
    private readonly StatusService _status;
    private readonly TermDictionary _terms;

    public KasuMetaController(
        IKasuMetaConfigProvider config,
        OverlayService overlays,
        SearchService search,
        ILibraryItemSource library,
        ILocalizedMetadataStore store,
        IFetchQueue queue,
        StatusService status,
        TermDictionary terms)
    {
        _terms = terms;
        _config = config;
        _overlays = overlays;
        _search = search;
        _library = library;
        _store = store;
        _queue = queue;
        _status = status;
    }

    /// <summary>
    /// Capability check: a client calls this after sign-in. A 404 means the plugin isn't installed,
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
            Features: Features,
            FallbackToOriginalLanguage: config.FallbackToOriginalLanguage);
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
    /// Admin: how the plugin is doing. Configuration health, fetch progress (queue length, items
    /// per outcome), the newest failures, and each provider's counters since the server started.
    /// The plugin's configuration page shows it.
    /// </summary>
    /// <response code="200">The status.</response>
    /// <response code="403">Not an administrator.</response>
    [HttpGet("Status")]
    [Authorize(Policy = ElevationPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<KasuMetaStatus>> GetStatus(CancellationToken cancellationToken) =>
        await _status.BuildAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Admin view of one item: every stored locale as stored (no fallback), with each value's
    /// source and lock, and the item's fetch state. What the translations editor loads.
    /// </summary>
    /// <response code="200">The stored values; Locales is empty when nothing is stored.</response>
    /// <response code="403">Not an administrator.</response>
    /// <response code="404">No such item in the library.</response>
    [HttpGet("Items/{itemId}/All")]
    [Authorize(Policy = ElevationPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ItemTranslations>> GetAllLocales([FromRoute] Guid itemId, CancellationToken cancellationToken)
    {
        if (_library.GetItem(itemId) is null)
        {
            return NotFound();
        }

        return await _overlays.BuildAllLocalesAsync(itemId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets or removes manual values of one item in one locale, in one transaction. A value
    /// always replaces what is stored, locked or not. A null removes the stored value and queues
    /// a re-fetch, so the provider's value comes back if it has one.
    /// </summary>
    /// <response code="200">Applied; the body is the item's admin view after the change.</response>
    /// <response code="400">Bad locale, unknown or repeated key, empty text, bad URL, or nothing to change.</response>
    /// <response code="403">Not an administrator.</response>
    /// <response code="404">No such item in the library.</response>
    [HttpPut("Items/{itemId}/{locale}")]
    [Authorize(Policy = ElevationPolicy)]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ItemTranslations>> SetOverrides(
        [FromRoute] Guid itemId,
        [FromRoute] string locale,
        [FromBody] ItemOverridesRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!LocaleChain.TryNormalize(locale, out var normalized))
        {
            return BadRequest("Locale must be a BCP-47 code such as es-MX.");
        }

        if (!TryBuildEdit(itemId, normalized, request, out var edit, out var error))
        {
            return BadRequest(error);
        }

        if (_library.GetItem(itemId) is null)
        {
            return NotFound();
        }

        await _store.ApplyManualEditAsync(edit, cancellationToken).ConfigureAwait(false);
        if (edit.RemoveFields.Count > 0 || edit.RemoveImages.Count > 0)
        {
            _queue.Enqueue(itemId, force: true);
        }

        return await _overlays.BuildAllLocalesAsync(itemId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Queues a forced re-fetch of one item from its providers. Locked values stay. Poll
    /// <c>Items/{itemId}/All</c> for a newer <c>Fetch.LastFetch</c> to see it done.
    /// </summary>
    /// <response code="202">Queued.</response>
    /// <response code="403">Not an administrator.</response>
    /// <response code="404">No such item in the library.</response>
    [HttpPost("Items/{itemId}/Refresh")]
    [Authorize(Policy = ElevationPolicy)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Refresh([FromRoute] Guid itemId)
    {
        if (_library.GetItem(itemId) is null)
        {
            return NotFound();
        }

        _queue.Enqueue(itemId, force: true);
        return Accepted();
    }

    /// <summary>
    /// Validates an override request and turns it into a store edit. Values are trimmed; text
    /// must be non-empty and at most <see cref="MaxTextLength"/> long, image URLs absolute http(s)
    /// and at most <see cref="MaxUrlLength"/> long.
    /// </summary>
    internal static bool TryBuildEdit(
        Guid itemId,
        string locale,
        ItemOverridesRequest request,
        out ManualEdit edit,
        out string? error)
    {
        var setFields = new List<LocalizedField>();
        var setImages = new List<LocalizedImage>();
        var removeFields = new List<string>();
        var removeImages = new List<string>();
        edit = new ManualEdit(itemId, locale, setFields, setImages, removeFields, removeImages);
        error = null;

        if ((request.Fields?.Count ?? 0) + (request.Images?.Count ?? 0) == 0)
        {
            error = "Nothing to change: give at least one field or image.";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (raw, value) in request.Fields ?? new Dictionary<string, ManualFieldValue?>())
        {
            if (!TryParseFields([raw], out var parsed, out _))
            {
                error = $"Unknown field '{raw}'. Known: {string.Join(", ", MetadataFields.All.Order(StringComparer.Ordinal))}.";
                return false;
            }

            var field = parsed.Single();
            if (!seen.Add(field))
            {
                error = $"Field '{raw}' is given twice.";
                return false;
            }

            if (value is null)
            {
                removeFields.Add(field);
                continue;
            }

            if (MetadataFields.ListFields.Contains(field))
            {
                if (!TryBuildList(raw, value, out var json, out error))
                {
                    return false;
                }

                setFields.Add(new LocalizedField(itemId, locale, field, json, MetadataSources.Manual, value.Locked));
                continue;
            }

            var text = value.Value?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                error = $"Field '{raw}' is empty; send null to remove it.";
                return false;
            }

            if (text.Length > MaxTextLength)
            {
                error = $"Field '{raw}' is longer than {MaxTextLength} characters.";
                return false;
            }

            setFields.Add(new LocalizedField(itemId, locale, field, text, MetadataSources.Manual, value.Locked));
        }

        foreach (var (raw, value) in request.Images ?? new Dictionary<string, ManualImageValue?>())
        {
            if (!TryParseImageTypes([raw], out var parsed, out _))
            {
                error = $"Unknown image type '{raw}'. Known: {string.Join(", ", LocalizedImageTypes.All.Order(StringComparer.Ordinal))}.";
                return false;
            }

            var type = parsed.Single();
            if (!seen.Add(type))
            {
                error = $"Image type '{raw}' is given twice.";
                return false;
            }

            if (value is null)
            {
                removeImages.Add(type);
                continue;
            }

            var url = value.Url?.Trim();
            if (string.IsNullOrEmpty(url)
                || url.Length > MaxUrlLength
                || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                error = $"Image '{raw}' needs an absolute http(s) URL of at most {MaxUrlLength} characters; send null to remove it.";
                return false;
            }

            setImages.Add(new LocalizedImage(itemId, locale, type, url, MetadataSources.Manual, value.Locked));
        }

        return true;
    }

    /// <summary>
    /// Validates a manual genre or tag list: 1 to <see cref="MaxListValues"/> terms, each at most
    /// <see cref="MaxTermLength"/> long, after trimming and dropping empty and repeated ones.
    /// </summary>
    internal static bool TryBuildList(string raw, ManualFieldValue value, out string json, out string? error)
    {
        json = string.Empty;
        error = null;
        var values = ListValues.Clean(value.Values ?? []);
        if (values.Count == 0)
        {
            error = $"Field '{raw}' needs a non-empty Values list; send null to remove it.";
            return false;
        }

        if (values.Count > MaxListValues)
        {
            error = $"Field '{raw}' has more than {MaxListValues} values.";
            return false;
        }

        if (values.Any(v => v.Length > MaxTermLength))
        {
            error = $"Field '{raw}' has a value longer than {MaxTermLength} characters.";
            return false;
        }

        json = ListValues.Serialize(values)!;
        return true;
    }

    /// <summary>
    /// Stores an uploaded image file (JPEG, PNG, WebP or GIF, at most 10 MiB) as the manual image
    /// of one item, locale and type, replacing the stored one (provider, URL or file). The file's
    /// format is checked by its content; its size is read from its header.
    /// </summary>
    /// <remarks>
    /// The form is read here rather than bound, so that a body over the limit answers 413 (model
    /// binding would turn it into a validation 400). Its parts: <c>file</c>, the image; and
    /// <c>locked</c>, whether provider refreshes must leave the image alone (default true).
    /// </remarks>
    /// <param name="itemId">The item.</param>
    /// <param name="locale">The locale the image is for.</param>
    /// <param name="imageType">Primary, Logo, Backdrop or Thumb (any casing).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <response code="200">Stored; the body is the item's admin view after the change.</response>
    /// <response code="400">Bad locale or image type, a malformed form, no file, or not a JPEG, PNG, WebP or GIF.</response>
    /// <response code="403">Not an administrator.</response>
    /// <response code="404">No such item in the library.</response>
    /// <response code="413">The file is larger than 10 MiB.</response>
    [HttpPut("Items/{itemId}/{locale}/Images/{imageType}")]
    [Authorize(Policy = ElevationPolicy)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxUploadBytes + MultipartOverhead)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes + MultipartOverhead)]
    [DisableFormValueModelBinding]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<ItemTranslations>> UploadImage(
        [FromRoute] Guid itemId,
        [FromRoute] string locale,
        [FromRoute] string imageType,
        CancellationToken cancellationToken)
    {
        if (Request.ContentLength > MaxUploadBytes + MultipartOverhead)
        {
            return TooLarge();
        }

        IFormCollection form;
        try
        {
            form = await Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            // A chunked body (no Content-Length) that ran past the limit.
            return TooLarge();
        }
        catch (InvalidDataException ex)
        {
            // The form reader's own limits ("Multipart body length limit … exceeded") or a broken body.
            return ex.Message.Contains("limit", StringComparison.OrdinalIgnoreCase)
                   || ex.Message.Contains("too large", StringComparison.OrdinalIgnoreCase)
                ? TooLarge()
                : BadRequest("The multipart form could not be read.");
        }

        bool? locked = bool.TryParse(form["locked"].ToString(), out var parsed) ? parsed : null;
        return await StoreUploadAsync(itemId, locale, imageType, form.Files.GetFile("file"), locked, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Validates and stores an upload (<see cref="UploadImage"/> after the form is read).</summary>
    internal async Task<ActionResult<ItemTranslations>> StoreUploadAsync(
        Guid itemId,
        string locale,
        string imageType,
        IFormFile? file,
        bool? locked,
        CancellationToken cancellationToken)
    {
        if (!LocaleChain.TryNormalize(locale, out var normalized))
        {
            return BadRequest("Locale must be a BCP-47 code such as es-MX.");
        }

        if (!TryParseImageTypes([imageType], out var types, out _))
        {
            return BadRequest(
                $"Unknown image type '{imageType}'. Known: {string.Join(", ", LocalizedImageTypes.All.Order(StringComparer.Ordinal))}.");
        }

        if (file is null || file.Length == 0)
        {
            return BadRequest("Send the image as the multipart form's 'file' part.");
        }

        if (file.Length > MaxUploadBytes)
        {
            return TooLarge();
        }

        if (_library.GetItem(itemId) is null)
        {
            return NotFound();
        }

        var data = new byte[file.Length];
        var stream = file.OpenReadStream();
        await using (stream.ConfigureAwait(false))
        {
            await stream.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
        }

        if (ImageHeader.Read(data) is not { } header)
        {
            return BadRequest("The file is not a JPEG, PNG, WebP or GIF image.");
        }

        var image = new LocalizedImage(
            itemId,
            normalized,
            types.Single(),
            string.Empty,
            MetadataSources.Manual,
            locked ?? true,
            header.Width,
            header.Height);
        await _store.SetUploadedImageAsync(image, data, cancellationToken).ConfigureAwait(false);
        return await _overlays.BuildAllLocalesAsync(itemId, cancellationToken).ConfigureAwait(false);
    }

    private ObjectResult TooLarge() =>
        StatusCode(StatusCodes.Status413PayloadTooLarge, $"The image is larger than {MaxUploadBytes / (1024 * 1024)} MiB.");

    /// <summary>
    /// An uploaded localized image file, for anyone who may see the item. Overlays point here
    /// (<c>Uploaded: true</c>); the <c>v</c> query parameter they add only busts caches.
    /// </summary>
    /// <response code="200">The file.</response>
    /// <response code="304">Unchanged since the ETag sent in If-None-Match.</response>
    /// <response code="400">Bad locale or image type.</response>
    /// <response code="404">No uploaded file for that item, locale and type, or the item isn't visible.</response>
    [HttpGet("Items/{itemId}/{locale}/Images/{imageType}")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK, "image/jpeg", "image/png", "image/webp", "image/gif")]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetUploadedImage(
        [FromRoute] Guid itemId,
        [FromRoute] string locale,
        [FromRoute] string imageType,
        CancellationToken cancellationToken)
    {
        if (!LocaleChain.TryNormalize(locale, out var normalized))
        {
            return BadRequest("Locale must be a BCP-47 code such as es-MX.");
        }

        if (!TryParseImageTypes([imageType], out var types, out _))
        {
            return BadRequest($"Unknown image type '{imageType}'.");
        }

        if (!TryVisible([itemId], out var visible))
        {
            return Forbid();
        }

        if (!visible.Contains(itemId)
            || await _store.GetImageFileAsync(itemId, normalized, types.Single(), cancellationToken).ConfigureAwait(false) is not { } stored)
        {
            return NotFound();
        }

        var etag = string.Create(CultureInfo.InvariantCulture, $"\"{stored.UpdatedAt.ToUnixTimeSeconds()}\"");
        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "private, max-age=86400";
        Response.Headers.XContentTypeOptions = "nosniff";
        if (Request.Headers.IfNoneMatch.Any(v => v is not null && v.Split(',').Any(t => t.Trim() == etag)))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        var contentType = ImageHeader.Read(stored.Data)?.ContentType ?? MediaTypeNames.Application.Octet;
        return File(stored.Data, contentType);
    }

    /// <summary>
    /// Admin: the genre or tag dictionary for one locale. Lists every term the library's items
    /// carry in English and every term the dictionary holds, with its translation (if any) and
    /// how many items use it; untranslated terms first.
    /// </summary>
    /// <param name="kind"><c>genre</c> or <c>tag</c>.</param>
    /// <param name="locale">The locale of the translations.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <response code="200">The terms.</response>
    /// <response code="400">Unknown kind or bad locale.</response>
    /// <response code="403">Not an administrator.</response>
    [HttpGet("Terms")]
    [Authorize(Policy = ElevationPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<TermList>> GetTerms(
        [FromQuery] string? kind,
        [FromQuery] string? locale,
        CancellationToken cancellationToken)
    {
        if (!TryParseTermKind(kind, out var parsedKind))
        {
            return BadRequest("kind must be 'genre' or 'tag'.");
        }

        if (!LocaleChain.TryNormalize(locale, out var normalized))
        {
            return BadRequest("Locale must be a BCP-47 code such as es-MX.");
        }

        return await BuildTermsAsync(parsedKind, normalized, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Admin: sets or removes dictionary translations of one kind and locale, in one transaction.
    /// Overlays use them at once, with no re-fetch.
    /// </summary>
    /// <response code="200">Applied; the body is the kind's terms for that locale after the change.</response>
    /// <response code="400">Unknown kind, bad locale, nothing to change, or an empty or too long term or translation.</response>
    /// <response code="403">Not an administrator.</response>
    [HttpPut("Terms/{kind}/{locale}")]
    [Authorize(Policy = ElevationPolicy)]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<TermList>> SetTerms(
        [FromRoute] string kind,
        [FromRoute] string locale,
        [FromBody] TermsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryParseTermKind(kind, out var parsedKind))
        {
            return BadRequest("kind must be 'genre' or 'tag'.");
        }

        if (!LocaleChain.TryNormalize(locale, out var normalized))
        {
            return BadRequest("Locale must be a BCP-47 code such as es-MX.");
        }

        if (!TryBuildTerms(request, out var clean, out var error))
        {
            return BadRequest(error);
        }

        await _terms.SetAsync(parsedKind, normalized, clean, cancellationToken).ConfigureAwait(false);
        return await BuildTermsAsync(parsedKind, normalized, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Parses a dictionary kind (<c>genre</c>, <c>tag</c>; any casing, plurals too).</summary>
    internal static bool TryParseTermKind(string? raw, out string kind)
    {
        kind = (raw ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "GENRE" or "GENRES" => TermKinds.Genre,
            "TAG" or "TAGS" => TermKinds.Tag,
            _ => string.Empty,
        };
        return kind.Length > 0;
    }

    /// <summary>
    /// Validates dictionary translations: at least one; terms 1 to <see cref="MaxTermLength"/>
    /// long and not repeated (ignoring case); translations at most as long. An empty or null
    /// translation removes the term's.
    /// </summary>
    internal static bool TryBuildTerms(TermsRequest request, out IReadOnlyDictionary<string, string?> translations, out string? error)
    {
        var clean = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        translations = clean;
        error = null;
        if (request.Translations is not { Count: > 0 } given)
        {
            error = "Nothing to change: give at least one translation.";
            return false;
        }

        foreach (var (rawTerm, rawValue) in given)
        {
            var term = rawTerm?.Trim();
            if (string.IsNullOrEmpty(term) || term.Length > MaxTermLength)
            {
                error = $"Terms must be 1 to {MaxTermLength} characters long.";
                return false;
            }

            var value = rawValue?.Trim();
            if (value is { Length: > MaxTermLength })
            {
                error = $"The translation of '{term}' is longer than {MaxTermLength} characters.";
                return false;
            }

            if (!clean.TryAdd(term, string.IsNullOrEmpty(value) ? null : value))
            {
                error = $"Term '{term}' is given twice.";
                return false;
            }
        }

        return true;
    }

    private async Task<TermList> BuildTermsAsync(string kind, string locale, CancellationToken cancellationToken)
    {
        var used = await _store.GetTermsInUseAsync(kind, cancellationToken).ConfigureAwait(false);
        var stored = await _store.GetTermsAsync(kind, null, cancellationToken).ConfigureAwait(false);
        var translations = stored
            .Where(t => t.Locale == locale)
            .ToDictionary(t => t.Term, t => t.Value, StringComparer.OrdinalIgnoreCase);
        var items = used.ToDictionary(u => u.Term, u => u.Items, StringComparer.OrdinalIgnoreCase);
        var terms = used.Select(u => u.Term)
            .Concat(stored.Select(t => t.Term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(t => new TermEntry(t, translations.GetValueOrDefault(t), items.GetValueOrDefault(t)))
            .OrderBy(t => t.Translation is not null)
            .ThenByDescending(t => t.Items)
            .ThenBy(t => t.Term, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new TermList { Kind = kind, Locale = locale, Terms = terms };
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
