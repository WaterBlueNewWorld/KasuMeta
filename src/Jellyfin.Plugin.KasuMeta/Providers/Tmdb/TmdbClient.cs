using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.RateLimiting;
using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Localization;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KasuMeta.Providers.Tmdb;

/// <summary>Reads translations from TMDB.</summary>
public interface ITmdbClient
{
    /// <summary>Gets every language's text for a movie, or null if TMDB has no such movie.</summary>
    Task<IReadOnlyList<TmdbTranslation>?> GetMovieTranslationsAsync(int movieId, CancellationToken cancellationToken);

    /// <summary>Gets every language's text for a series, or null if unknown.</summary>
    Task<IReadOnlyList<TmdbTranslation>?> GetSeriesTranslationsAsync(int seriesId, CancellationToken cancellationToken);

    /// <summary>Gets every language's text for a season, or null if unknown.</summary>
    Task<IReadOnlyList<TmdbTranslation>?> GetSeasonTranslationsAsync(int seriesId, int seasonNumber, CancellationToken cancellationToken);

    /// <summary>
    /// Gets every language's text for an episode, or null if unknown. Keyed by the series' id and
    /// numbers, since episodes in Jellyfin often carry no Tmdb id of their own.
    /// </summary>
    Task<IReadOnlyList<TmdbTranslation>?> GetEpisodeTranslationsAsync(
        int seriesId,
        int seasonNumber,
        int episodeNumber,
        CancellationToken cancellationToken);
}

/// <summary>
/// TMDB v3 client. One request per item returns every language, whatever the number of enabled
/// locales: movies and series ask for their details with <c>append_to_response=translations</c>
/// (the original title fills the original language's empty entry), seasons and episodes use
/// plain <c>/translations</c>.
/// </summary>
/// <remarks>
/// Polite by construction: at most <see cref="MaxConcurrency"/> requests in flight and
/// <see cref="RequestsPerSecond"/> per second (TMDB allows roughly 50), retries on 429/5xx and
/// network errors with backoff (honouring <c>Retry-After</c>), never on 401. The key is read
/// from the configuration per request, so a change on the dashboard applies at once, and it
/// is never logged.
/// </remarks>
public sealed class TmdbClient : ITmdbClient, IDisposable
{
    internal const int MaxConcurrency = 4;
    internal const int RequestsPerSecond = 20;
    internal const int MaxRetries = 3;
    internal static readonly Uri BaseUri = new("https://api.themoviedb.org/3/");
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IKasuMetaConfigProvider _config;
    private readonly ILogger<TmdbClient> _logger;
    private readonly SemaphoreSlim _concurrency = new(MaxConcurrency, MaxConcurrency);
    private readonly TokenBucketRateLimiter _rate = new(new TokenBucketRateLimiterOptions
    {
        TokenLimit = RequestsPerSecond,
        TokensPerPeriod = RequestsPerSecond,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        QueueLimit = 10_000,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        AutoReplenishment = true,
    });

