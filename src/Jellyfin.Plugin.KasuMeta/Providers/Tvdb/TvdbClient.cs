using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Localization;
using Jellyfin.Plugin.KasuMeta.Storage;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KasuMeta.Providers.Tvdb;

/// <summary>Reads translations and localized artwork from TVDB (API v4).</summary>
public interface ITvdbClient
{
    /// <summary>Gets a movie's text and artwork by its TVDB id, or null if TVDB has no such movie.</summary>
    Task<TvdbTitle?> GetMovieAsync(int movieId, CancellationToken cancellationToken);

    /// <summary>Gets a series' text and artwork, or null if unknown.</summary>
    Task<TvdbTitle?> GetSeriesAsync(int seriesId, CancellationToken cancellationToken);

    /// <summary>Gets a season's text and artwork by its own TVDB id, or null if unknown.</summary>
    Task<TvdbTitle?> GetSeasonAsync(int seasonId, CancellationToken cancellationToken);

    /// <summary>Gets an episode's text (no artwork: stills carry no language), or null if unknown.</summary>
    Task<TvdbTitle?> GetEpisodeAsync(int episodeId, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the TVDB id of a series' season by number, in the series' default episode order;
    /// null if the series or the season is unknown.
    /// </summary>
    Task<int?> FindSeasonIdAsync(int seriesId, int seasonNumber, CancellationToken cancellationToken);

    /// <summary>Finds an episode's TVDB id by its series and numbers (default order); null if unknown.</summary>
    Task<int?> FindEpisodeIdAsync(int seriesId, int seasonNumber, int episodeNumber, CancellationToken cancellationToken);

    /// <summary>Finds a movie's or series' TVDB id by its IMDb id (<c>tt1259521</c>); null if unknown.</summary>
    Task<int?> FindByImdbIdAsync(string imdbId, bool series, CancellationToken cancellationToken);
}

/// <summary>
/// TVDB v4 client. One request per record: extended records with <c>meta=translations</c> carry
/// every language's name and overview, and (movies, series, seasons) their artwork. Seasons and
/// episodes without a TVDB id of their own cost one lookup more, by the series' id and numbers.
/// </summary>
/// <remarks>
/// <para>
/// TVDB authenticates with a bearer token from <c>POST /login</c> (the API key, plus a
/// subscriber PIN for user-supported keys). The token lasts a month; it's kept in memory, renewed
/// after <see cref="TokenLifetime"/> or when TVDB answers 401, and dropped when the key changes.
/// Neither the key, the PIN nor the token is ever logged.
/// </para>
/// <para>
/// TVDB publishes no rate limit; the client stays polite like the TMDB one: at most
/// <see cref="MaxConcurrency"/> requests in flight and <see cref="RequestsPerSecond"/> per second,
/// retries on 429/5xx and network errors with backoff (honouring <c>Retry-After</c>).
/// </para>
/// </remarks>
public sealed partial class TvdbClient : ITvdbClient, IDisposable
{
    internal const int MaxConcurrency = 4;
    internal const int RequestsPerSecond = 10;
    internal const int MaxRetries = 3;
    internal static readonly Uri BaseUri = new("https://api4.thetvdb.com/v4/");

    /// <summary>How long a token is reused; TVDB's last a month.</summary>
    internal static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(25);

    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>Artwork type ids (<c>/artwork/types</c>) KasuMeta keeps, per record kind.</summary>
    private static readonly Dictionary<int, string> MovieArtwork = new()
    {
        [14] = LocalizedImageTypes.Primary,
        [15] = LocalizedImageTypes.Backdrop,
        [25] = LocalizedImageTypes.Logo,
    };

    private static readonly Dictionary<int, string> SeriesArtwork = new()
    {
        [2] = LocalizedImageTypes.Primary,
        [3] = LocalizedImageTypes.Backdrop,
        [23] = LocalizedImageTypes.Logo,
    };

    private static readonly Dictionary<int, string> SeasonArtwork = new()
    {
        [7] = LocalizedImageTypes.Primary,
        [8] = LocalizedImageTypes.Backdrop,
    };

    private static readonly Dictionary<int, string> NoArtwork = [];

