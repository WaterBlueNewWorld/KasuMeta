using System.Net;
using System.Text;
using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Providers;
using Jellyfin.Plugin.KasuMeta.Providers.Tmdb;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.KasuMeta.Tests.Providers;

public class TmdbClientTests
{
    private const string V3Key = "0123456789abcdef0123456789abcdef";
    private const string V4Token = "eyJhbGciOiJIUzI1NiJ9.e30.sig";

    // Shapes as recorded: details nest the appended translations one level deeper.
    private const string EmptyDetails = """{"original_language":"en","translations":{"translations":[]}}""";
    private const string EmptyTranslations = """{"translations":[]}""";
    private static readonly CancellationToken None = CancellationToken.None;

    // ---- Recorded responses (Fixtures/README.md) ----

    [Fact]
    public async Task Antitrust_gets_its_english_title_from_original_title()
    {
        var translations = await RecordedAsync("movie-9989.json", c => c.GetMovieAsync(9989, None));

        // TMDB's en-US entry has an empty title: English is the original language.
        Assert.Equal("Antitrust", Find(translations, "en-US").Title);
        Assert.StartsWith("Truth can be dangerous...Trust", Find(translations, "en-US").Tagline, StringComparison.Ordinal);
        Assert.Equal("Amenaza Virtual", Find(translations, "es-MX").Title);
        Assert.Equal("Conspiración en la red: Hackers 3", Find(translations, "es-ES").Title);
        Assert.Equal("奪命連線", Find(translations, "zh-TW").Title);
        var ja = Find(translations, "ja-JP");
        Assert.Equal(("サベイランス", (string?)null), (ja.Title, ja.Overview)); // title only
    }

    [Fact]
    public async Task Cabin_in_the_woods_differs_between_mexico_and_spain()
    {
        var translations = await RecordedAsync("movie-22970.json", c => c.GetMovieAsync(22970, None));

        Assert.Equal("La cabaña del terror", Find(translations, "es-MX").Title);
        Assert.Equal("La cabaña en el bosque", Find(translations, "es-ES").Title);
        Assert.Equal("The Cabin in the Woods", Find(translations, "en-US").Title);
        Assert.Equal("林中小屋", Find(translations, "zh-CN").Title);
        Assert.Equal("キャビン", Find(translations, "ja-JP").Title);
        Assert.All(translations, t => Assert.False(t is { Title: null, Overview: null, Tagline: null }));
    }

    [Fact]
    public async Task Magia_record_gets_its_japanese_title_from_original_name()
    {
        var translations = await RecordedAsync("tv-94523.json", c => c.GetSeriesAsync(94523, None));

        var ja = Find(translations, "ja-JP");
        Assert.Equal("マギアレコード 魔法少女まどか☆マギカ外伝", ja.Title); // empty in /translations
        Assert.NotNull(ja.Overview);
        Assert.Equal("それは、少女たちも知らない、願いの物語。", ja.Tagline);
        // es-MX has a title but no overview; es-ES has both. Fallback is per field (LocaleChain).
        Assert.Null(Find(translations, "es-MX").Overview);
        Assert.NotNull(Find(translations, "es-ES").Overview);
    }

    [Fact]
    public async Task A_translated_title_is_kept_over_the_original_name()
    {
        var translations = await RecordedAsync("tv-80834.json", c => c.GetSeriesAsync(80834, None));

        // The ja-JP translation has its own spelling (with "・"); original_name has none.
        Assert.Equal("少女☆歌劇 レヴュー・スタァライト", Find(translations, "ja-JP").Title);
        Assert.Equal("Revue Starlight", Find(translations, "en-US").Title);
        Assert.Equal("我，再生产", Find(translations, "zh-CN").Tagline);
    }

    [Fact]
    public async Task Season_names_are_mostly_missing()
    {
        var translations = await RecordedAsync("tv-80834-s1.json", c => c.GetSeasonAsync(80834, 1, None));

        Assert.Equal("少女☆歌剧 Revue Starlight", Find(translations, "zh-CN").Title);
        Assert.DoesNotContain(translations, t => t.Locale is "es-MX" or "es-ES" or "en-US"); // all empty
    }