    public TmdbClient(IHttpClientFactory httpClientFactory, IKasuMetaConfigProvider config, ILogger<TmdbClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    /// <summary>Gets or sets how the client waits between retries (tests replace it).</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <inheritdoc />
    /// <remarks>Details with appended translations: still one request, plus the original title.</remarks>
    public Task<IReadOnlyList<TmdbTranslation>?> GetMovieTranslationsAsync(int movieId, CancellationToken cancellationToken) =>
        GetDetailsTranslationsAsync(Path($"movie/{movieId}"), cancellationToken);

    /// <inheritdoc />
    /// <remarks>Details with appended translations: still one request, plus the original name.</remarks>
    public Task<IReadOnlyList<TmdbTranslation>?> GetSeriesTranslationsAsync(int seriesId, CancellationToken cancellationToken) =>
        GetDetailsTranslationsAsync(Path($"tv/{seriesId}"), cancellationToken);

    /// <inheritdoc />
    /// <remarks>Plain /translations: season details carry every episode (44 KB for one season).</remarks>
    public Task<IReadOnlyList<TmdbTranslation>?> GetSeasonTranslationsAsync(int seriesId, int seasonNumber, CancellationToken cancellationToken) =>
        GetPlainTranslationsAsync(Path($"tv/{seriesId}/season/{seasonNumber}/translations"), cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<TmdbTranslation>?> GetEpisodeTranslationsAsync(
        int seriesId,
        int seasonNumber,
        int episodeNumber,
        CancellationToken cancellationToken) =>
        GetPlainTranslationsAsync(
            Path($"tv/{seriesId}/season/{seasonNumber}/episode/{episodeNumber}/translations"),
            cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        _concurrency.Dispose();
        _rate.Dispose();
    }

    /// <summary>
    /// Builds a BCP-47 locale from TMDB's codes, or null if they don't make one. TMDB writes
    /// Cantonese as the non-standard <c>cn</c>; that becomes <c>yue</c>.
    /// </summary>
    internal static string? LocaleFromTmdb(string? language, string? region)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var lang = string.Equals(language.Trim(), "cn", StringComparison.OrdinalIgnoreCase) ? "yue" : language.Trim();
        var raw = string.IsNullOrWhiteSpace(region) ? lang : lang + "-" + region.Trim();
        return LocaleChain.TryNormalize(raw, out var locale) ? locale : null;
    }

    /// <summary>
    /// Normalizes translations. When the original language and title are known, entries in that
    /// language with no title get the original one (TMDB leaves them empty), and if TMDB has no
    /// entry for the original language at all, a bare-language entry (<c>ja</c>) carries it.
    /// </summary>
    internal static IReadOnlyList<TmdbTranslation> Map(
        TmdbTranslationsResponse response,
        string? originalLanguage = null,
        string? originalTitle = null)
    {
        var original = LocaleFromTmdb(originalLanguage, null);
        var originalText = NullIfEmpty(originalTitle);
        var result = new List<TmdbTranslation>(response.Translations.Count + 1);
        foreach (var entry in response.Translations)
        {
            var locale = LocaleFromTmdb(entry.Language, entry.Region);
            if (locale is null || entry.Data is null)
            {
                continue;
            }

            var title = NullIfEmpty(entry.Data.Title) ?? NullIfEmpty(entry.Data.Name);
            if (title is null && original is not null && LocaleChain.Language(locale) == original)
            {
                title = originalText;
            }

            var translation = new TmdbTranslation(
                locale,
                title,
                NullIfEmpty(entry.Data.Overview),
                NullIfEmpty(entry.Data.Tagline));
            if (translation is { Title: null, Overview: null, Tagline: null })
            {
                continue;
            }

            result.Add(translation);
        }

        if (original is not null && originalText is not null
            && !result.Any(t => LocaleChain.Language(t.Locale) == original && t.Title is not null))
        {
            result.Add(new TmdbTranslation(original, originalText, null, null));
        }

        return result;
    }

    private static string Path(FormattableString path) => path.ToString(CultureInfo.InvariantCulture);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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

    private async Task<IReadOnlyList<TmdbTranslation>?> GetDetailsTranslationsAsync(string path, CancellationToken cancellationToken)
    {
        var (found, details) = await GetJsonAsync<TmdbDetailsResponse>(path, "append_to_response=translations", cancellationToken)
            .ConfigureAwait(false);
        if (!found)
        {
            return null;
        }

        return details?.Translations is null
            ? []
            : Map(details.Translations, details.OriginalLanguage, details.OriginalTitle ?? details.OriginalName);
    }

    private async Task<IReadOnlyList<TmdbTranslation>?> GetPlainTranslationsAsync(string path, CancellationToken cancellationToken)
    {
        var (found, body) = await GetJsonAsync<TmdbTranslationsResponse>(path, null, cancellationToken).ConfigureAwait(false);
        if (!found)
        {
            return null;
        }

        return body is null ? [] : Map(body);
    }

    /// <summary>GETs and parses JSON; <c>Found</c> is false on 404.</summary>
    private async Task<(bool Found, T? Body)> GetJsonAsync<T>(string path, string? query, CancellationToken cancellationToken)
        where T : class
    {
        var key = _config.Current.TmdbApiKey?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            throw new ProviderNotConfiguredException("TMDB API key is not set (Dashboard › Plugins › KasuMeta).");
        }

        // A v4 read access token is a JWT; a v3 key is 32 hex characters.
        var bearer = key.StartsWith("eyJ", StringComparison.Ordinal);
        var parameters = new List<string>(2);
        if (query is not null)
        {
            parameters.Add(query);
        }

        if (!bearer)
        {
            parameters.Add("api_key=" + Uri.EscapeDataString(key));
        }

        var uri = new Uri(BaseUri, parameters.Count == 0 ? path : path + "?" + string.Join('&', parameters));

        for (var attempt = 0; ; attempt++)
        {
            using var lease = await _rate.AcquireAsync(1, cancellationToken).ConfigureAwait(false);
            await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            TimeSpan? wait;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (bearer)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                }

                var http = _httpClientFactory.CreateClient(NamedClient.Default);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await using (stream.ConfigureAwait(false))
                    {
                        var body = await JsonSerializer
                            .DeserializeAsync<T>(stream, cancellationToken: cancellationToken)
                            .ConfigureAwait(false);
                        return (true, body);
                    }
                }

                switch (response.StatusCode)
                {
                    case HttpStatusCode.NotFound:
                        return (false, null);
                    case HttpStatusCode.Unauthorized:
                        throw new ProviderAuthenticationException("TMDB rejected the API key (401).");
                }

                if (!IsTransient(response.StatusCode) || attempt >= MaxRetries)
                {
                    throw new HttpRequestException(
                        $"TMDB {path} failed with {(int)response.StatusCode} after {attempt + 1} attempt(s).",
                        null,
                        response.StatusCode);
                }

                wait = RetryAfter(response, attempt);
                TmdbLog.Retrying(_logger, path, (int)response.StatusCode, wait.Value.TotalSeconds, attempt + 1);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is null && attempt < MaxRetries)
            {
                // Network failure (DNS, reset, TLS): no response at all.
                wait = Backoff(attempt);
                TmdbLog.RetryingAfterError(_logger, path, ex.Message, wait.Value.TotalSeconds, attempt + 1);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested && attempt < MaxRetries)
            {
                // HttpClient timeout, not a cancellation from the caller.
                wait = Backoff(attempt);
                TmdbLog.RetryingAfterError(_logger, path, ex.Message, wait.Value.TotalSeconds, attempt + 1);
            }
            finally
            {
                _concurrency.Release();
            }

            await Delay(wait.Value, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Source-generated log messages for the TMDB client. Paths only: never the key.</summary>
internal static partial class TmdbLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "TMDB {Path} returned {Status}; retry {Attempt} in {Seconds}s")]
    public static partial void Retrying(ILogger logger, string path, int status, double seconds, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "TMDB {Path} failed ({Error}); retry {Attempt} in {Seconds}s")]
    public static partial void RetryingAfterError(ILogger logger, string path, string error, double seconds, int attempt);
}
