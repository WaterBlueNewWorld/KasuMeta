using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.RateLimiting;
using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Localization;
using Jellyfin.Plugin.KasuMeta.Storage;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KasuMeta.Providers.Tmdb;

/// <summary>Reads translations and localized images from TMDB.</summary>
public interface ITmdbClient
{
    /// <summary>
    /// Gets a movie's text, images, genres and keywords, or null if TMDB has no such movie.
    /// Images come in the enabled locales' languages plus <paramref name="extraImageLanguages"/>
    /// (the item's original language).
    /// </summary>
    Task<TmdbTitle?> GetMovieAsync(
        int movieId,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? extraImageLanguages = null);

    /// <summary>Gets a series' text, images, genres and keywords, or null if unknown.</summary>
    Task<TmdbTitle?> GetSeriesAsync(
        int seriesId,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? extraImageLanguages = null);

    /// <summary>Gets a season's text and posters, or null if unknown.</summary>
    Task<TmdbTitle?> GetSeasonAsync(
        int seriesId,
        int seasonNumber,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? extraImageLanguages = null);

    /// <summary>
    /// Gets TMDB's genre names in one locale by genre id (<c>/genre/{movie|tv}/list</c>), cached
    /// for a day. Genres TMDB has no name for in that locale are absent.
    /// </summary>
    Task<IReadOnlyDictionary<int, string>> GetGenreNamesAsync(bool tv, string locale, CancellationToken cancellationToken);

    /// <summary>
    /// Gets an episode's text (episode stills carry no language, so no images), or null if
    /// unknown. Keyed by the series' id and numbers, since episodes in Jellyfin often carry no
    /// Tmdb id of their own.
    /// </summary>
    Task<TmdbTitle?> GetEpisodeAsync(int seriesId, int seasonNumber, int episodeNumber, CancellationToken cancellationToken);
}

