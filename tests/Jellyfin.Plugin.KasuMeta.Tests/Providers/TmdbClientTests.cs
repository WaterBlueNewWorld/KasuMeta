using System.Net;
using System.Text;
using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Providers;
using Jellyfin.Plugin.KasuMeta.Providers.Tmdb;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.KasuMeta.Tests.Providers;

public class TmdbClientTests
{
    // Hand-written in the shape TMDB's API reference documents for /translations. Replace with
    // recorded responses once a key is configured on the dev server (PLAN.md §3, Phase 1.3).
    private const string MovieTranslations = """
        {
          "id": 129,
          "translations": [
            { "iso_3166_1": "MX", "iso_639_1": "es", "name": "Español", "english_name": "Spanish",
              "data": { "homepage": "", "overview": "Chihiro, una niña de diez años…", "runtime": 125,
                        "tagline": "", "title": "El viaje de Chihiro" } },
            { "iso_3166_1": "JP", "iso_639_1": "ja", "name": "日本語", "english_name": "Japanese",
              "data": { "homepage": "", "overview": "", "runtime": 0, "tagline": "", "title": "千と千尋の神隠し" } },
            { "iso_3166_1": "HK", "iso_639_1": "cn", "name": "广州话 / 廣州話", "english_name": "Cantonese",
              "data": { "homepage": "", "overview": "", "runtime": 0, "tagline": "", "title": "千與千尋" } },
            { "iso_3166_1": "", "iso_639_1": "eo", "name": "Esperanto", "english_name": "Esperanto",
              "data": { "homepage": "", "overview": "Resumo", "runtime": 0, "tagline": "", "title": "" } },
            { "iso_3166_1": "DE", "iso_639_1": "de", "name": "Deutsch", "english_name": "German",
              "data": { "homepage": "", "overview": "", "runtime": 0, "tagline": "", "title": "" } }
          ]
        }
        """;

    private const string SeriesTranslations = """
        {
          "id": 80834,
          "translations": [
            { "iso_3166_1": "MX", "iso_639_1": "es", "name": "Español", "english_name": "Spanish",
              "data": { "name": "Revue Starlight", "overview": "Karen y Hikari…", "homepage": "", "tagline": "Posición cero" } }
          ]
        }
        """;

    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Movie_translations_are_normalized()
    {
        var (client, handler, _) = Create("0123456789abcdef0123456789abcdef");
        handler.Respond(HttpStatusCode.OK, MovieTranslations);

        var translations = await client.GetMovieTranslationsAsync(129, None);

        Assert.NotNull(translations);
        var es = Assert.Single(translations, t => t.Locale == "es-MX");
        Assert.Equal(("El viaje de Chihiro", "Chihiro, una niña de diez años…", (string?)null), (es.Title, es.Overview, es.Tagline));
        Assert.Null(Assert.Single(translations, t => t.Locale == "ja-JP").Overview);
        Assert.Equal("千與千尋", Assert.Single(translations, t => t.Locale == "yue-HK").Title); // TMDB "cn"
        Assert.Equal("Resumo", Assert.Single(translations, t => t.Locale == "eo").Overview); // no region
        Assert.DoesNotContain(translations, t => t.Locale == "de-DE"); // nothing but empty strings
    }

    [Fact]
    public async Task Series_use_name_for_the_title()
    {
        var (client, handler, _) = Create("0123456789abcdef0123456789abcdef");
        handler.Respond(HttpStatusCode.OK, SeriesTranslations);

        var es = Assert.Single((await client.GetSeriesTranslationsAsync(80834, None))!);

        Assert.Equal(("Revue Starlight", "Posición cero"), (es.Title, es.Tagline));
    }

    [Fact]
    public async Task Each_kind_calls_its_path()
    {
        var (client, handler, _) = Create("0123456789abcdef0123456789abcdef");
        for (var i = 0; i < 4; i++)
        {
            handler.Respond(HttpStatusCode.OK, """{"translations":[]}""");
        }

        await client.GetMovieTranslationsAsync(129, None);
        await client.GetSeriesTranslationsAsync(80834, None);
        await client.GetSeasonTranslationsAsync(80834, 1, None);
        await client.GetEpisodeTranslationsAsync(80834, 1, 3, None);

        Assert.Equal(
            ["/3/movie/129/translations", "/3/tv/80834/translations", "/3/tv/80834/season/1/translations", "/3/tv/80834/season/1/episode/3/translations"],
            handler.Requests.Select(r => r.RequestUri!.AbsolutePath));
        Assert.All(handler.Requests, r => Assert.Equal("api.themoviedb.org", r.RequestUri!.Host));
    }

