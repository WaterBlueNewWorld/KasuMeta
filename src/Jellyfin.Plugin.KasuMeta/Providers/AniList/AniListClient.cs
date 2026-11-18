using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Localization;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KasuMeta.Providers.AniList;

/// <summary>Reads anime titles from AniList.</summary>
public interface IAniListClient
{
    /// <summary>
    /// Gets a title's text by its AniList media id (Jellyfin's <c>AniList</c> provider id), or
    /// null if AniList has no such media.
    /// </summary>
    Task<IReadOnlyList<AniListTranslation>?> GetMediaAsync(int mediaId, CancellationToken cancellationToken);
}

/// <summary>
/// AniList GraphQL client: one request per item, no key. Maps the English title and synopsis to
/// <c>en-US</c>, the native title to the country of origin's locale (<c>ja-JP</c>,
/// <c>zh-CN</c>, <c>zh-TW</c>, <c>ko-KR</c>) and the romanized title to <c>{language}-Latn</c>.
/// </summary>
/// <remarks>
/// AniList allowed 30 requests a minute when this was written (its <c>X-RateLimit-Limit</c>
/// header; the documented 90 is reduced while the API is "degraded"), so requests go one at a
/// time, at most <see cref="RequestsPerMinute"/> in any minute. 429, 5xx and network errors are
/// retried, honouring <c>Retry-After</c>. Requests always carry a User-Agent: AniList's
/// Cloudflare front refuses clients without a recognisable one (error 1010).
/// </remarks>
public sealed partial class AniListClient : IAniListClient, IDisposable
{
    internal const int RequestsPerMinute = 30;
    internal const int MaxRetries = 3;
    internal static readonly Uri Endpoint = new("https://graphql.anilist.co");

    /// <summary>The query sent for every item (the recorded fixtures answer exactly this).</summary>
    internal const string Query =
        "query ($id: Int) { Media(id: $id) { id countryOfOrigin title { romaji english native } description(asHtml: false) } }";

    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IKasuMetaConfigProvider _config;
    private readonly ILogger<AniListClient> _logger;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly SlidingWindowRateLimiter _rate = new(new SlidingWindowRateLimiterOptions
    {
        PermitLimit = RequestsPerMinute,
        Window = TimeSpan.FromMinutes(1),
        SegmentsPerWindow = 6,
        QueueLimit = 100_000,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        AutoReplenishment = true,
    });

