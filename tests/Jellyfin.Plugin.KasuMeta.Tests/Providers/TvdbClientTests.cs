using System.Net;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Providers;
using Jellyfin.Plugin.KasuMeta.Providers.Tvdb;
using Jellyfin.Plugin.KasuMeta.Tests.Storage;
using Jellyfin.Plugin.KasuMeta.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.KasuMeta.Tests.Providers;

public class TvdbClientTests
{
    private const string Key = "00000000-1111-2222-3333-444444444444";
    private const string Login = """{"status":"success","data":{"token":"token-1"}}""";
    private const string Login2 = """{"status":"success","data":{"token":"token-2"}}""";
    private const string Empty = """{"status":"success","data":{"id":1}}""";
    private static readonly CancellationToken None = CancellationToken.None;

    // ---- Recorded responses (Fixtures/README.md) ----

    [Fact]
    public async Task Cabin_in_the_woods_has_one_entry_per_language()
    {
        var title = await RecordedAsync("movie-963.json", c => c.GetMovieAsync(963, None));

        Assert.Equal(("La cabaña en el bosque", (string?)null), (Find(title, "es").Title, Find(title, "es").Tagline));
        Assert.StartsWith("Cinco estudiantes", Find(title, "es").Overview, StringComparison.Ordinal);
        Assert.Equal("キャビン", Find(title, "ja").Title);
        Assert.Equal("林中小屋", Find(title, "zh-CN").Title); // TVDB's zho is "Chinese - China"
        Assert.Equal("O Segredo da Cabana", Find(title, "pt-BR").Title); // TVDB's pt
        Assert.Equal("O Segredo da Cabana", Find(title, "pt-PT").Title); // TVDB's por
        Assert.Equal("You think you know the story.", Find(title, "en").Tagline); // movies have taglines
        Assert.DoesNotContain(title.Translations, t => t.Locale.Length == 3); // every id maps to BCP-47
    }

    [Fact]
    public async Task Cabin_in_the_woods_keeps_its_best_poster_per_language_and_drops_textless_art()
    {
        var title = await RecordedAsync("movie-963.json", c => c.GetMovieAsync(963, None));

        Assert.Equal(["en Primary", "pl Primary"], title.Images.Select(i => $"{i.Locale} {i.ImageType}"));
        var poster = title.Images[0];
        Assert.StartsWith("https://artworks.thetvdb.com/banners/", poster.Url, StringComparison.Ordinal);
        Assert.Equal((680, 1000), (poster.Width, poster.Height));
    }