    /// <summary>
    /// TVDB language ids (<c>/languages</c>, mostly ISO 639-2) as BCP-47. TVDB splits only
    /// Portuguese and Chinese by country, so those carry a region; everything else is the bare
    /// language. Ids missing here are skipped.
    /// </summary>
    private static readonly Dictionary<string, string> Languages = BuildLanguages(
        "aar:aa abk:ab afr:af aka:ak amh:am ara:ar arg:an asm:as ava:av ave:ae aym:ay aze:az bak:ba bam:bm " +
        "bel:be ben:bn bih:bh bis:bi bod:bo bos:bs bre:br bul:bg cat:ca ces:cs cha:ch che:ce chu:cu chv:cv " +
        "cor:kw cos:co cre:cr cym:cy dan:da deu:de div:dv dzo:dz ell:el eng:en epo:eo est:et eus:eu ewe:ee " +
        "fao:fo fas:fa fij:fj fin:fi fra:fr fry:fy ful:ff gla:gd gle:ga glg:gl glv:gv grn:gn guj:gu hat:ht " +
        "hau:ha heb:he her:hz hin:hi hmo:ho hrv:hr hun:hu hye:hy ibo:ig ido:io iii:ii iku:iu ile:ie ina:ia " +
        "ind:id ipk:ik isl:is ita:it jav:jv jpn:ja kal:kl kan:kn kas:ks kat:ka kau:kr kaz:kk khm:km kik:ki " +
        "kin:rw kir:ky kom:kv kon:kg kor:ko kua:kj kur:ku lao:lo lat:la lav:lv lim:li lin:ln lit:lt ltz:lb " +
        "lub:lu lug:lg mah:mh mal:ml mar:mr mkd:mk mlg:mg mlt:mt mon:mn mri:mi msa:ms mya:my nau:na nav:nv " +
        "nbl:nr nde:nd ndo:ng nep:ne nld:nl nor:no nya:ny oci:oc oji:oj ori:or orm:om oss:os pan:pa pli:pi " +
        "pol:pl por:pt-PT pt:pt-BR pus:ps que:qu roh:rm ron:ro run:rn rus:ru sag:sg san:sa sin:si slk:sk " +
        "slv:sl sme:se smo:sm sna:sn snd:sd som:so sot:st spa:es sqi:sq srd:sc srp:sr ssw:ss sun:su swa:sw " +
        "swe:sv tah:ty tam:ta tat:tt tel:te tgk:tg tgl:tl tha:th tir:ti ton:to tsn:tn tso:ts tuk:tk tur:tr " +
        "twi:tw uig:ug ukr:uk urd:ur uzb:uz ven:ve vie:vi vol:vo wln:wa wol:wo xho:xh yid:yi yor:yo " +
        "yue:yue zha:za zho:zh-CN zhtw:zh-TW zul:zu");

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IKasuMetaConfigProvider _config;
    private readonly ProviderStats _stats;
    private readonly TimeProvider _time;
    private readonly ILogger<TvdbClient> _logger;
    private readonly SemaphoreSlim _login = new(1, 1);
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

    private volatile Session? _session;

    public TvdbClient(
        IHttpClientFactory httpClientFactory,
        IKasuMetaConfigProvider config,
        ProviderStats stats,
        TimeProvider time,
        ILogger<TvdbClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _stats = stats;
        _time = time;
        _logger = logger;
    }

    /// <summary>Gets or sets how the client waits between retries (tests replace it).</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <inheritdoc />
    public Task<TvdbTitle?> GetMovieAsync(int movieId, CancellationToken cancellationToken) =>
        GetTitleAsync(Path($"movies/{movieId}/extended?meta=translations"), MovieArtwork, fillOriginal: true, cancellationToken);

    /// <inheritdoc />
    public Task<TvdbTitle?> GetSeriesAsync(int seriesId, CancellationToken cancellationToken) =>
        GetTitleAsync(Path($"series/{seriesId}/extended?meta=translations"), SeriesArtwork, fillOriginal: true, cancellationToken);

    /// <inheritdoc />
    public Task<TvdbTitle?> GetSeasonAsync(int seasonId, CancellationToken cancellationToken) =>
        GetTitleAsync(Path($"seasons/{seasonId}/extended?meta=translations"), SeasonArtwork, fillOriginal: false, cancellationToken);

    /// <inheritdoc />
    public Task<TvdbTitle?> GetEpisodeAsync(int episodeId, CancellationToken cancellationToken) =>
        GetTitleAsync(Path($"episodes/{episodeId}/extended?meta=translations"), NoArtwork, fillOriginal: false, cancellationToken);

    /// <inheritdoc />
    public Task<int?> FindSeasonIdAsync(int seriesId, int seasonNumber, CancellationToken cancellationToken) =>
        CountedAsync(async () =>
        {
            var (found, series) = await GetAsync<TvdbRecord>(Path($"series/{seriesId}/extended?short=true"), cancellationToken).ConfigureAwait(false);
            return found && series is not null ? PickSeason(series, seasonNumber) : null;
        }, cancellationToken);