    [Fact]
    public async Task A_v3_key_goes_in_the_query()
    {
        var (client, handler, _) = Create("0123456789abcdef0123456789abcdef");
        handler.Respond(HttpStatusCode.OK, """{"translations":[]}""");

        await client.GetMovieTranslationsAsync(1, None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("?api_key=0123456789abcdef0123456789abcdef", request.RequestUri!.Query);
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task A_v4_token_goes_in_the_bearer_header()
    {
        var (client, handler, _) = Create("eyJhbGciOiJIUzI1NiJ9.e30.sig");
        handler.Respond(HttpStatusCode.OK, """{"translations":[]}""");

        await client.GetMovieTranslationsAsync(1, None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(string.Empty, request.RequestUri!.Query);
        Assert.Equal(("Bearer", "eyJhbGciOiJIUzI1NiJ9.e30.sig"), (request.Headers.Authorization!.Scheme, request.Headers.Authorization.Parameter));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task No_key_means_no_request(string key)
    {
        var (client, handler, _) = Create(key);

        await Assert.ThrowsAsync<ProviderNotConfiguredException>(() => client.GetMovieTranslationsAsync(1, None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Not_found_is_null()
    {
        var (client, handler, _) = Create("0123456789abcdef0123456789abcdef");
        handler.Respond(HttpStatusCode.NotFound, """{"success":false,"status_code":34}""");

        Assert.Null(await client.GetMovieTranslationsAsync(999999999, None));
    }

    [Fact]
    public async Task A_rejected_key_is_not_retried()
    {
        var (client, handler, delays) = Create("0123456789abcdef0123456789abcdef");
        handler.Respond(HttpStatusCode.Unauthorized, """{"status_code":7}""");

        await Assert.ThrowsAsync<ProviderAuthenticationException>(() => client.GetMovieTranslationsAsync(1, None));
        Assert.Single(handler.Requests);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Too_many_requests_waits_for_Retry_After()
    {
        var (client, handler, delays) = Create("0123456789abcdef0123456789abcdef");
        handler.Respond(HttpStatusCode.TooManyRequests, "{}", retryAfterSeconds: 2);
        handler.Respond(HttpStatusCode.OK, MovieTranslations);

        var translations = await client.GetMovieTranslationsAsync(129, None);

        Assert.NotEmpty(translations!);
        Assert.Equal([TimeSpan.FromSeconds(2)], delays);
    }

    [Fact]
    public async Task Server_errors_back_off_then_give_up()
    {
        var (client, handler, delays) = Create("0123456789abcdef0123456789abcdef");
        for (var i = 0; i <= TmdbClient.MaxRetries; i++)
        {
            handler.Respond(HttpStatusCode.BadGateway, "{}");
        }

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetMovieTranslationsAsync(1, None));

        Assert.Equal(HttpStatusCode.BadGateway, error.StatusCode);
        Assert.Equal(TmdbClient.MaxRetries + 1, handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], delays);
        Assert.DoesNotContain("0123456789abcdef", error.Message, StringComparison.Ordinal); // never the key
    }

    [Fact]
    public async Task Network_errors_are_retried()
    {
        var (client, handler, delays) = Create("0123456789abcdef0123456789abcdef");
        handler.Fail(new HttpRequestException("connection reset"));
        handler.Respond(HttpStatusCode.OK, """{"translations":[]}""");

        Assert.NotNull(await client.GetMovieTranslationsAsync(1, None));
        Assert.Equal([TimeSpan.FromSeconds(1)], delays);
    }

    [Fact]
    public async Task Other_client_errors_fail_at_once()
    {
        var (client, handler, delays) = Create("0123456789abcdef0123456789abcdef");
        handler.Respond(HttpStatusCode.BadRequest, "{}");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetMovieTranslationsAsync(1, None));
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_retried()
    {
        var (client, handler, delays) = Create("0123456789abcdef0123456789abcdef");
        handler.Respond(HttpStatusCode.OK, "{}");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetMovieTranslationsAsync(1, cts.Token));
        Assert.Empty(delays);
    }

    [Theory]
    [InlineData("es", "MX", "es-MX")]
    [InlineData("pt", "BR", "pt-BR")]
    [InlineData("zh", "TW", "zh-TW")]
    [InlineData("cn", "CN", "yue-CN")]
    [InlineData("en", "", "en")]
    [InlineData("en", null, "en")]
    [InlineData("", "US", null)]
    [InlineData("xx-bad", "US", null)]
    public void Locales_are_built_from_tmdb_codes(string? language, string? region, string? expected)
    {
        Assert.Equal(expected, TmdbClient.LocaleFromTmdb(language, region));
    }

    private static (TmdbClient Client, FakeHandler Handler, List<TimeSpan> Delays) Create(string key)
    {
        var handler = new FakeHandler();
        var config = new FakeConfig(new PluginConfiguration { TmdbApiKey = key });
        var delays = new List<TimeSpan>();
        var client = new TmdbClient(new FakeHttpClientFactory(handler), config, NullLogger<TmdbClient>.Instance)
        {
            Delay = (wait, _) =>
            {
                delays.Add(wait);
                return Task.CompletedTask;
            },
        };
        return (client, handler, delays);
    }

    private sealed class FakeConfig(PluginConfiguration config) : IKasuMetaConfigProvider
    {
        public PluginConfiguration Current => config;

        public Version PluginVersion => new(0, 0, 1, 0);
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();

        public List<HttpRequestMessage> Requests { get; } = [];

        public void Respond(HttpStatusCode status, string json, int? retryAfterSeconds = null) =>
            _responses.Enqueue(() =>
            {
                var response = new HttpResponseMessage(status)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
                if (retryAfterSeconds is { } seconds)
                {
                    response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
                }

                return response;
            });

        public void Fail(Exception exception) => _responses.Enqueue(() => throw exception);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(_responses.Dequeue()());
        }
    }
}