    [Fact]
    public async Task Magia_record_skips_aliases_and_maps_its_artwork_types()
    {
        var title = await RecordedAsync("series-370931.json", c => c.GetSeriesAsync(370931, None));

        // The primary jpn entry, not one of the four Japanese aliases.
        Assert.Equal("マギアレコード 魔法少女まどか☆マギカ外伝", Find(title, "ja").Title);
        Assert.Equal("Magia Record: Mahou Shoujo Madoka☆Magica Gaiden", Find(title, "es").Title);
        Assert.Equal("Magia Record: Puella Magi Madoka Magica Side Story", Find(title, "en").Title);
        Assert.NotNull(Find(title, "en").Overview);
        Assert.Contains(title.Images, i => i is { Locale: "ja", ImageType: "Primary" });
        Assert.Contains(title.Images, i => i is { Locale: "ja", ImageType: "Logo" }); // clearlogo, type 23
        Assert.All(title.Images, i => Assert.DoesNotContain("/seasons/", i.Url, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_season_has_overviews_but_no_name_of_its_own()
    {
        var title = await RecordedAsync("season-1805557.json", c => c.GetSeasonAsync(1805557, None));

        // The only Chinese name is an alias of the series': left out.
        Assert.All(title.Translations, t => Assert.Null(t.Title));
        Assert.StartsWith("A dream of an unknown girl", Find(title, "en").Overview, StringComparison.Ordinal);
        Assert.Equal(("es", "Primary"), (Assert.Single(title.Images).Locale, title.Images[0].ImageType));
    }

    [Fact]
    public async Task An_episode_has_names_in_each_language_and_no_images()
    {
        var title = await RecordedAsync("episode-7489229.json", c => c.GetEpisodeAsync(7489229, None));

        Assert.Equal("Have You Heard? That Rumor About the Magical Girls", Find(title, "en").Title);
        Assert.Equal("やぁやぁ、知ってる？魔法少女のその噂", Find(title, "ja").Title);
        Assert.Equal("¿Lo has escuchado?  Ese rumor sobre las Chicas Mágicas", Find(title, "es").Title);
        Assert.Empty(title.Images);
    }

    [Fact]
    public async Task A_season_id_is_found_in_the_series_default_order()
    {
        var (client, handler, _) = Create();
        handler.Respond(HttpStatusCode.OK, Login);
        handler.Respond(HttpStatusCode.OK, Fixture("series-370931.json"));

        // Season 1 exists as "absolute" (1748064) and "official" (1805557); aired is the default.
        Assert.Equal(1805557, await client.FindSeasonIdAsync(370931, 1, None));
        Assert.Equal("/v4/series/370931/extended?short=true", handler.Requests[1].RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task An_episode_id_is_found_by_its_numbers()
    {
        var (client, handler, _) = Create();
        handler.Respond(HttpStatusCode.OK, Login);
        handler.Respond(HttpStatusCode.OK, Fixture("series-346988-episodes-s1e5.json"));

        Assert.Equal(6759431, await client.FindEpisodeIdAsync(346988, 1, 5, None));
        Assert.Equal("/v4/series/346988/episodes/default?page=0&season=1&episodeNumber=5", handler.Requests[1].RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task An_imdb_id_finds_the_movie_but_not_a_series()
    {
        var (client, handler, _) = Create();
        handler.Respond(HttpStatusCode.OK, Login);
        handler.Respond(HttpStatusCode.OK, Fixture("remoteid-tt1259521.json"));
        handler.Respond(HttpStatusCode.OK, Fixture("remoteid-tt1259521.json"));

        Assert.Equal(963, await client.FindByImdbIdAsync("tt1259521", series: false, None));
        Assert.Null(await client.FindByImdbIdAsync("tt1259521", series: true, None));
        Assert.Equal("/v4/search/remoteid/tt1259521", handler.Requests[1].RequestUri!.PathAndQuery);
    }

    [Theory]
    [InlineData("1259521")]
    [InlineData("tt12/../x")]
    [InlineData("")]
    public async Task Something_that_isnt_an_imdb_id_is_never_sent(string id)
    {
        var (client, handler, _) = Create();

        Assert.Null(await client.FindByImdbIdAsync(id, series: false, None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Not_found_is_null()
    {
        var (client, handler, _) = Create();
        handler.Respond(HttpStatusCode.OK, Login);
        handler.Respond(HttpStatusCode.NotFound, Fixture("not-found.json"));

        Assert.Null(await client.GetSeriesAsync(99999999, None));
    }

    // ---- Mapping rules the recorded data doesn't cover ----

    [Fact]
    public void The_record_name_fills_a_missing_original_language_title()
    {
        var record = Record("""{"nameTranslations":[{"language":"eng","name":"Spirited Away"}],"overviewTranslations":[]}""");
        record.OriginalLanguage = "jpn";
        record.Name = "千と千尋の神隠し";

        var translations = TvdbClient.MapTranslations(record, fillOriginal: true);

        Assert.Equal(["en Spirited Away", "ja 千と千尋の神隠し"], translations.Select(t => $"{t.Locale} {t.Title}"));
        Assert.Single(TvdbClient.MapTranslations(record, fillOriginal: false));
    }

    [Fact]
    public void Translations_in_an_unexpected_shape_are_ignored_rather_than_failing()
    {
        var record = Record("""[{"language":"eng","name":"x"}]""");

        Assert.Empty(TvdbClient.MapTranslations(record, fillOriginal: false));
    }

    [Fact]
    public void Artwork_choice_prefers_raster_then_score_and_is_stable()
    {
        var types = new Dictionary<int, string> { [2] = "Primary" };
        TvdbArtwork Art(string image, double score, string? language = "eng", int type = 2) =>
            new() { Image = image, Score = score, Language = language, Type = type, Width = 680, Height = 1000 };

        var images = TvdbClient.MapImages(
            [
                Art("https://a/logo.svg", 100),
                Art("https://a/b.jpg", 10),
                Art("https://a/a.jpg", 10),
                Art("https://a/textless.jpg", 999, language: null),
                Art("https://a/banner.jpg", 999, type: 1),
            ],
            types);

        Assert.Equal("https://a/a.jpg", Assert.Single(images).Url);
    }

    [Theory]
    [InlineData("eng", "en")]
    [InlineData("SPA", "es")]
    [InlineData("pt", "pt-BR")]
    [InlineData("por", "pt-PT")]
    [InlineData("zho", "zh-CN")]
    [InlineData("zhtw", "zh-TW")]
    [InlineData("yue", "yue")]
    [InlineData("xyz", null)]
    [InlineData(null, null)]
    public void Language_ids_become_bcp47(string? language, string? expected)
    {
        Assert.Equal(expected, TvdbClient.LocaleFromTvdb(language));
    }

    // ---- Login and HTTP behaviour ----

    [Fact]
    public async Task It_logs_in_once_and_reuses_the_token()
    {
        var (client, handler, _) = Create();
        handler.Respond(HttpStatusCode.OK, Login);
        handler.Respond(HttpStatusCode.OK, Empty);
        handler.Respond(HttpStatusCode.OK, Empty);

        await client.GetMovieAsync(1, None);
        await client.GetEpisodeAsync(2, None);

        Assert.Equal(
            ["POST /v4/login", "GET /v4/movies/1/extended?meta=translations", "GET /v4/episodes/2/extended?meta=translations"],
            handler.Requests.Select(r => $"{r.Method} {r.RequestUri!.PathAndQuery}"));
        Assert.Equal(Key, JsonDocument.Parse(handler.Bodies[0]).RootElement.GetProperty("apikey").GetString());
        Assert.False(JsonDocument.Parse(handler.Bodies[0]).RootElement.TryGetProperty("pin", out _));
        Assert.All(handler.Requests.Skip(1), r => Assert.Equal(("Bearer", "token-1"), (r.Headers.Authorization!.Scheme, r.Headers.Authorization.Parameter)));
        Assert.All(handler.Requests, r => Assert.Equal("api4.thetvdb.com", r.RequestUri!.Host));
    }

    [Fact]
    public async Task The_pin_is_sent_when_set()
    {
        var (client, handler, _) = Create(pin: "1234");
        handler.Respond(HttpStatusCode.OK, Login);
        handler.Respond(HttpStatusCode.OK, Empty);

        await client.GetMovieAsync(1, None);

        Assert.Equal("1234", JsonDocument.Parse(handler.Bodies[0]).RootElement.GetProperty("pin").GetString());
    }

    [Fact]
    public async Task A_rejected_token_logs_in_again_once()
    {
        var (client, handler, _) = Create();
        handler.Respond(HttpStatusCode.OK, Login);
        handler.Respond(HttpStatusCode.Unauthorized, "{}");
        handler.Respond(HttpStatusCode.OK, Login2);
        handler.Respond(HttpStatusCode.OK, Empty);

        Assert.NotNull(await client.GetMovieAsync(1, None));
        Assert.Equal("token-2", handler.Requests[3].Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task A_token_rejected_twice_is_an_authentication_failure()
    {
        var (client, handler, _) = Create();
        handler.Respond(HttpStatusCode.OK, Login);
        handler.Respond(HttpStatusCode.Unauthorized, "{}");
        handler.Respond(HttpStatusCode.OK, Login2);
        handler.Respond(HttpStatusCode.Unauthorized, "{}");

        await Assert.ThrowsAsync<ProviderAuthenticationException>(() => client.GetMovieAsync(1, None));
    }

    [Fact]
    public async Task An_old_token_is_renewed()
    {
        var (client, handler, time) = CreateTimed();
        handler.Respond(HttpStatusCode.OK, Login);
        handler.Respond(HttpStatusCode.OK, Empty);
        handler.Respond(HttpStatusCode.OK, Login2);
        handler.Respond(HttpStatusCode.OK, Empty);

        await client.GetMovieAsync(1, None);
        time.Now += TvdbClient.TokenLifetime + TimeSpan.FromMinutes(1);
        await client.GetMovieAsync(1, None);

        Assert.Equal("POST", handler.Requests[2].Method.Method);
        Assert.Equal("token-2", handler.Requests[3].Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task A_new_key_logs_in_again()
    {
        var config = new PluginConfiguration { TvdbApiKey = Key };
        var (client, handler, _) = Create(config: config);
        handler.Respond(HttpStatusCode.OK, Login);
        handler.Respond(HttpStatusCode.OK, Empty);
        handler.Respond(HttpStatusCode.OK, Login2);
        handler.Respond(HttpStatusCode.OK, Empty);

        await client.GetMovieAsync(1, None);
        config.TvdbApiKey = "another-key";
        await client.GetMovieAsync(1, None);

        Assert.Equal("another-key", JsonDocument.Parse(handler.Bodies[2]).RootElement.GetProperty("apikey").GetString());
    }

    [Fact]
    public async Task A_rejected_key_is_not_retried_and_never_logged()
    {
        var (client, handler, delays) = Create();
        handler.Respond(HttpStatusCode.Unauthorized, """{"status":"failure","message":"Unauthorized"}""");

        var error = await Assert.ThrowsAsync<ProviderAuthenticationException>(() => client.GetMovieAsync(1, None));

        Assert.Single(handler.Requests);
        Assert.Empty(delays);
        Assert.Contains("PIN", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task No_key_means_no_request(string key)
    {
        var (client, handler, _) = Create(config: new PluginConfiguration { TvdbApiKey = key });

        await Assert.ThrowsAsync<ProviderNotConfiguredException>(() => client.GetSeriesAsync(1, None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Server_errors_back_off_then_give_up_and_are_counted()
    {
        var stats = new ProviderStats(TimeProvider.System);
        var (client, handler, delays) = Create(stats: stats);
        handler.Respond(HttpStatusCode.OK, Login);
        for (var i = 0; i <= TvdbClient.MaxRetries; i++)
        {
            handler.Respond(HttpStatusCode.ServiceUnavailable, "{}");
        }

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetSeriesAsync(1, None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], delays);
        var counters = stats.Snapshot()["tvdb"];
        Assert.Equal((5L, 3L, 1L), (counters.Requests, counters.Retries, counters.Failures));
    }

    [Fact]
    public async Task Too_many_requests_waits_for_Retry_After()
    {
        var (client, handler, delays) = Create();
        handler.Respond(HttpStatusCode.OK, Login);
        handler.Respond(HttpStatusCode.TooManyRequests, "{}", retryAfterSeconds: 3);
        handler.Respond(HttpStatusCode.OK, Empty);

        Assert.NotNull(await client.GetMovieAsync(1, None));
        Assert.Equal([TimeSpan.FromSeconds(3)], delays);
    }

    // ---- Helpers ----

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tvdb", name), Encoding.UTF8);

    private static TvdbRecord Record(string translationsJson) =>
        new() { Translations = JsonDocument.Parse(translationsJson).RootElement.Clone() };

    private static TvdbTranslation Find(TvdbTitle title, string locale) =>
        Assert.Single(title.Translations, t => t.Locale == locale);

    private static async Task<TvdbTitle> RecordedAsync(string fixture, Func<TvdbClient, Task<TvdbTitle?>> call)
    {
        var (client, handler, _) = Create();
        handler.Respond(HttpStatusCode.OK, Login);
        handler.Respond(HttpStatusCode.OK, Fixture(fixture));
        var title = await call(client);
        Assert.NotNull(title);
        return title;
    }

    private static (TvdbClient Client, FakeHandler Handler, List<TimeSpan> Delays) Create(
        PluginConfiguration? config = null,
        string pin = "",
        ProviderStats? stats = null)
    {
        var (client, handler, delays, _) = CreateWithTime(config ?? new PluginConfiguration { TvdbApiKey = Key, TvdbPin = pin }, stats);
        return (client, handler, delays);
    }

    private static (TvdbClient Client, FakeHandler Handler, ManualTimeProvider Time) CreateTimed()
    {
        var (client, handler, _, time) = CreateWithTime(new PluginConfiguration { TvdbApiKey = Key }, null);
        return (client, handler, time);
    }

    private static (TvdbClient Client, FakeHandler Handler, List<TimeSpan> Delays, ManualTimeProvider Time) CreateWithTime(
        PluginConfiguration config,
        ProviderStats? stats)
    {
        var handler = new FakeHandler();
        var delays = new List<TimeSpan>();
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero));
        var client = new TvdbClient(
            new FakeHttpClientFactory(handler),
            new FakeConfig(config),
            stats ?? new ProviderStats(TimeProvider.System),
            time,
            NullLogger<TvdbClient>.Instance)
        {
            Delay = (wait, _) =>
            {
                delays.Add(wait);
                return Task.CompletedTask;
            },
        };
        return (client, handler, delays, time);
    }
}