    /// <inheritdoc />
    public Task<int?> FindEpisodeIdAsync(int seriesId, int seasonNumber, int episodeNumber, CancellationToken cancellationToken) =>
        CountedAsync(async () =>
        {
            var (found, page) = await GetAsync<TvdbEpisodePage>(
                    Path($"series/{seriesId}/episodes/default?page=0&season={seasonNumber}&episodeNumber={episodeNumber}"),
                    cancellationToken)
                .ConfigureAwait(false);
            return found
                ? page?.Episodes?.FirstOrDefault(e => e.SeasonNumber == seasonNumber && e.Number == episodeNumber && e.Id > 0)?.Id
                : null;
        }, cancellationToken);

    /// <inheritdoc />
    public Task<int?> FindByImdbIdAsync(string imdbId, bool series, CancellationToken cancellationToken)
    {
        // Into the path, so only the shape IMDb ids have.
        if (string.IsNullOrWhiteSpace(imdbId) || !ImdbId().IsMatch(imdbId.Trim()))
        {
            return Task.FromResult<int?>(null);
        }

        return CountedAsync(async () =>
        {
            var (found, hits) = await GetAsync<List<TvdbRemoteIdHit>>("search/remoteid/" + imdbId.Trim(), cancellationToken).ConfigureAwait(false);
            if (!found || hits is null)
            {
                return null;
            }

            var match = hits.Select(h => series ? h.Series : h.Movie).FirstOrDefault(r => r is { Id: > 0 });
            return match?.Id;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _login.Dispose();
        _concurrency.Dispose();
        _rate.Dispose();
    }

    /// <summary>Turns a TVDB language id (<c>spa</c>, <c>pt</c>, <c>zhtw</c>) into BCP-47, or null if unknown.</summary>
    internal static string? LocaleFromTvdb(string? language) =>
        language is not null && Languages.TryGetValue(language.Trim().ToLowerInvariant(), out var locale) ? locale : null;

    /// <summary>
    /// Normalizes a record's text: one entry per locale, aliases left out, empty strings as
    /// null. With <paramref name="fillOriginal"/> (movies and series), the record's own name
    /// stands in for the original language's title when TVDB lists no translation for it.
    /// </summary>
    internal static IReadOnlyList<TvdbTranslation> MapTranslations(TvdbRecord record, bool fillOriginal)
    {
        var translations = record.Translations.ValueKind == JsonValueKind.Object
            ? record.Translations.Deserialize<TvdbTranslations>()
            : null;

        var order = new List<string>();
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        var taglines = new Dictionary<string, string>(StringComparer.Ordinal);
        var overviews = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in Usable(translations?.NameTranslations))
        {
            var locale = LocaleFromTvdb(entry.Language)!;
            Put(titles, locale, entry.Name);
            Put(taglines, locale, entry.Tagline);
        }

        foreach (var entry in Usable(translations?.OverviewTranslations))
        {
            Put(overviews, LocaleFromTvdb(entry.Language)!, entry.Overview);
        }

        if (fillOriginal
            && LocaleFromTvdb(record.OriginalLanguage) is { } original
            && NullIfEmpty(record.Name) is { } name
            && !titles.ContainsKey(original))
        {
            Put(titles, original, name);
        }

        return order
            .Select(l => new TvdbTranslation(l, titles.GetValueOrDefault(l), overviews.GetValueOrDefault(l), taglines.GetValueOrDefault(l)))
            .Where(t => t is not { Title: null, Overview: null, Tagline: null })
            .ToList();

        void Put(Dictionary<string, string> into, string locale, string? value)
        {
            if (NullIfEmpty(value) is { } text && into.TryAdd(locale, text) && !order.Contains(locale, StringComparer.Ordinal))
            {
                order.Add(locale);
            }
        }

        // The primary entry first, so it wins over a second one in the same language.
        static IEnumerable<TvdbTranslationEntry> Usable(List<TvdbTranslationEntry>? entries) =>
            (entries ?? [])
                .Where(e => e.IsAlias != true && LocaleFromTvdb(e.Language) is not null)
                .OrderByDescending(e => e.IsPrimary == true);
    }

    /// <summary>
    /// Picks the best artwork per locale and type: raster before SVG, then TVDB's score, then
    /// width, with the URL as the last tie-break so the choice is stable. Textless art (no
    /// language) and types KasuMeta doesn't localize are left to Jellyfin.
    /// </summary>
    internal static IReadOnlyList<TvdbImage> MapImages(IEnumerable<TvdbArtwork>? artworks, IReadOnlyDictionary<int, string> types) =>
        (artworks ?? [])
            .Where(a => !string.IsNullOrWhiteSpace(a.Image) && a.Width > 0 && a.Height > 0 && types.ContainsKey(a.Type))
            .Select(a => (Type: types[a.Type], Art: a, Locale: LocaleFromTvdb(a.Language)))
            .Where(x => x.Locale is not null)
            .GroupBy(x => (x.Locale, x.Type))
            .Select(g => g
                .OrderBy(x => x.Art.Image!.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(x => x.Art.Score)
                .ThenByDescending(x => x.Art.Width)
                .ThenBy(x => x.Art.Image, StringComparer.Ordinal)
                .First())
            .Select(x => new TvdbImage(x.Locale!, x.Type, x.Art.Image!.Trim(), x.Art.Width, x.Art.Height))
            .OrderBy(i => i.Locale, StringComparer.Ordinal)
            .ThenBy(i => i.ImageType, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The season with this number in the series' default order (TVDB lists one season per
    /// order: aired, DVD, absolute…), or in aired order when the default isn't known.
    /// </summary>
    internal static int? PickSeason(TvdbRecord series, int seasonNumber)
    {
        var numbered = (series.Seasons ?? []).Where(s => s.Number == seasonNumber && s.Id > 0).ToList();
        var pick = numbered.FirstOrDefault(s => series.DefaultSeasonType is { } t && s.Type?.Id == t)
                   ?? numbered.FirstOrDefault(s => s.Type?.Type == "official");
        return pick?.Id;
    }

    private static Dictionary<string, string> BuildLanguages(string pairs) =>
        pairs.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split(':'))
            .ToDictionary(p => p[0], p => LocaleChain.Normalize(p[1]), StringComparer.Ordinal);

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

    [GeneratedRegex("^tt[0-9]{1,12}$", RegexOptions.CultureInvariant)]
    private static partial Regex ImdbId();

    private Task<TvdbTitle?> GetTitleAsync(
        string path,
        IReadOnlyDictionary<int, string> artworkTypes,
        bool fillOriginal,
        CancellationToken cancellationToken) =>
        CountedAsync(async () =>
        {
            var (found, record) = await GetAsync<TvdbRecord>(path, cancellationToken).ConfigureAwait(false);
            if (!found)
            {
                return null;
            }

            return record is null
                ? new TvdbTitle([], [])
                : new TvdbTitle(
                    MapTranslations(record, fillOriginal),
                    MapImages(record.Artworks ?? record.Artwork, artworkTypes),
                    fillOriginal ? LocaleFromTvdb(record.OriginalLanguage) : null,
                    ListValues.Clean((record.Genres ?? []).Select(g => g.Name)),
                    ListValues.Clean((record.Tags ?? []).Select(t => t.Name)));
        }, cancellationToken);

    /// <summary>Counts a call's failure for the status page, as the other clients do.</summary>
    private async Task<T> CountedAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _stats.Failure(MetadataSources.Tvdb, ex.Message);
            throw;
        }
    }