/// <summary>
/// TMDB v3 client. One request per item, whatever the number of enabled locales: movies, series
/// and seasons ask for their details with <c>append_to_response=translations,images</c> and
/// <c>include_image_language</c> set to the enabled languages; the original title fills the
/// original language's empty entry. Episodes use plain <c>/translations</c>.
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

    /// <summary>How long a genre list is reused; TMDB's genres hardly ever change.</summary>
    internal static readonly TimeSpan GenreListLifetime = TimeSpan.FromHours(24);

    /// <summary>Where TMDB serves original-size images; <c>file_path</c> starts with a slash.</summary>
    internal const string ImageBase = "https://image.tmdb.org/t/p/original";
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IKasuMetaConfigProvider _config;
    private readonly ILogger<TmdbClient> _logger;
    private readonly ProviderStats _stats;
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

    private readonly ConcurrentDictionary<(bool Tv, string Locale), (DateTimeOffset Fetched, IReadOnlyDictionary<int, string> Names)> _genres = new();

    public TmdbClient(
        IHttpClientFactory httpClientFactory,
        IKasuMetaConfigProvider config,
        ProviderStats stats,
        ILogger<TmdbClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _stats = stats;
        _logger = logger;
    }

    /// <summary>Gets or sets how the client waits between retries (tests replace it).</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <summary>Gets or sets the clock the genre cache uses (tests replace it).</summary>
    internal Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public Task<TmdbTitle?> GetMovieAsync(
        int movieId,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? extraImageLanguages = null) =>
        GetDetailsAsync(Path($"movie/{movieId}"), withKeywords: true, extraImageLanguages, cancellationToken);

    /// <inheritdoc />
    public Task<TmdbTitle?> GetSeriesAsync(
        int seriesId,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? extraImageLanguages = null) =>
        GetDetailsAsync(Path($"tv/{seriesId}"), withKeywords: true, extraImageLanguages, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Season details are heavier than /translations (they list every episode, ~45 KB) but carry
    /// the posters in the same request; requests, not bytes, are what TMDB limits.
    /// </remarks>
    public Task<TmdbTitle?> GetSeasonAsync(
        int seriesId,
        int seasonNumber,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? extraImageLanguages = null) =>
        GetDetailsAsync(Path($"tv/{seriesId}/season/{seasonNumber}"), withKeywords: false, extraImageLanguages, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, string>> GetGenreNamesAsync(bool tv, string locale, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        var key = (tv, locale);
        if (_genres.TryGetValue(key, out var cached) && Now() - cached.Fetched < GenreListLifetime)
        {
            return cached.Names;
        }

        var (found, body) = await GetJsonAsync<TmdbGenreListResponse>(
                tv ? "genre/tv/list" : "genre/movie/list",
                "language=" + Uri.EscapeDataString(TmdbLanguage(locale)),
                cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyDictionary<int, string> names = !found || body is null
            ? new Dictionary<int, string>()
            : body.Genres
                .Where(g => g.Id > 0 && !string.IsNullOrWhiteSpace(g.Name))
                .GroupBy(g => g.Id)
                .ToDictionary(g => g.Key, g => g.First().Name!.Trim());
        _genres[key] = (Now(), names);
        return names;
    }

    /// <inheritdoc />
    public Task<TmdbTitle?> GetEpisodeAsync(int seriesId, int seasonNumber, int episodeNumber, CancellationToken cancellationToken) =>
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

    /// <summary>TMDB's spelling of a locale: <c>es-MX</c> stays as it is; Cantonese is TMDB's <c>cn</c>.</summary>
    internal static string TmdbLanguage(string locale)
    {
        var normalized = LocaleChain.TryNormalize(locale, out var n) ? n : locale;
        return LocaleChain.Language(normalized) == "yue" ? "cn" + normalized[3..] : normalized;
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

    /// <summary>
    /// Builds TMDB's <c>include_image_language</c> from the enabled locales: their distinct
    /// languages (TMDB's <c>cn</c> for Cantonese). Empty when nothing usable is enabled.
    /// </summary>
    internal static string ImageLanguages(IEnumerable<string> enabledLocales, IEnumerable<string>? extra = null) =>
        string.Join(
            ',',
            enabledLocales
                .Concat(extra ?? [])
                .Select(l => LocaleChain.TryNormalize(l, out var n) ? LocaleChain.Language(n) : null)
                .OfType<string>()
                .Select(l => l == "yue" ? "cn" : l)
                .Distinct(StringComparer.Ordinal));

    /// <summary>
    /// Picks the best image per locale and type: raster before SVG (TMDB has SVG logos, which
    /// Flutter's image widget can't draw), then highest vote average, vote count and width, with
    /// the path as the last tie-break so the choice is stable. Images without a language
    /// (textless art) aren't localized and are left to Jellyfin.
    /// </summary>
    internal static IReadOnlyList<TmdbImage> MapImages(TmdbImagesResponse? response)
    {
        if (response is null)
        {
            return [];
        }

        IEnumerable<(string Type, TmdbImageEntry Entry)> All() =>
            response.Posters.Select(e => (LocalizedImageTypes.Primary, e))
                .Concat(response.Logos.Select(e => (LocalizedImageTypes.Logo, e)))
                .Concat(response.Backdrops.Select(e => (LocalizedImageTypes.Backdrop, e)));

        return All()
            .Where(x => !string.IsNullOrWhiteSpace(x.Entry.FilePath) && x.Entry.Width > 0 && x.Entry.Height > 0)
            .Select(x => (x.Type, x.Entry, Locale: LocaleFromTmdb(x.Entry.Language, x.Entry.Region)))
            .Where(x => x.Locale is not null)
            .GroupBy(x => (x.Locale, x.Type))
            .Select(g => g
                .OrderBy(x => x.Entry.FilePath!.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(x => x.Entry.VoteAverage)
                .ThenByDescending(x => x.Entry.VoteCount)
                .ThenByDescending(x => x.Entry.Width)
                .ThenBy(x => x.Entry.FilePath, StringComparer.Ordinal)
                .First())
            .Select(x => new TmdbImage(x.Locale!, x.Type, ImageBase + x.Entry.FilePath, x.Entry.Width, x.Entry.Height))
            .OrderBy(i => i.Locale, StringComparer.Ordinal)
            .ThenBy(i => i.ImageType, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<TmdbTitle?> GetDetailsAsync(
        string path,
        bool withKeywords,
        IReadOnlyCollection<string>? extraImageLanguages,
        CancellationToken cancellationToken)
    {
        var languages = ImageLanguages(_config.Current.EnabledLocales, extraImageLanguages);
        var append = "translations"
                     + (languages.Length == 0 ? string.Empty : ",images")
                     + (withKeywords ? ",keywords" : string.Empty);
        var query = languages.Length == 0
            ? "append_to_response=" + append
            : "append_to_response=" + append + "&include_image_language=" + languages;
        var (found, details) = await GetJsonAsync<TmdbDetailsResponse>(path, query, cancellationToken).ConfigureAwait(false);
        if (!found)
        {
            return null;
        }

        if (details is null)
        {
            return new TmdbTitle([], []);
        }

        var translations = details.Translations is null
            ? []
            : Map(details.Translations, details.OriginalLanguage, details.OriginalTitle ?? details.OriginalName);
        var genres = (details.Genres ?? [])
            .Where(g => g.Id > 0 && !string.IsNullOrWhiteSpace(g.Name))
            .Select(g => new TmdbGenre(g.Id, g.Name!.Trim()))
            .ToList();
        var keywords = ListValues.Clean((details.Keywords?.Keywords ?? details.Keywords?.Results ?? []).Select(k => k.Name));
        return new TmdbTitle(
            translations,
            MapImages(details.Images),
            LocaleFromTmdb(details.OriginalLanguage, null),
            genres,
            keywords);
    }

    private async Task<TmdbTitle?> GetPlainTranslationsAsync(string path, CancellationToken cancellationToken)
    {
        var (found, body) = await GetJsonAsync<TmdbTranslationsResponse>(path, null, cancellationToken).ConfigureAwait(false);
        if (!found)
        {
            return null;
        }

        return new TmdbTitle(body is null ? [] : Map(body), []);
    }

    /// <summary>GETs and parses JSON; <c>Found</c> is false on 404. Counts what happened for the status page.</summary>
    private async Task<(bool Found, T? Body)> GetJsonAsync<T>(string path, string? query, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await GetJsonCoreAsync<T>(path, query, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _stats.Failure(MetadataSources.Tmdb, ex.Message);
            throw;
        }
    }

    private async Task<(bool Found, T? Body)> GetJsonCoreAsync<T>(string path, string? query, CancellationToken cancellationToken)
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
                _stats.Request(MetadataSources.Tmdb);
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
                        _stats.NotFound(MetadataSources.Tmdb);
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

            _stats.Retry(MetadataSources.Tmdb);
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