    public AniListClient(IHttpClientFactory httpClientFactory, IKasuMetaConfigProvider config, ILogger<AniListClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    /// <summary>Gets or sets how the client waits between retries (tests replace it).</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <inheritdoc />
    public async Task<IReadOnlyList<AniListTranslation>?> GetMediaAsync(int mediaId, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new { query = Query, variables = new { id = mediaId } });
        for (var attempt = 0; ; attempt++)
        {
            using var lease = await _rate.AcquireAsync(1, cancellationToken).ConfigureAwait(false);
            await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
            TimeSpan wait;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
                {
                    Content = new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json),
                };
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("KasuMeta", _config.PluginVersion.ToString()));
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("(Jellyfin plugin)"));

                var http = _httpClientFactory.CreateClient(NamedClient.Default);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                // AniList answers an unknown id with 404 and a GraphQL error body.
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }

                if (response.IsSuccessStatusCode)
                {
                    var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await using (stream.ConfigureAwait(false))
                    {
                        var parsed = await JsonSerializer
                            .DeserializeAsync<AniListResponse>(stream, cancellationToken: cancellationToken)
                            .ConfigureAwait(false);
                        return parsed?.Data?.Media is { } media ? Map(media) : null;
                    }
                }

                if (!IsTransient(response.StatusCode) || attempt >= MaxRetries)
                {
                    throw new HttpRequestException(
                        $"AniList media {mediaId} failed with {(int)response.StatusCode} after {attempt + 1} attempt(s).",
                        null,
                        response.StatusCode);
                }

                wait = RetryAfter(response, attempt);
                AniListLog.Retrying(_logger, mediaId, (int)response.StatusCode, wait.TotalSeconds, attempt + 1);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is null && attempt < MaxRetries)
            {
                wait = Backoff(attempt);
                AniListLog.RetryingAfterError(_logger, mediaId, ex.Message, wait.TotalSeconds, attempt + 1);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested && attempt < MaxRetries)
            {
                wait = Backoff(attempt);
                AniListLog.RetryingAfterError(_logger, mediaId, ex.Message, wait.TotalSeconds, attempt + 1);
            }
            finally
            {
                _oneAtATime.Release();
            }

            await Delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _oneAtATime.Dispose();
        _rate.Dispose();
    }

    /// <summary>Maps a media entry to per-locale text; entries with no text are left out.</summary>
    internal static IReadOnlyList<AniListTranslation> Map(AniListMediaEntry media)
    {
        var (native, romanized) = LocalesFor(media.CountryOfOrigin);
        var result = new List<AniListTranslation>(3);
        Add("en-US", media.Title?.English, CleanDescription(media.Description));
        if (native is not null && romanized is not null)
        {
            Add(native, media.Title?.Native, null);
            Add(romanized, media.Title?.Romaji, null);
        }

        return result;

        void Add(string locale, string? title, string? overview)
        {
            title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
            if (title is not null || overview is not null)
            {
                result.Add(new AniListTranslation(locale, title, overview));
            }
        }
    }

    /// <summary>
    /// The native and romanized locales for a country of origin; nulls for a country whose
    /// language isn't known here (its native title can't be placed).
    /// </summary>
    internal static (string? Native, string? Romanized) LocalesFor(string? country) =>
        country?.Trim().ToUpperInvariant() switch
        {
            "JP" => ("ja-JP", "ja-Latn"),
            "CN" => ("zh-CN", "zh-Latn"),
            "TW" => ("zh-TW", "zh-Latn"),
            "KR" => ("ko-KR", "ko-Latn"),
            _ => (null, null),
        };

    /// <summary>
    /// Turns AniList's description into plain text: <c>&lt;br&gt;</c> becomes a line break, other
    /// tags go, entities are decoded, and a closing credit such as "(Source: Aniplex of America)"
    /// or "[Written by MAL Rewrite]" is dropped, as TMDB overviews carry none.
    /// </summary>
    internal static string? CleanDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var text = LineBreak().Replace(description, "\n");
        text = Tag().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text).Replace("\r\n", "\n", StringComparison.Ordinal);
        text = string.Join('\n', text.Split('\n').Select(line => line.Trim()));
        text = Credit().Replace(text, string.Empty);
        text = BlankLines().Replace(text, "\n\n").Trim();
        return text.Length == 0 ? null : text;
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status == HttpStatusCode.TooManyRequests || (int)status >= 500;

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 1 s, 2 s, 4 s

    private static TimeSpan RetryAfter(HttpResponseMessage response, int attempt)
    {
        var header = response.Headers.RetryAfter;
        TimeSpan? fromDate = header?.Date is { } date ? date - DateTimeOffset.UtcNow : null;
        var wait = header?.Delta ?? fromDate ?? Backoff(attempt);
        return wait < TimeSpan.Zero ? TimeSpan.Zero : wait > MaxRetryAfter ? MaxRetryAfter : wait;
    }

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LineBreak();

    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s*(\((?:Source|Sources):[^()]*\)|\[Written by [^\[\]]*\])\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Credit();

    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex BlankLines();
}

/// <summary>Source-generated log messages for the AniList client.</summary>
internal static partial class AniListLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "AniList media {MediaId} returned {Status}; retry {Attempt} in {Seconds}s")]
    public static partial void Retrying(ILogger logger, int mediaId, int status, double seconds, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AniList media {MediaId} failed ({Error}); retry {Attempt} in {Seconds}s")]
    public static partial void RetryingAfterError(ILogger logger, int mediaId, string error, double seconds, int attempt);
}