    /// <summary>GETs a record; <c>Found</c> is false on 404. Logs in first, and once more on a 401.</summary>
    private async Task<(bool Found, T? Data)> GetAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        var token = await TokenAsync(null, cancellationToken).ConfigureAwait(false);
        var (status, body) = await SendAsync<TvdbEnvelope<T>>(() => Get(path, token), path, cancellationToken).ConfigureAwait(false);
        if (status == HttpStatusCode.Unauthorized)
        {
            // Expired or revoked token: one fresh login, then give up.
            token = await TokenAsync(token, cancellationToken).ConfigureAwait(false);
            (status, body) = await SendAsync<TvdbEnvelope<T>>(() => Get(path, token), path, cancellationToken).ConfigureAwait(false);
            if (status == HttpStatusCode.Unauthorized)
            {
                throw new ProviderAuthenticationException("TVDB rejected the token (401).");
            }
        }

        if (status == HttpStatusCode.NotFound)
        {
            _stats.NotFound(MetadataSources.Tvdb);
            return (false, null);
        }

        return (true, body?.Data);
    }

    /// <summary>
    /// A valid token for the configured key: the cached one, or a new login when there is none,
    /// it's old, it belongs to another key, or it is <paramref name="rejected"/>.
    /// </summary>
    private async Task<string> TokenAsync(string? rejected, CancellationToken cancellationToken)
    {
        var config = _config.Current;
        var key = config.TvdbApiKey?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            throw new ProviderNotConfiguredException("TVDB API key is not set (Dashboard › Plugins › KasuMeta).");
        }