    [Fact]
    public async Task Episode_titles_come_in_each_language()
    {
        var translations = await RecordedAsync(
            "tv-80834-s1e1.translations.json",
            c => c.GetEpisodeAsync(80834, 1, 1, None));

        Assert.Equal("Stage Girls", Find(translations, "en-US").Title);
        Assert.Equal("Chicas del escenario", Find(translations, "es-MX").Title);
        Assert.Equal("舞台少女", Find(translations, "ja-JP").Title);
        Assert.DoesNotContain(translations, t => t.Locale == "es-ES"); // empty
    }

    [Fact]
    public async Task Not_found_is_null()
    {
        var (client, handler, _) = Create(V4Token);
        handler.Respond(HttpStatusCode.NotFound, Fixture("movie-404.json"));

        Assert.Null(await client.GetMovieAsync(999999999, None));
    }

    // ---- Images (recorded) ----

    [Fact]
    public async Task Cabin_in_the_woods_has_regional_posters_and_logos()
    {
        var images = (await RecordedTitleAsync("movie-22970.json", c => c.GetMovieAsync(22970, None))).Images;

        var mx = Image(images, "es-MX", "Primary");
        Assert.Equal("https://image.tmdb.org/t/p/original/iLIe4sykUb0NxZpSKcHZmaqbgoy.jpg", mx.Url);
        Assert.Equal((2000, 3000), (mx.Width, mx.Height));
        Assert.NotEqual(mx.Url, Image(images, "es-ES", "Primary").Url);
        Assert.NotNull(Image(images, "ja-JP", "Logo"));
        // Chinese art comes per region, so Simplified (CN) and Traditional (TW, HK) stay apart.
        Assert.Equal(["zh-CN", "zh-HK", "zh-TW"], images.Where(i => i.ImageType == "Primary" && i.Locale.StartsWith("zh", StringComparison.Ordinal)).Select(i => i.Locale));
        Assert.All(images, i => Assert.StartsWith(TmdbClient.ImageBase + "/", i.Url, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_raster_logo_is_preferred_over_a_better_voted_svg()
    {
        // The recorded en-US logos of The Cabin in the Woods include an SVG.
        var images = (await RecordedTitleAsync("movie-22970.json", c => c.GetMovieAsync(22970, None))).Images;

        Assert.DoesNotContain(".svg", Image(images, "en-US", "Logo").Url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_season_brings_its_posters_and_an_episode_none()
    {
        var season = await RecordedTitleAsync("tv-80834-s1.json", c => c.GetSeasonAsync(80834, 1, None));
        var episode = await RecordedTitleAsync("tv-80834-s1e1.translations.json", c => c.GetEpisodeAsync(80834, 1, 1, None));

        Assert.Equal(("ja-JP", "Primary"), (Assert.Single(season.Images).Locale, season.Images[0].ImageType));
        Assert.Empty(episode.Images);
    }

    [Theory]
    [InlineData(new[] { "en-US", "es-ES", "es-MX", "ja-JP", "zh-CN" }, "en,es,ja,zh")]
    [InlineData(new[] { "yue-HK", "zh-TW" }, "cn,zh")]
    [InlineData(new[] { "not a locale" }, "")]
    public void Image_languages_come_from_the_enabled_locales(string[] enabled, string expected)
    {
        Assert.Equal(expected, TmdbClient.ImageLanguages(enabled));
    }

    [Fact]
    public async Task With_no_usable_locale_images_are_not_requested()
    {
        var handler = new FakeHandler();
        var config = new FakeConfig(new PluginConfiguration { TmdbApiKey = V4Token, EnabledLocales = [] });
        var client = new TmdbClient(new FakeHttpClientFactory(handler), config, NullLogger<TmdbClient>.Instance);
        handler.Respond(HttpStatusCode.OK, EmptyDetails);

        await client.GetMovieAsync(1, None);

        Assert.Equal("?append_to_response=translations", handler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public void Image_choice_is_stable_and_skips_textless_and_broken_entries()
    {
        var response = new TmdbImagesResponse
        {
            Posters =
            [
                new TmdbImageEntry { Language = "es", Region = "MX", FilePath = "/b.jpg", Width = 1000, Height = 1500, VoteAverage = 5 },
                new TmdbImageEntry { Language = "es", Region = "MX", FilePath = "/a.jpg", Width = 1000, Height = 1500, VoteAverage = 5 },
                new TmdbImageEntry { Language = null, Region = null, FilePath = "/textless.jpg", Width = 2000, Height = 3000, VoteAverage = 9 },
                new TmdbImageEntry { Language = "ja", Region = "JP", FilePath = "", Width = 1000, Height = 1500, VoteAverage = 9 },
            ],
        };

        var images = TmdbClient.MapImages(response);

        Assert.Equal("https://image.tmdb.org/t/p/original/a.jpg", Assert.Single(images).Url);
    }

    // ---- Mapping rules TMDB data doesn't happen to cover ----

    [Fact]
    public void Map_converts_cantonese_and_regionless_entries_and_drops_empty_ones()
    {
        var response = new TmdbTranslationsResponse
        {
            Translations =
            [
                Entry("cn", "HK", title: "千與千尋"),
                Entry("eo", "", overview: "Resumo"),
                Entry("de", "DE"),
                Entry("xx-bad", "US", title: "ignored"),
            ],
        };

        var translations = TmdbClient.Map(response);

        Assert.Equal(["yue-HK", "eo"], translations.Select(t => t.Locale));
    }

    [Fact]
    public void Map_adds_the_original_title_when_tmdb_has_no_entry_for_that_language()
    {
        var response = new TmdbTranslationsResponse { Translations = [Entry("en", "US", title: "Spirited Away")] };

        var translations = TmdbClient.Map(response, "ja", "千と千尋の神隠し");

        Assert.Equal(("ja", "千と千尋の神隠し"), (translations[^1].Locale, translations[^1].Title));
    }

    // ---- HTTP behaviour ----

    [Fact]
    public async Task Each_kind_calls_its_path()
    {
        var (client, handler, _) = Create(V4Token);
        handler.Respond(HttpStatusCode.OK, EmptyDetails);
        handler.Respond(HttpStatusCode.OK, EmptyDetails);
        handler.Respond(HttpStatusCode.OK, EmptyDetails);
        handler.Respond(HttpStatusCode.OK, EmptyTranslations);

        await client.GetMovieAsync(129, None);
        await client.GetSeriesAsync(80834, None);
        await client.GetSeasonAsync(80834, 1, None);
        await client.GetEpisodeAsync(80834, 1, 3, None);

        Assert.Equal(
            [
                "/3/movie/129?append_to_response=translations,images&include_image_language=en,es,ja,zh",
                "/3/tv/80834?append_to_response=translations,images&include_image_language=en,es,ja,zh",
                "/3/tv/80834/season/1?append_to_response=translations,images&include_image_language=en,es,ja,zh",
                "/3/tv/80834/season/1/episode/3/translations",
            ],
            handler.Requests.Select(r => r.RequestUri!.PathAndQuery));
        Assert.All(handler.Requests, r => Assert.Equal("api.themoviedb.org", r.RequestUri!.Host));
    }

    [Fact]
    public async Task A_v3_key_goes_in_the_query()
    {
        var (client, handler, _) = Create(V3Key);
        handler.Respond(HttpStatusCode.OK, EmptyDetails);
        handler.Respond(HttpStatusCode.OK, EmptyTranslations);

        await client.GetMovieAsync(1, None);
        await client.GetEpisodeAsync(1, 1, 1, None);

        Assert.Equal($"?append_to_response=translations,images&include_image_language=en,es,ja,zh&api_key={V3Key}", handler.Requests[0].RequestUri!.Query);
        Assert.Equal($"?api_key={V3Key}", handler.Requests[1].RequestUri!.Query);
        Assert.All(handler.Requests, r => Assert.Null(r.Headers.Authorization));
    }

    [Fact]
    public async Task A_v4_token_goes_in_the_bearer_header()
    {
        var (client, handler, _) = Create(V4Token);
        handler.Respond(HttpStatusCode.OK, EmptyTranslations);

        await client.GetEpisodeAsync(1, 1, 1, None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(string.Empty, request.RequestUri!.Query);
        Assert.Equal(("Bearer", V4Token), (request.Headers.Authorization!.Scheme, request.Headers.Authorization.Parameter));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task No_key_means_no_request(string key)
    {
        var (client, handler, _) = Create(key);

        await Assert.ThrowsAsync<ProviderNotConfiguredException>(() => client.GetMovieAsync(1, None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_rejected_key_is_not_retried()
    {
        var (client, handler, delays) = Create(V3Key);
        handler.Respond(HttpStatusCode.Unauthorized, """{"status_code":7}""");

        await Assert.ThrowsAsync<ProviderAuthenticationException>(() => client.GetMovieAsync(1, None));
        Assert.Single(handler.Requests);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Too_many_requests_waits_for_Retry_After()
    {
        var (client, handler, delays) = Create(V4Token);
        handler.Respond(HttpStatusCode.TooManyRequests, "{}", retryAfterSeconds: 2);
        handler.Respond(HttpStatusCode.OK, Fixture("movie-9989.json"));

        var title = await client.GetMovieAsync(9989, None);

        Assert.NotEmpty(title!.Translations);
        Assert.Equal([TimeSpan.FromSeconds(2)], delays);
    }

    [Fact]
    public async Task Server_errors_back_off_then_give_up()
    {
        var (client, handler, delays) = Create(V3Key);
        for (var i = 0; i <= TmdbClient.MaxRetries; i++)
        {
            handler.Respond(HttpStatusCode.BadGateway, "{}");
        }

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetMovieAsync(1, None));

        Assert.Equal(HttpStatusCode.BadGateway, error.StatusCode);
        Assert.Equal(TmdbClient.MaxRetries + 1, handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], delays);
        Assert.DoesNotContain(V3Key, error.Message, StringComparison.Ordinal); // never the key
    }

    [Fact]
    public async Task Network_errors_are_retried()
    {
        var (client, handler, delays) = Create(V4Token);
        handler.Fail(new HttpRequestException("connection reset"));
        handler.Respond(HttpStatusCode.OK, EmptyTranslations);

        Assert.NotNull(await client.GetEpisodeAsync(1, 1, 1, None));
        Assert.Equal([TimeSpan.FromSeconds(1)], delays);
    }

    [Fact]
    public async Task Other_client_errors_fail_at_once()
    {
        var (client, handler, delays) = Create(V4Token);
        handler.Respond(HttpStatusCode.BadRequest, "{}");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetMovieAsync(1, None));
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_retried()
    {
        var (client, handler, delays) = Create(V4Token);
        handler.Respond(HttpStatusCode.OK, "{}");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetMovieAsync(1, cts.Token));
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

    // ---- Helpers ----

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tmdb", name), Encoding.UTF8);

    private static async Task<IReadOnlyList<TmdbTranslation>> RecordedAsync(
        string fixture,
        Func<TmdbClient, Task<TmdbTitle?>> call) =>
        (await RecordedTitleAsync(fixture, call)).Translations;

    private static async Task<TmdbTitle> RecordedTitleAsync(string fixture, Func<TmdbClient, Task<TmdbTitle?>> call)
    {
        var (client, handler, _) = Create(V4Token);
        handler.Respond(HttpStatusCode.OK, Fixture(fixture));
        var title = await call(client);
        Assert.NotNull(title);
        return title;
    }

    private static TmdbImage Image(IReadOnlyList<TmdbImage> images, string locale, string type) =>
        Assert.Single(images, i => i.Locale == locale && i.ImageType == type);

    private static TmdbTranslation Find(IReadOnlyList<TmdbTranslation> translations, string locale) =>
        Assert.Single(translations, t => t.Locale == locale);

    private static TmdbTranslationEntry Entry(string language, string region, string? title = null, string? overview = null) =>
        new() { Language = language, Region = region, Data = new TmdbTranslationData { Title = title, Overview = overview } };

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
