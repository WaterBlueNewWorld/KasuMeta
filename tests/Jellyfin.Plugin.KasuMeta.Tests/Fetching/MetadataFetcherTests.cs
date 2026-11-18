using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Fetching;
using Jellyfin.Plugin.KasuMeta.Library;
using Jellyfin.Plugin.KasuMeta.Providers;
using Jellyfin.Plugin.KasuMeta.Providers.AniList;
using Jellyfin.Plugin.KasuMeta.Providers.Tmdb;
using Jellyfin.Plugin.KasuMeta.Providers.Tvdb;
using Jellyfin.Plugin.KasuMeta.Search;
using Jellyfin.Plugin.KasuMeta.Storage;
using Jellyfin.Plugin.KasuMeta.Tests.Storage;
using Jellyfin.Plugin.KasuMeta.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.KasuMeta.Tests.Fetching;

public sealed class MetadataFetcherTests : IDisposable
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Guid Antitrust = Guid.Parse("11111111111111111111111111111111");

    private static readonly IReadOnlyList<TmdbTranslation> AntitrustTranslations =
    [
        new("en-US", "Antitrust", "Milo Hoffman…", "Truth can be dangerous"),
        new("es-MX", "Amenaza Virtual", "Milo Hoffman…", null),
        new("es-ES", "Conspiración en la red: Hackers 3", "Milo…", null),
        new("ja-JP", "サベイランス", null, null),
        new("de-DE", "Startup", "Milo…", null),
        new("zh-TW", "奪命連線", "…", null),
    ];

    private static readonly IReadOnlyList<TmdbImage> AntitrustImages =
    [
        new("es-ES", "Primary", "https://image.tmdb.org/t/p/original/es.jpg", 1920, 2560),
        new("es-ES", "Logo", "https://image.tmdb.org/t/p/original/es-logo.png", 558, 92),
        new("en-US", "Primary", "https://image.tmdb.org/t/p/original/en.jpg", 1000, 1500),
        new("zh-TW", "Primary", "https://image.tmdb.org/t/p/original/tw.jpg", 1000, 1500),
    ];

    private static readonly TmdbTitle Antitrust_ = new(AntitrustTranslations, AntitrustImages);

    private readonly TestDatabase _db = new();
    private readonly FakeLibrary _library = new();
    // Magia Record as AniList (104051) and TMDB (94523) describe it, trimmed.
    private static readonly Guid Magia = Guid.Parse("22222222222222222222222222222222");

    private static readonly IReadOnlyList<AniListTranslation> MagiaAniList =
    [
        new("en-US", "Magia Record: Puella Magi Madoka Magica Side Story", "In exchange for the fulfillment of a wish…"),
        new("ja-JP", "マギアレコード 魔法少女まどか☆マギカ外伝", null),
        new("ja-Latn", "Magia Record: Mahou Shoujo Madoka☆Magica Gaiden", null),
    ];

    private static readonly TmdbTitle MagiaTmdb = new(
        [
            new("en-US", "Magia Record: Puella Magi Madoka Magica Side Story", null, null), // no English overview
            new("ja-JP", "マギアレコード 魔法少女まどか☆マギカ外伝", "それは、少女たちも知らない、願いの物語。", null),
        ],
        []);

    private readonly FakeTmdb _tmdb = new();
    private readonly FakeTvdb _tvdb = new();
    private readonly FakeAniList _aniList = new();
    private readonly FakeConfig _config = new(new PluginConfiguration { EnabledLocales = ["en-US", "es-MX", "ja-JP"] });
    private readonly MetadataFetcher _fetcher;

    public MetadataFetcherTests()
    {
        _fetcher = new MetadataFetcher(_library, _db.Store, _tmdb, _tvdb, _aniList, _config, _db.Time, NullLogger<MetadataFetcher>.Instance);
        _tmdb.Answer = _ => Antitrust_;
        _aniList.Answer = _ => MagiaAniList;
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_movie_is_fetched_and_only_enabled_language_groups_are_kept()
    {
        _library.Add(FakeLibrary.Movie(Antitrust, "9989"));

        var result = await _fetcher.FetchAsync(Antitrust, force: false, None);

        Assert.Equal(FetchResult.Fetched, result);
        Assert.Equal(["movie/9989"], _tmdb.Calls);
        var rows = await _db.Store.GetFieldsAsync([Antitrust], null, None);
        // es-ES stays (same group as es-MX, a fallback for it); de-DE and zh-TW go.
        Assert.Equal(["en-US", "es-ES", "es-MX", "ja-JP"], rows.Select(r => r.Locale).Distinct().Order());
        Assert.Contains(rows, r => r is { Locale: "en-US", Field: MetadataFields.Tagline, Value: "Truth can be dangerous" });
        Assert.DoesNotContain(rows, r => r is { Locale: "ja-JP", Field: MetadataFields.Overview });
        var state = await _db.Store.GetFetchStateAsync(Antitrust, None);
        Assert.Equal((FetchStatus.Ok, "9989", MetadataFetcher.FetchVersion), (state!.Status, state.ProviderIds[ProviderNames.Tmdb], state.FetchVersion));
        Assert.Equal(ItemKind.Movie, state.Kind);
    }

    [Fact]
    public async Task Images_are_stored_with_their_size_for_the_enabled_language_groups()
    {
        _library.Add(FakeLibrary.Movie(Antitrust, "9989"));

        await _fetcher.FetchAsync(Antitrust, force: false, None);

        var images = await _db.Store.GetImagesAsync([Antitrust], null, None);
        Assert.Equal(["en-US/Primary", "es-ES/Logo", "es-ES/Primary"], images.Select(i => $"{i.Locale}/{i.ImageType}").Order(StringComparer.Ordinal));
        var poster = Assert.Single(images, i => i is { Locale: "es-ES", ImageType: "Primary" });
        Assert.Equal((1920, 2560, MetadataSources.Tmdb), (poster.Width!.Value, poster.Height!.Value, poster.Source));
    }

    [Fact]
    public async Task A_state_from_an_older_fetch_version_is_fetched_again()
    {
        _library.Add(FakeLibrary.Movie(Antitrust, "9989"));
        var snapshot = new Dictionary<string, string> { [ProviderNames.Tmdb] = "9989" };
        await _db.Store.SetFetchStateAsync(new FetchState(Antitrust, snapshot, _db.Time.Now, FetchStatus.Ok, FetchVersion: 1), None);

        Assert.Equal(FetchResult.Fetched, await _fetcher.FetchAsync(Antitrust, force: false, None));
        Assert.NotEmpty(await _db.Store.GetImagesAsync([Antitrust], null, None));
    }

    [Fact]
    public async Task An_unchanged_item_is_not_fetched_again_unless_forced()
    {
        _library.Add(FakeLibrary.Movie(Antitrust, "9989"));
        await _fetcher.FetchAsync(Antitrust, force: false, None);

        Assert.Equal(FetchResult.Unchanged, await _fetcher.FetchAsync(Antitrust, force: false, None));
        Assert.Equal(FetchResult.Fetched, await _fetcher.FetchAsync(Antitrust, force: true, None));
        Assert.Equal(2, _tmdb.Calls.Count);
    }

    [Fact]
    public async Task A_new_identification_replaces_the_old_titles_but_keeps_locked_ones()
    {
        _library.Add(FakeLibrary.Movie(Antitrust, "9989"));
        await _fetcher.FetchAsync(Antitrust, force: false, None);
        await _db.Store.UpsertFieldsAsync(
            [new(Antitrust, "es-MX", MetadataFields.Overview, "Curada a mano", MetadataSources.Manual, Locked: true)],
            None);

        // Identify picks another film: new TMDB id, which has only an English title.
        _library.Add(FakeLibrary.Movie(Antitrust, "1234"));
        _tmdb.Answer = _ => new TmdbTitle([new TmdbTranslation("en-US", "Another Film", null, null)], []);
        var result = await _fetcher.FetchAsync(Antitrust, force: false, None);

        Assert.Equal(FetchResult.Fetched, result);
        var rows = await _db.Store.GetFieldsAsync([Antitrust], null, None);
        Assert.DoesNotContain(rows, r => r.Value == "Amenaza Virtual"); // the old film's title is gone
        Assert.Contains(rows, r => r is { Locale: "en-US", Field: MetadataFields.Name, Value: "Another Film" });
        Assert.Contains(rows, r => r is { Value: "Curada a mano", Locked: true });
        Assert.Empty(await _db.Store.GetImagesAsync([Antitrust], null, None)); // the old film's posters too
    }

    [Fact]
    public async Task No_tmdb_id_is_skipped_and_retried_when_the_ids_change()
    {
        _library.Add(FakeLibrary.Movie(Antitrust, null));

        Assert.Equal(FetchResult.Skipped, await _fetcher.FetchAsync(Antitrust, force: false, None));
        Assert.Equal(FetchResult.Unchanged, await _fetcher.FetchAsync(Antitrust, force: false, None));
        Assert.Empty(_tmdb.Calls);
        var skipped = await _db.Store.GetFetchStateAsync(Antitrust, None);
        Assert.Equal((FetchStatus.Skipped, ItemKind.Movie), (skipped!.Status, skipped.Kind)); // the kind is known even with nothing to fetch

        _library.Add(FakeLibrary.Movie(Antitrust, "9989")); // metadata refresh found the id
        Assert.Equal(FetchResult.Fetched, await _fetcher.FetchAsync(Antitrust, force: false, None));
    }

    [Fact]
    public async Task Unknown_to_tmdb_is_skipped()
    {
        _library.Add(FakeLibrary.Movie(Antitrust, "999999999"));
        _tmdb.Answer = _ => null;

        Assert.Equal(FetchResult.Skipped, await _fetcher.FetchAsync(Antitrust, force: false, None));
        Assert.Contains("404", (await _db.Store.GetFetchStateAsync(Antitrust, None))!.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_provider_failure_is_recorded_and_retried_next_time()
    {
        _library.Add(FakeLibrary.Movie(Antitrust, "9989"));
        _tmdb.Answer = _ => throw new ProviderNotConfiguredException("TMDB API key is not set");

        Assert.Equal(FetchResult.Failed, await _fetcher.FetchAsync(Antitrust, force: false, None));
        var state = await _db.Store.GetFetchStateAsync(Antitrust, None);
        Assert.Equal((FetchStatus.Error, "TMDB API key is not set"), (state!.Status, state.LastError));

        _tmdb.Answer = _ => Antitrust_; // the admin added the key
        Assert.Equal(FetchResult.Fetched, await _fetcher.FetchAsync(Antitrust, force: false, None));
    }

    [Fact]
    public async Task An_unexpected_exception_is_not_swallowed()
    {
        _library.Add(FakeLibrary.Movie(Antitrust, "9989"));
        _tmdb.Answer = _ => throw new InvalidOperationException("bug");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _fetcher.FetchAsync(Antitrust, force: false, None));
    }

    [Fact]
    public async Task An_item_gone_from_the_library_loses_its_rows()
    {
        _library.Add(FakeLibrary.Movie(Antitrust, "9989"));
        await _fetcher.FetchAsync(Antitrust, force: false, None);
        _library.Items.Remove(Antitrust);

        Assert.Equal(FetchResult.Removed, await _fetcher.FetchAsync(Antitrust, force: false, None));
        Assert.Empty(await _db.Store.GetFieldsAsync([Antitrust], null, None));
        Assert.Null(await _db.Store.GetFetchStateAsync(Antitrust, None));
    }

    [Fact]
    public async Task An_episode_is_fetched_by_its_series_id_and_numbers()
    {
        var episode = Guid.NewGuid();
        _library.Add(FakeLibrary.Episode(episode, "80834", season: 1, episode: 3));

        await _fetcher.FetchAsync(episode, force: false, None);

        Assert.Equal(["tv/80834/season/1/episode/3"], _tmdb.Calls);
        var state = await _db.Store.GetFetchStateAsync(episode, None);
        Assert.Equal(("80834", "1", "3"), (state!.ProviderIds["Series.Tmdb"], state.ProviderIds["Season"], state.ProviderIds["Episode"]));
        Assert.Equal(ItemKind.Episode, state.Kind);
    }

    [Fact]
    public async Task An_episode_without_numbers_is_skipped()
    {
        var episode = Guid.NewGuid();
        _library.Add(FakeLibrary.Episode(episode, "80834", season: 1, episode: null));

        Assert.Equal(FetchResult.Skipped, await _fetcher.FetchAsync(episode, force: false, None));
        Assert.Empty(_tmdb.Calls);
    }

    // ---- AniList ----

    [Fact]
    public async Task AniList_fills_only_what_tmdb_left_empty()
    {
        _config.Current.EnabledLocales = ["en-US", "ja-JP", "ja-Latn"];
        AddMagia();
        _tmdb.Answer = _ => MagiaTmdb;

        Assert.Equal(FetchResult.Fetched, await _fetcher.FetchAsync(Magia, force: false, None));

        Assert.Equal([104051], _aniList.Calls);
        var rows = await _db.Store.GetFieldsAsync([Magia], null, None);
        Assert.Equal(
            [
                "en-US name tmdb",
                "en-US overview anilist", // TMDB has no English overview
                "ja-JP name tmdb",
                "ja-JP overview tmdb",
                "ja-Latn name anilist", // romaji: TMDB never has it
            ],
            rows.Select(r => $"{r.Locale} {r.Field} {r.Source}").Order(StringComparer.Ordinal));
        Assert.Equal(FetchStatus.Ok, (await _db.Store.GetFetchStateAsync(Magia, None))!.Status);
    }

    [Fact]
    public async Task Romaji_is_stored_for_search_even_when_ja_Latn_is_not_enabled()
    {
        _config.Current.EnabledLocales = ["en-US"];
        AddMagia();
        _tmdb.Answer = _ => MagiaTmdb;

        await _fetcher.FetchAsync(Magia, force: false, None);

        var rows = await _db.Store.GetFieldsAsync([Magia], null, None);
        Assert.Equal(("Magia Record: Mahou Shoujo Madoka☆Magica Gaiden", MetadataSources.AniList), Value(rows, "ja-Latn", MetadataFields.Name));
        Assert.DoesNotContain(rows, r => r.Locale == "ja-JP"); // the native title still follows the enabled locales
        var hits = await _db.Store.SearchNamesAsync(SearchQuery.Parse("mahou shoujo"), Enum.GetValues<ItemKind>().ToHashSet(), 50, None);
        Assert.Contains(hits, h => h.ItemId == Magia && h.Locale == "ja-Latn");
    }

    [Fact]
    public void Romanized_titles_of_any_language_are_kept_but_other_locales_still_follow_the_enabled_ones()
    {
        var rows = MetadataFetcher.ToAniListFields(
            Magia,
            [
                new("ko-KR", "검은 사제들", null),
                new("ko-Latn", "Geomeun Sajedeul", null),
                new("zh-Latn", "Tian Guan Ci Fu", null),
                new("en-US", "The Priests", "Synopsis."),
            ],
            ["es-MX"]);

        Assert.Equal(["ko-Latn", "zh-Latn"], rows.Select(r => r.Locale).Order(StringComparer.Ordinal));
        Assert.All(rows, r => Assert.Equal(MetadataFields.Name, r.Field));
    }

    [Fact]
    public async Task AniList_never_replaces_a_manual_value_and_gives_way_when_tmdb_gains_one()
    {
        _config.Current.EnabledLocales = ["en-US", "ja-JP", "ja-Latn"];
        AddMagia();
        _tmdb.Answer = _ => MagiaTmdb;
        await _db.Store.UpsertFieldsAsync([new(Magia, "ja-Latn", MetadataFields.Name, "Magireco", MetadataSources.Manual)], None);
        await _fetcher.FetchAsync(Magia, force: false, None);

        // TMDB adds an English overview later.
        _tmdb.Answer = _ => new TmdbTitle([.. MagiaTmdb.Translations, new("en-US", null, "From TMDB.", null)], []);
        await _fetcher.FetchAsync(Magia, force: true, None);

        var rows = await _db.Store.GetFieldsAsync([Magia], null, None);
        Assert.Equal(("Magireco", MetadataSources.Manual), Value(rows, "ja-Latn", MetadataFields.Name));
        Assert.Equal(("From TMDB.", MetadataSources.Tmdb), Value(rows, "en-US", MetadataFields.Overview));
    }

    [Fact]
    public async Task An_item_with_only_an_AniList_id_is_fetched_from_AniList()
    {
        _config.Current.EnabledLocales = ["en-US"];
        _library.Add(FakeLibrary.WithIds(Magia, ItemKind.Series, (ProviderNames.AniList, "104051")));

        Assert.Equal(FetchResult.Fetched, await _fetcher.FetchAsync(Magia, force: false, None));

        Assert.Empty(_tmdb.Calls);
        var rows = await _db.Store.GetFieldsAsync([Magia], null, None);
        Assert.All(rows, r => Assert.Equal(MetadataSources.AniList, r.Source));
        Assert.Equal(["en-US", "ja-Latn"], rows.Select(r => r.Locale).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task An_AniList_failure_keeps_tmdbs_text_and_is_retried()
    {
        AddMagia();
        _tmdb.Answer = _ => MagiaTmdb;
        _aniList.Answer = _ => throw new HttpRequestException("AniList media 104051 failed with 503 after 4 attempt(s).");

        Assert.Equal(FetchResult.Failed, await _fetcher.FetchAsync(Magia, force: false, None));

        var state = await _db.Store.GetFetchStateAsync(Magia, None);
        Assert.Equal((FetchStatus.Error, "AniList: AniList media 104051 failed with 503 after 4 attempt(s)."), (state!.Status, state.LastError));
        Assert.Contains(await _db.Store.GetFieldsAsync([Magia], null, None), r => r.Source == MetadataSources.Tmdb);

        _aniList.Answer = _ => MagiaAniList;
        Assert.Equal(FetchResult.Fetched, await _fetcher.FetchAsync(Magia, force: false, None));
    }

    [Fact]
    public async Task Unknown_to_both_providers_is_skipped_with_both_reasons()
    {
        AddMagia();
        _tmdb.Answer = _ => null;
        _aniList.Answer = _ => null;

        Assert.Equal(FetchResult.Skipped, await _fetcher.FetchAsync(Magia, force: false, None));

        Assert.Equal(
            "TMDB has no such title (404); AniList has no such title (404)",
            (await _db.Store.GetFetchStateAsync(Magia, None))!.LastError);
    }

    [Fact]
    public async Task AniList_can_be_turned_off()
    {
        _config.Current.AniListEnabled = false;
        AddMagia();
        _tmdb.Answer = _ => MagiaTmdb;

        await _fetcher.FetchAsync(Magia, force: false, None);

        Assert.Empty(_aniList.Calls);
    }

    [Theory]
    [InlineData(true)] // the item was re-identified without an AniList id
    [InlineData(false)] // the admin turned AniList off
    public async Task AniList_titles_go_when_AniList_no_longer_applies(bool idRemoved)
    {
        _config.Current.EnabledLocales = ["en-US", "ja-JP", "ja-Latn"];
        AddMagia();
        _tmdb.Answer = _ => MagiaTmdb;
        await _fetcher.FetchAsync(Magia, force: false, None);
        await _db.Store.UpsertFieldsAsync([new(Magia, "ko-Latn", MetadataFields.Name, "Kept", MetadataSources.AniList, Locked: true)], None);

        if (idRemoved)
        {
            _library.Add(FakeLibrary.Series(Magia, "94523"));
        }
        else
        {
            _config.Current.AniListEnabled = false;
        }

        await _fetcher.FetchAsync(Magia, force: true, None);

        var rows = await _db.Store.GetFieldsAsync([Magia], null, None);
        Assert.Equal(["Kept"], rows.Where(r => r.Source == MetadataSources.AniList).Select(r => r.Value)); // locked rows stay
        Assert.Contains(rows, r => r.Source == MetadataSources.Tmdb);
    }

    [Theory]
    [InlineData(ItemKind.Season)]
    [InlineData(ItemKind.Episode)]
    public async Task Seasons_and_episodes_are_never_looked_up_on_AniList(ItemKind kind)
    {
        _library.Add(FakeLibrary.WithIds(Magia, kind, (ProviderNames.AniList, "104051")));

        Assert.Equal(FetchResult.Skipped, await _fetcher.FetchAsync(Magia, force: false, None));
        Assert.Empty(_aniList.Calls);
    }

    [Fact]
    public async Task A_new_AniList_id_triggers_a_fetch()
    {
        _library.Add(FakeLibrary.Series(Magia, "94523"));
        _tmdb.Answer = _ => MagiaTmdb;
        await _fetcher.FetchAsync(Magia, force: false, None);

        AddMagia(); // the AniList plugin added its id
        Assert.Equal(FetchResult.Fetched, await _fetcher.FetchAsync(Magia, force: false, None));
        Assert.Equal([104051], _aniList.Calls);
    }

    // ---- TVDB and provider priority ----

    // The Cabin in the Woods as TVDB (963) has it, trimmed: one entry per language.
    private static readonly TvdbTitle CabinTvdb = new(
        [
            new("en", "The Cabin in the Woods", "Five friends go for a break…", "You think you know the story."),
            new("es", "La cabaña en el bosque", "Cinco estudiantes universitarios…", null),
            new("ja", "キャビン", "夏休みに山へ…", null),
            new("de", "The Cabin in the Woods", "Fünf Freunde…", null),
        ],
        [
            new("en", "Primary", "https://artworks.thetvdb.com/banners/movies/963/posters/en.jpg", 680, 1000),
            new("es", "Primary", "https://artworks.thetvdb.com/banners/movies/963/posters/es.jpg", 680, 1000),
        ]);

    private static readonly TmdbTitle CabinTmdb = new(
        [
            new("en-US", "The Cabin in the Woods", "A group of five friends…", null),
            new("es-MX", "La cabaña del terror", null, null), // no Mexican overview
            new("ja-JP", "キャビン", null, null),
        ],
        [new("es-MX", "Primary", "https://image.tmdb.org/t/p/original/mx.jpg", 2000, 3000)]);

    private static readonly Guid Cabin = Guid.Parse("33333333333333333333333333333333");

    [Fact]
    public async Task By_default_tvdb_is_never_asked()
    {
        _library.Add(FakeLibrary.WithIds(Cabin, ItemKind.Movie, (ProviderNames.Tmdb, "22970"), (ProviderNames.Tvdb, "963")));

        await _fetcher.FetchAsync(Cabin, force: false, None);

        Assert.Empty(_tvdb.Calls);
        var state = await _db.Store.GetFetchStateAsync(Cabin, None);
        Assert.False(state!.ProviderIds.ContainsKey(MetadataFetcher.ProvidersSnapshotKey)); // old snapshots stay valid
    }

    [Fact]
    public async Task Tvdb_alone_spreads_its_languages_over_the_enabled_locales()
    {
        UseProviders(tmdb: false, tvdb: true);
        _library.Add(FakeLibrary.WithIds(Cabin, ItemKind.Movie, (ProviderNames.Tmdb, "22970"), (ProviderNames.Tvdb, "963")));
        _tvdb.Answer = _ => CabinTvdb;

        Assert.Equal(FetchResult.Fetched, await _fetcher.FetchAsync(Cabin, force: false, None));

        Assert.Empty(_tmdb.Calls);
        Assert.Equal(["movie/963"], _tvdb.Calls);
        var rows = await _db.Store.GetFieldsAsync([Cabin], null, None);
        Assert.All(rows, r => Assert.Equal(MetadataSources.Tvdb, r.Source));
        // en → en-US, es → es-MX, ja → ja-JP (the enabled locales); German isn't enabled.
        Assert.Equal(["en-US", "es-MX", "ja-JP"], rows.Select(r => r.Locale).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(("La cabaña en el bosque", MetadataSources.Tvdb), Value(rows, "es-MX", MetadataFields.Name));
        Assert.Equal(("You think you know the story.", MetadataSources.Tvdb), Value(rows, "en-US", MetadataFields.Tagline));
        var poster = Assert.Single(await _db.Store.GetImagesAsync([Cabin], ["es-MX"], None));
        Assert.Equal(("https://artworks.thetvdb.com/banners/movies/963/posters/es.jpg", 680, MetadataSources.Tvdb), (poster.Url, poster.Width!.Value, poster.Source));
    }

    [Fact]
    public async Task A_movie_without_a_tvdb_id_is_looked_up_by_its_imdb_id()
    {
        UseProviders(tmdb: false, tvdb: true);
        _library.Add(FakeLibrary.WithIds(Cabin, ItemKind.Movie, (ProviderNames.Imdb, "tt1259521")));
        _tvdb.Find = path => path == "find/movie/tt1259521" ? 963 : null;
        _tvdb.Answer = _ => CabinTvdb;

        Assert.Equal(FetchResult.Fetched, await _fetcher.FetchAsync(Cabin, force: false, None));
        Assert.Equal(["find/movie/tt1259521", "movie/963"], _tvdb.Calls);
    }

    [Fact]
    public async Task Tmdb_first_keeps_tmdbs_values_and_tvdb_fills_the_gaps()
    {
        UseProviders(tmdb: true, tvdb: true, tmdbWeight: 80, tvdbWeight: 20);
        _library.Add(FakeLibrary.WithIds(Cabin, ItemKind.Movie, (ProviderNames.Tmdb, "22970"), (ProviderNames.Tvdb, "963")));
        _tmdb.Answer = _ => CabinTmdb;
        _tvdb.Answer = _ => CabinTvdb;

        Assert.Equal(FetchResult.Fetched, await _fetcher.FetchAsync(Cabin, force: false, None));

        var rows = await _db.Store.GetFieldsAsync([Cabin], null, None);
        Assert.Equal(("La cabaña del terror", MetadataSources.Tmdb), Value(rows, "es-MX", MetadataFields.Name));
        Assert.Equal(("Cinco estudiantes universitarios…", MetadataSources.Tvdb), Value(rows, "es-MX", MetadataFields.Overview)); // TMDB had none
        Assert.Equal(("夏休みに山へ…", MetadataSources.Tvdb), Value(rows, "ja-JP", MetadataFields.Overview));
        Assert.Equal(("A group of five friends…", MetadataSources.Tmdb), Value(rows, "en-US", MetadataFields.Overview));
        var images = await _db.Store.GetImagesAsync([Cabin], null, None);
        Assert.Equal(MetadataSources.Tmdb, Assert.Single(images, i => i.Locale == "es-MX").Source);
        Assert.Equal(MetadataSources.Tvdb, Assert.Single(images, i => i.Locale == "en-US").Source); // TMDB had no en-US poster
    }

    [Fact]
    public async Task Tvdb_first_wins_where_both_have_a_value()
    {
        UseProviders(tmdb: true, tvdb: true, tmdbWeight: 10, tvdbWeight: 90);
        _library.Add(FakeLibrary.WithIds(Cabin, ItemKind.Movie, (ProviderNames.Tmdb, "22970"), (ProviderNames.Tvdb, "963")));
        _tmdb.Answer = _ => CabinTmdb;
        _tvdb.Answer = _ => CabinTvdb;

        await _fetcher.FetchAsync(Cabin, force: false, None);

        var rows = await _db.Store.GetFieldsAsync([Cabin], null, None);
        Assert.Equal(("La cabaña en el bosque", MetadataSources.Tvdb), Value(rows, "es-MX", MetadataFields.Name));
        Assert.Equal(MetadataSources.Tvdb, Assert.Single(await _db.Store.GetImagesAsync([Cabin], ["es-MX"], None)).Source);
        // TMDB still keeps the locales TVDB doesn't reach (es-ES isn't enabled, so TVDB's Spanish skips it).
        Assert.DoesNotContain(rows, r => r.Locale == "es-ES" && r.Source == MetadataSources.Tvdb);
    }

    [Fact]
    public async Task A_tie_goes_to_tmdb()
    {
        UseProviders(tmdb: true, tvdb: true, tmdbWeight: 50, tvdbWeight: 50);
        _library.Add(FakeLibrary.WithIds(Cabin, ItemKind.Movie, (ProviderNames.Tmdb, "22970"), (ProviderNames.Tvdb, "963")));
        _tmdb.Answer = _ => CabinTmdb;
        _tvdb.Answer = _ => CabinTvdb;

        await _fetcher.FetchAsync(Cabin, force: false, None);

        Assert.Equal(MetadataSources.Tmdb, Value(await _db.Store.GetFieldsAsync([Cabin], null, None), "es-MX", MetadataFields.Name).Source);
    }

    [Fact]
    public async Task A_tvdb_failure_keeps_its_stored_values_until_the_retry()
    {
        UseProviders(tmdb: true, tvdb: true, tmdbWeight: 10, tvdbWeight: 90);
        _library.Add(FakeLibrary.WithIds(Cabin, ItemKind.Movie, (ProviderNames.Tmdb, "22970"), (ProviderNames.Tvdb, "963")));
        _tmdb.Answer = _ => CabinTmdb;
        _tvdb.Answer = _ => CabinTvdb;
        await _fetcher.FetchAsync(Cabin, force: false, None);

        _tvdb.Answer = _ => throw new HttpRequestException("TVDB movies/963/extended?meta=translations failed with 503 after 4 attempt(s).");
        Assert.Equal(FetchResult.Failed, await _fetcher.FetchAsync(Cabin, force: true, None));

        var rows = await _db.Store.GetFieldsAsync([Cabin], null, None);
        Assert.Equal(("La cabaña en el bosque", MetadataSources.Tvdb), Value(rows, "es-MX", MetadataFields.Name)); // not handed to TMDB
        var state = await _db.Store.GetFetchStateAsync(Cabin, None);
        Assert.Equal((FetchStatus.Error, "TVDB movies/963/extended?meta=translations failed with 503 after 4 attempt(s)."), (state!.Status, state.LastError));
    }

    [Fact]
    public async Task A_tvdb_key_problem_is_reported_with_its_provider()
    {
        UseProviders(tmdb: false, tvdb: true);
        _library.Add(FakeLibrary.WithIds(Cabin, ItemKind.Movie, (ProviderNames.Tvdb, "963")));
        _tvdb.Answer = _ => throw new HttpRequestException("connection reset");

        Assert.Equal(FetchResult.Failed, await _fetcher.FetchAsync(Cabin, force: false, None));
        Assert.Equal("TVDB: connection reset", (await _db.Store.GetFetchStateAsync(Cabin, None))!.LastError);
    }

    [Fact]
    public async Task Turning_a_provider_off_removes_its_values_but_not_locked_ones()
    {
        UseProviders(tmdb: true, tvdb: true);
        _library.Add(FakeLibrary.WithIds(Cabin, ItemKind.Movie, (ProviderNames.Tmdb, "22970"), (ProviderNames.Tvdb, "963")));
        _tmdb.Answer = _ => CabinTmdb;
        _tvdb.Answer = _ => CabinTvdb;
        await _fetcher.FetchAsync(Cabin, force: false, None);
        await _db.Store.UpsertFieldsAsync([new(Cabin, "de", MetadataFields.Name, "Kept", MetadataSources.Tvdb, Locked: true)], None);

        UseProviders(tmdb: true, tvdb: false);
        Assert.Equal(FetchResult.Fetched, await _fetcher.FetchAsync(Cabin, force: false, None)); // the snapshot changed

        var rows = await _db.Store.GetFieldsAsync([Cabin], null, None);
        Assert.Equal(["Kept"], rows.Where(r => r.Source == MetadataSources.Tvdb).Select(r => r.Value));
        Assert.DoesNotContain(await _db.Store.GetImagesAsync([Cabin], null, None), i => i.Source == MetadataSources.Tvdb);
    }

    [Fact]
    public async Task Changing_which_provider_wins_makes_an_item_count_as_changed()
    {
        UseProviders(tmdb: true, tvdb: true, tmdbWeight: 90, tvdbWeight: 10);
        _library.Add(FakeLibrary.WithIds(Cabin, ItemKind.Movie, (ProviderNames.Tmdb, "22970"), (ProviderNames.Tvdb, "963")));
        await _fetcher.FetchAsync(Cabin, force: false, None);

        UseProviders(tmdb: true, tvdb: true, tmdbWeight: 80, tvdbWeight: 20); // same order
        Assert.Equal(FetchResult.Unchanged, await _fetcher.FetchAsync(Cabin, force: false, None));

        UseProviders(tmdb: true, tvdb: true, tmdbWeight: 20, tvdbWeight: 80);
        Assert.Equal(FetchResult.Fetched, await _fetcher.FetchAsync(Cabin, force: false, None));
        Assert.Equal("tvdb>tmdb", (await _db.Store.GetFetchStateAsync(Cabin, None))!.ProviderIds[MetadataFetcher.ProvidersSnapshotKey]);
    }

    [Fact]
    public async Task A_season_without_its_own_tvdb_id_is_found_by_the_series_and_number()
    {
        UseProviders(tmdb: false, tvdb: true);
        var season = Guid.NewGuid();
        _library.Add(FakeLibrary.Child(season, ItemKind.Season, [], [(ProviderNames.Tvdb, "370931")], season: 1));
        _tvdb.Find = path => path == "find/season/370931/1" ? 1805557 : null;

        Assert.Equal(FetchResult.Fetched, await _fetcher.FetchAsync(season, force: false, None));

        Assert.Equal(["find/season/370931/1", "season/1805557"], _tvdb.Calls);
        Assert.Equal("370931", (await _db.Store.GetFetchStateAsync(season, None))!.ProviderIds["Series.Tvdb"]);
    }

    [Fact]
    public async Task An_episode_uses_its_own_tvdb_id_when_it_has_one()
    {
        UseProviders(tmdb: false, tvdb: true);
        var episode = Guid.NewGuid();
        _library.Add(FakeLibrary.Child(episode, ItemKind.Episode, [(ProviderNames.Tvdb, "7489229")], [(ProviderNames.Tvdb, "370931")], 1, 1));

        await _fetcher.FetchAsync(episode, force: false, None);

        Assert.Equal(["episode/7489229"], _tvdb.Calls);
    }

    [Fact]
    public async Task An_episode_unknown_to_tvdb_is_skipped()
    {
        UseProviders(tmdb: false, tvdb: true);
        var episode = Guid.NewGuid();
        _library.Add(FakeLibrary.Child(episode, ItemKind.Episode, [], [(ProviderNames.Tvdb, "346988")], 1, 99));

        Assert.Equal(FetchResult.Skipped, await _fetcher.FetchAsync(episode, force: false, None));

        Assert.Equal(["find/episode/346988/1/99"], _tvdb.Calls);
        Assert.Equal("TVDB has no such title (404)", (await _db.Store.GetFetchStateAsync(episode, None))!.LastError);
    }

    [Fact]
    public async Task No_usable_id_for_either_provider_gives_both_reasons()
    {
        UseProviders(tmdb: true, tvdb: true);
        _library.Add(FakeLibrary.Movie(Cabin, null));

        Assert.Equal(FetchResult.Skipped, await _fetcher.FetchAsync(Cabin, force: false, None));

        Assert.Empty(_tvdb.Calls);
        Assert.Equal("No TMDB id; No TVDB or IMDb id", (await _db.Store.GetFetchStateAsync(Cabin, None))!.LastError);
    }

    [Fact]
    public async Task With_both_main_providers_off_only_AniList_is_asked()
    {
        UseProviders(tmdb: false, tvdb: false);
        _config.Current.EnabledLocales = ["en-US"];
        AddMagia();

        Assert.Equal(FetchResult.Fetched, await _fetcher.FetchAsync(Magia, force: false, None));
        Assert.Empty(_tmdb.Calls);
        Assert.Empty(_tvdb.Calls);
        Assert.All(await _db.Store.GetFieldsAsync([Magia], null, None), r => Assert.Equal(MetadataSources.AniList, r.Source));
    }

    [Fact]
    public async Task AniList_fills_only_what_tvdb_left_empty()
    {
        UseProviders(tmdb: false, tvdb: true);
        _config.Current.EnabledLocales = ["en-US", "ja-JP"];
        _library.Add(FakeLibrary.WithIds(Magia, ItemKind.Series, (ProviderNames.Tvdb, "370931"), (ProviderNames.AniList, "104051")));
        _tvdb.Answer = _ => new TvdbTitle([new("en", "Magia Record: Puella Magi Madoka Magica Side Story", null, null)], []);

        await _fetcher.FetchAsync(Magia, force: false, None);

        var rows = await _db.Store.GetFieldsAsync([Magia], null, None);
        Assert.Equal(MetadataSources.Tvdb, Value(rows, "en-US", MetadataFields.Name).Source);
        Assert.Equal(MetadataSources.AniList, Value(rows, "en-US", MetadataFields.Overview).Source);
        Assert.Equal(MetadataSources.AniList, Value(rows, "ja-Latn", MetadataFields.Name).Source);
    }

    [Fact]
    public void Spread_fills_enabled_siblings_but_not_where_a_more_exact_value_exists()
    {
        var spread = MetadataFetcher.Spread(
                [
                    ("es", "name", "Spanish"),
                    ("pt-BR", "name", "Brazilian"),
                    ("pt-PT", "name", "European"),
                    ("zh-CN", "name", "Simplified"),
                    ("zh-TW", "name", "Traditional"),
                    ("ja", "name", "Japanese"),
                    ("ja-JP", "overview", "Overview"),
                    ("de", "name", "German"),
                ],
                ["es-MX", "es-ES", "pt-BR", "zh-CN", "ja-JP"])
            .Select(v => $"{v.Locale} {v.Key} {v.Value}")
            .Order(StringComparer.Ordinal);

        Assert.Equal(
            [
                "es-ES name Spanish",
                "es-MX name Spanish",
                "ja-JP name Japanese",
                "ja-JP overview Overview",
                "pt-BR name Brazilian",
                "pt-PT name European", // the pt group is enabled, so it's kept for fallback, but only as pt-PT
                "zh-CN name Simplified", // zh-TW is another fallback group: dropped; German isn't enabled
            ],
            spread);
    }

    [Theory]
    [InlineData(true, false, 100, 50, new[] { "tmdb" })]
    [InlineData(false, true, 100, 50, new[] { "tvdb" })]
    [InlineData(true, true, 100, 50, new[] { "tmdb", "tvdb" })]
    [InlineData(true, true, 10, 90, new[] { "tvdb", "tmdb" })]
    [InlineData(true, true, 70, 70, new[] { "tmdb", "tvdb" })]
    [InlineData(true, true, 500, 100, new[] { "tmdb", "tvdb" })] // clamped to 100: a tie
    [InlineData(true, true, -5, 0, new[] { "tmdb", "tvdb" })] // clamped to 0: a tie
    [InlineData(false, false, 100, 50, new string[0])]
    public void The_order_follows_the_weights(bool tmdb, bool tvdb, int tmdbWeight, int tvdbWeight, string[] expected)
    {
        var config = new PluginConfiguration { TmdbEnabled = tmdb, TvdbEnabled = tvdb, TmdbWeight = tmdbWeight, TvdbWeight = tvdbWeight };

        Assert.Equal(expected, ProviderPriority.Order(config));
        Assert.Equal(expected is ["tmdb"] ? null : string.Join('>', expected), ProviderPriority.Fingerprint(config));
    }

    private void UseProviders(bool tmdb, bool tvdb, int tmdbWeight = ProviderPriority.DefaultTmdbWeight, int tvdbWeight = ProviderPriority.DefaultTvdbWeight)
    {
        _config.Current.TmdbEnabled = tmdb;
        _config.Current.TvdbEnabled = tvdb;
        _config.Current.TmdbWeight = tmdbWeight;
        _config.Current.TvdbWeight = tvdbWeight;
    }

    private static (string Value, string Source) Value(IEnumerable<LocalizedField> rows, string locale, string field)
    {
        var row = Assert.Single(rows, r => r.Locale == locale && r.Field == field);
        return (row.Value, row.Source);
    }

    private void AddMagia() =>
        _library.Add(FakeLibrary.WithIds(Magia, ItemKind.Series, (ProviderNames.Tmdb, "94523"), (ProviderNames.AniList, "104051")));
}