        var pin = config.TvdbPin?.Trim() ?? string.Empty;
        var credentials = key + "\n" + pin;
        if (Usable(_session) is { } cached)
        {
            return cached;
        }

        await _login.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Usable(_session) is { } fresh)
            {
                return fresh; // another request logged in meanwhile
            }

            object body = pin.Length == 0 ? new { apikey = key } : new { apikey = key, pin };
            var json = JsonSerializer.Serialize(body);
            var (status, login) = await SendAsync<TvdbEnvelope<TvdbLoginData>>(
                    () => new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, "login"))
                    {
                        Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json),
                    },
                    "login",
                    cancellationToken)
                .ConfigureAwait(false);
            if (status == HttpStatusCode.Unauthorized)
            {
                _session = null;
                throw new ProviderAuthenticationException(
                    pin.Length == 0
                        ? "TVDB rejected the API key (401). A user-supported key also needs its subscriber PIN."
                        : "TVDB rejected the API key or PIN (401).");
            }

            if (status != HttpStatusCode.OK || login?.Data?.Token is not { Length: > 0 } token)
            {
                throw new HttpRequestException($"TVDB login failed with {(int)status}.", null, status);
            }

            _session = new Session(token, credentials, _time.GetUtcNow() + TokenLifetime);
            return token;
        }
        finally
        {
            _login.Release();
        }

        string? Usable(Session? session) =>
            session is not null
            && session.Credentials == credentials
            && session.Token != rejected
            && _time.GetUtcNow() < session.Expires
                ? session.Token
                : null;
    }

    private static HttpRequestMessage Get(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>
    /// Sends with retries on 429, 5xx and network errors. Returns the status with the parsed body
    /// on success, and without one on 401 and 404 (the caller decides); anything else throws.
    /// </summary>
    private async Task<(HttpStatusCode Status, T? Body)> SendAsync<T>(
        Func<HttpRequestMessage> build,
        string what,
        CancellationToken cancellationToken)
        where T : class
    {
        for (var attempt = 0; ; attempt++)
        {
            using var lease = await _rate.AcquireAsync(1, cancellationToken).ConfigureAwait(false);
            await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            TimeSpan wait;
            try
            {
                using var request = build();
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("KasuMeta", _config.PluginVersion.ToString()));

                var http = _httpClientFactory.CreateClient(NamedClient.Default);
                _stats.Request(MetadataSources.Tvdb);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await using (stream.ConfigureAwait(false))
                    {
                        var body = await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                        return (response.StatusCode, body);
                    }
                }

                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
                {
                    return (response.StatusCode, null);
                }

                if (!IsTransient(response.StatusCode) || attempt >= MaxRetries)
                {
                    throw new HttpRequestException(
                        $"TVDB {what} failed with {(int)response.StatusCode} after {attempt + 1} attempt(s).",
                        null,
                        response.StatusCode);
                }

                wait = RetryAfter(response, attempt);
                TvdbLog.Retrying(_logger, what, (int)response.StatusCode, wait.TotalSeconds, attempt + 1);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is null && attempt < MaxRetries)
            {
                wait = Backoff(attempt);
                TvdbLog.RetryingAfterError(_logger, what, ex.Message, wait.TotalSeconds, attempt + 1);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested && attempt < MaxRetries)
            {
                wait = Backoff(attempt);
                TvdbLog.RetryingAfterError(_logger, what, ex.Message, wait.TotalSeconds, attempt + 1);
            }
            finally
            {
                _concurrency.Release();
            }

            _stats.Retry(MetadataSources.Tvdb);
            await Delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A login: the token, which key and PIN it was made for, and until when it's reused.</summary>
    private sealed record Session(string Token, string Credentials, DateTimeOffset Expires);
}

/// <summary>Source-generated log messages for the TVDB client. Paths only: never the key, PIN or token.</summary>
internal static partial class TvdbLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "TVDB {Path} returned {Status}; retry {Attempt} in {Seconds}s")]
    public static partial void Retrying(ILogger logger, string path, int status, double seconds, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "TVDB {Path} failed ({Error}); retry {Attempt} in {Seconds}s")]
    public static partial void RetryingAfterError(ILogger logger, string path, string error, double seconds, int attempt);
}
