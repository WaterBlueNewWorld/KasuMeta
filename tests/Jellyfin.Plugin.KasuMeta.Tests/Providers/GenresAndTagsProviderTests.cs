using System.Net;
using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Providers;
using Jellyfin.Plugin.KasuMeta.Providers.AniList;
using Jellyfin.Plugin.KasuMeta.Providers.Tmdb;
using Jellyfin.Plugin.KasuMeta.Providers.Tvdb;
using Jellyfin.Plugin.KasuMeta.Tests.Storage;
using Jellyfin.Plugin.KasuMeta.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.KasuMeta.Tests.Providers;

/// <summary>Genres, tags (keywords) and the original language, as each provider sends them.</summary>
public class GenresAndTagsProviderTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Tmdb_movie_details_carry_genres_keywords_and_the_original_language()
    {
        var (client, handler) = Tmdb();
        handler.Respond(HttpStatusCode.OK, """
            {"original_language":"ja","original_title":"君の名は。",
             "genres":[{"id":16,"name":"Animation"},{"id":18,"name":"Drama"},{"id":0,"name":"bad"}],
             "keywords":{"keywords":[{"id":1,"name":"body swap"},{"id":2,"name":"Body Swap"},{"id":3,"name":"comet"}]},
             "translations":{"translations":[]}}
            """);

        var title = (await client.GetMovieAsync(372058, None, ["ja"]))!;

        Assert.Equal("ja", title.OriginalLanguage);
        Assert.Equal([new TmdbGenre(16, "Animation"), new TmdbGenre(18, "Drama")], title.Genres);
        Assert.Equal(["body swap", "comet"], title.Keywords);
        Assert.Contains("include_image_language=en,ja", handler.Requests[0].RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tmdb_series_keywords_come_under_results()
    {
        var (client, handler) = Tmdb();
        handler.Respond(HttpStatusCode.OK, """
            {"original_language":"en","keywords":{"results":[{"id":9,"name":"dystopia"}]},"translations":{"translations":[]}}
            """);

        Assert.Equal(["dystopia"], (await client.GetSeriesAsync(1, None))!.Keywords);
    }

    [Fact]
    public async Task Genre_lists_are_asked_once_a_day_per_kind_and_locale()
    {
        var (client, handler) = Tmdb();
        var now = new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);
        client.Now = () => now;
        const string Names = """{"genres":[{"id":16,"name":"Animación"},{"id":18,"name":" "}]}""";
        handler.Respond(HttpStatusCode.OK, Names);
        handler.Respond(HttpStatusCode.OK, Names);

        var first = await client.GetGenreNamesAsync(tv: true, "es-MX", None);
        await client.GetGenreNamesAsync(tv: true, "es-MX", None);
        now += TmdbClient.GenreListLifetime;
        await client.GetGenreNamesAsync(tv: true, "es-MX", None);

        Assert.Equal(new Dictionary<int, string> { [16] = "Animación" }, first);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/3/genre/tv/list", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("?language=es-MX", handler.Requests[0].RequestUri!.Query);
    }

    [Theory]
    [InlineData("es-MX", "es-MX")]
    [InlineData("ja", "ja")]
    [InlineData("yue-HK", "cn-HK")]
    [InlineData("yue", "cn")]
    public void Tmdb_spells_cantonese_its_own_way(string locale, string expected) =>
        Assert.Equal(expected, TmdbClient.TmdbLanguage(locale));

    [Fact]
    public async Task Tvdb_series_carry_english_genres_tags_and_the_original_language()
    {
        var handler = new FakeHandler();
        var client = new TvdbClient(
            new FakeHttpClientFactory(handler),
            new FakeConfig(new PluginConfiguration { TvdbApiKey = "key" }),
            new ProviderStats(TimeProvider.System),
            new ManualTimeProvider(DateTimeOffset.UnixEpoch),
            NullLogger<TvdbClient>.Instance);
        handler.Respond(HttpStatusCode.OK, """{"status":"success","data":{"token":"t"}}""");
        handler.Respond(HttpStatusCode.OK, """
            {"status":"success","data":{"id":424536,"name":"葬送のフリーレン","originalLanguage":"jpn",
             "genres":[{"id":27,"name":"Anime","slug":"anime"},{"id":2,"name":"Fantasy"}],
             "tags":[{"id":1,"tag":7,"tagName":"Setting","name":"Medieval"},{"id":2,"name":" "}],
             "translations":{"nameTranslations":[],"overviewTranslations":[]}}}
            """);

        var title = (await client.GetSeriesAsync(424536, None))!;

        Assert.Equal("ja", title.OriginalLanguage);
        Assert.Equal(["Anime", "Fantasy"], title.Genres);
        Assert.Equal(["Medieval"], title.Tags);
    }

    [Fact]
    public void Anilist_genres_and_tags_go_on_the_english_entry_without_spoilers()
    {
        var media = new AniListMediaEntry
        {
            CountryOfOrigin = "JP",
            Title = new AniListTitleEntry { Romaji = "Sousou no Frieren", Native = "葬送のフリーレン" },
            Genres = ["Adventure", "Drama", null],
            Tags =
            [
                new AniListTagEntry { Name = "Elf" },
                new AniListTagEntry { Name = "Death of a Main Character", IsMediaSpoiler = true },
                new AniListTagEntry { Name = "Twist", IsGeneralSpoiler = true },
            ],
        };

        var english = Assert.Single(AniListClient.Map(media), t => t.Locale == "en-US");

        Assert.Null(english.Title); // no English title: the entry is there for its terms
        Assert.Equal(["Adventure", "Drama"], english.Genres);
        Assert.Equal(["Elf"], english.Tags);
    }

    [Fact]
    public void The_anilist_query_asks_for_genres_and_tags() =>
        Assert.Contains("genres tags { name isMediaSpoiler isGeneralSpoiler }", AniListClient.Query, StringComparison.Ordinal);

    private static (TmdbClient Client, FakeHandler Handler) Tmdb()
    {
        var handler = new FakeHandler();
        var client = new TmdbClient(
            new FakeHttpClientFactory(handler),
            new FakeConfig(new PluginConfiguration { TmdbApiKey = "eyJhbGciOiJIUzI1NiJ9.e30.sig", EnabledLocales = ["en-US"] }),
            new ProviderStats(TimeProvider.System),
            NullLogger<TmdbClient>.Instance);
        return (client, handler);
    }
}
