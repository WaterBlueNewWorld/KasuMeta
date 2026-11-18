using System.Net;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.KasuMeta.Providers.AniList;
using Jellyfin.Plugin.KasuMeta.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.KasuMeta.Tests.Providers;

public class AniListClientTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    // ---- Recorded responses (Fixtures/README.md) ----

    [Fact]
    public async Task Magia_record_gets_english_native_and_romaji_titles()
    {
        var translations = await RecordedAsync("media-104051.json", 104051);

        Assert.Equal(["en-US", "ja-JP", "ja-Latn"], translations.Select(t => t.Locale));
        Assert.Equal("Magia Record: Puella Magi Madoka Magica Side Story", Find(translations, "en-US").Title);
        Assert.Equal("マギアレコード 魔法少女まどか☆マギカ外伝", Find(translations, "ja-JP").Title);
        Assert.Equal("Magia Record: Mahou Shoujo Madoka☆Magica Gaiden", Find(translations, "ja-Latn").Title);
        Assert.Null(Find(translations, "ja-JP").Overview); // AniList's synopses are English
    }

    [Fact]
    public async Task The_synopsis_loses_its_markup_and_closing_credit()
    {
        var magia = Find(await RecordedAsync("media-104051.json", 104051), "en-US").Overview!;
        var revue = Find(await RecordedAsync("media-98658.json", 98658), "en-US").Overview!;

        Assert.StartsWith("In exchange for the fulfillment of a wish", magia, StringComparison.Ordinal);
        Assert.EndsWith("in search for her lost wish, begins…", magia, StringComparison.Ordinal);
        Assert.EndsWith("gain acceptance into the revue.", revue, StringComparison.Ordinal);
        Assert.All(new[] { magia, revue }, d =>
        {
            Assert.DoesNotContain("<", d, StringComparison.Ordinal);
            Assert.DoesNotContain("Source:", d, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Unknown_media_is_null()
    {
        var (client, handler, _) = Create();
        handler.Respond(HttpStatusCode.NotFound, Fixture("media-404.json"));

        Assert.Null(await client.GetMediaAsync(999999999, None));
    }

    // ---- Request shape ----

    [Fact]
    public async Task Sends_the_recorded_query_with_the_id_and_a_user_agent()
    {
        var (client, handler, _) = Create();
        handler.Respond(HttpStatusCode.OK, Fixture("media-104051.json"));

        await client.GetMediaAsync(104051, None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Post, AniListClient.Endpoint), (request.Method, request.RequestUri));
        // Cloudflare in front of AniList refuses clients without a recognisable User-Agent.
        Assert.StartsWith("KasuMeta/1.2.3.0", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal(AniListClient.Query, body.RootElement.GetProperty("query").GetString());
        Assert.Equal(104051, body.RootElement.GetProperty("variables").GetProperty("id").GetInt32());
    }

    // ---- Retries ----

    [Fact]
    public async Task A_429_is_retried_after_retry_after()
    {
        var (client, handler, delays) = Create();
        handler.Respond(HttpStatusCode.TooManyRequests, """{"errors":[{"message":"Too Many Requests.","status":429}]}""", retryAfterSeconds: 42);
        handler.Respond(HttpStatusCode.OK, Fixture("media-98658.json"));

        Assert.NotNull(await client.GetMediaAsync(98658, None));

        Assert.Equal([TimeSpan.FromSeconds(42)], delays);
    }

    [Fact]
    public async Task Retry_after_is_capped_at_a_minute()
    {
        var (client, handler, delays) = Create();
        handler.Respond(HttpStatusCode.TooManyRequests, "{}", retryAfterSeconds: 3600);
        handler.Respond(HttpStatusCode.OK, Fixture("media-98658.json"));

        await client.GetMediaAsync(98658, None);

        Assert.Equal([TimeSpan.FromSeconds(60)], delays);
    }

    [Fact]
    public async Task Server_and_network_errors_are_retried_then_reported()
    {
        var (client, handler, delays) = Create();
        handler.Fail(new HttpRequestException("connection reset"));
        for (var i = 0; i < AniListClient.MaxRetries; i++)
        {
            handler.Respond(HttpStatusCode.ServiceUnavailable, "{}");
        }

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetMediaAsync(98658, None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.Equal(AniListClient.MaxRetries + 1, handler.Requests.Count);
        Assert.Equal(AniListClient.MaxRetries, delays.Count);
    }

    [Fact]
    public async Task A_bad_request_is_not_retried()
    {
        var (client, handler, delays) = Create();
        handler.Respond(HttpStatusCode.BadRequest, """{"errors":[{"message":"Syntax Error","status":400}]}""");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetMediaAsync(98658, None));

        Assert.Single(handler.Requests);
        Assert.Empty(delays);
    }

    // ---- Mapping (synthetic: cases the recorded titles don't hit) ----

    [Theory]
    [InlineData("JP", "ja-JP", "ja-Latn")]
    [InlineData("CN", "zh-CN", "zh-Latn")]
    [InlineData("TW", "zh-TW", "zh-Latn")]
    [InlineData("kr", "ko-KR", "ko-Latn")]
    public void Native_and_romanized_titles_follow_the_country_of_origin(string country, string native, string romanized)
    {
        Assert.Equal((native, romanized), AniListClient.LocalesFor(country));
    }

    [Fact]
    public void An_unknown_country_keeps_only_the_english_text()
    {
        var mapped = AniListClient.Map(new AniListMediaEntry
        {
            CountryOfOrigin = null,
            Title = new AniListTitleEntry { English = "Title", Native = "Titre", Romaji = "Title" },
        });

        Assert.Equal("en-US", Assert.Single(mapped).Locale);
    }

    [Fact]
    public void Empty_titles_and_descriptions_are_left_out()
    {
        var mapped = AniListClient.Map(new AniListMediaEntry
        {
            CountryOfOrigin = "JP",
            Title = new AniListTitleEntry { English = null, Native = "  ", Romaji = "Hyouka" },
            Description = "<br>",
        });

        Assert.Equal(("ja-Latn", "Hyouka"), (Assert.Single(mapped).Locale, mapped[0].Title));
    }

    [Theory]
    [InlineData("A &amp; B<br>C", "A & B\nC")]
    [InlineData("<i>Italic</i> text.<br><br>\n<br>Next.", "Italic text.\n\nNext.")]
    [InlineData("Story.\n\n[Written by MAL Rewrite]", "Story.")]
    [InlineData("Story. (Source: Crunchyroll)", "Story.")]
    [InlineData("He said (Source: inside a sentence) and left.", "He said (Source: inside a sentence) and left.")]
    [InlineData("<br><br>", null)]
    public void Descriptions_become_plain_text(string raw, string? expected)
    {
        Assert.Equal(expected, AniListClient.CleanDescription(raw));
    }

    // ---- Helpers ----

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "anilist", name), Encoding.UTF8);

    private static async Task<IReadOnlyList<AniListTranslation>> RecordedAsync(string fixture, int id)
    {
        var (client, handler, _) = Create();
        handler.Respond(HttpStatusCode.OK, Fixture(fixture));
        var translations = await client.GetMediaAsync(id, None);
        Assert.NotNull(translations);
        return translations;
    }

    private static AniListTranslation Find(IReadOnlyList<AniListTranslation> translations, string locale) =>
        Assert.Single(translations, t => t.Locale == locale);

    private static (AniListClient Client, FakeHandler Handler, List<TimeSpan> Delays) Create()
    {
        var handler = new FakeHandler();
        var delays = new List<TimeSpan>();
        var client = new AniListClient(new FakeHttpClientFactory(handler), new FakeConfig(), NullLogger<AniListClient>.Instance)
        {
            Delay = (wait, _) =>
            {
                delays.Add(wait);
                return Task.CompletedTask;
            },
        };
        return (client, handler, delays);
    }
}
