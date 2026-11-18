using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Fetching;
using Jellyfin.Plugin.KasuMeta.Library;
using Jellyfin.Plugin.KasuMeta.Providers.AniList;
using Jellyfin.Plugin.KasuMeta.Providers.Tmdb;
using Jellyfin.Plugin.KasuMeta.Providers.Tvdb;
using Jellyfin.Plugin.KasuMeta.Storage;
using Jellyfin.Plugin.KasuMeta.Tests.Storage;
using Jellyfin.Plugin.KasuMeta.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.KasuMeta.Tests.Fetching;

/// <summary>Per-category provider choice, the original language, genres and tags.</summary>
public sealed class CategoryMergeTests : IDisposable
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Guid Show = Guid.Parse("44444444444444444444444444444444");

    private static readonly TmdbTitle ShowTmdb = new(
        [
            new("en-US", "Frieren", "An elf mage outlives her party.", null),
            new("es-MX", "Frieren: Más allá del final del viaje", "Una maga elfa…", null),
            new("ja-JP", "葬送のフリーレン", "魔王を倒した勇者一行の…", null),
        ],
        [
            new("es-MX", "Primary", "https://image.tmdb.org/t/p/original/es.jpg", 1000, 1500),
            new("ja-JP", "Logo", "https://image.tmdb.org/t/p/original/ja-logo.png", 800, 300),
        ],
        "ja",
        [new TmdbGenre(16, "Animation"), new TmdbGenre(10765, "Sci-Fi & Fantasy")],
        ["magic", "elf"]);

    private static readonly TvdbTitle ShowTvdb = new(
        [
            new("en", "Frieren: Beyond Journey's End", "TVDB's English synopsis.", null),
            new("ja", "葬送のフリーレン", "TVDBのあらすじ", null),
        ],
        [new("es", "Primary", "https://artworks.thetvdb.com/es.jpg", 680, 1000)],
        "ja",
        ["Anime", "Fantasy"],
        ["Elves", "Magic"]);

    private readonly TestDatabase _db = new();
    private readonly FakeLibrary _library = new();
    private readonly FakeTmdb _tmdb = new();
    private readonly FakeTvdb _tvdb = new();
    private readonly FakeAniList _aniList = new();
    private readonly FakeConfig _config = new(new PluginConfiguration
    {
        EnabledLocales = ["en-US", "es-MX"],
        TmdbApiKey = "tmdb-key",
        TvdbApiKey = "tvdb-key",
    });

    private readonly MetadataFetcher _fetcher;

    public CategoryMergeTests()
    {
        _fetcher = new MetadataFetcher(_library, _db.Store, _tmdb, _tvdb, _aniList, _config, _db.Time, NullLogger<MetadataFetcher>.Instance);
        _tmdb.Answer = _ => ShowTmdb;
        _tvdb.Answer = _ => ShowTvdb;
        _library.Add(FakeLibrary.WithIds(Show, ItemKind.Series, (ProviderNames.Tmdb, "209867"), (ProviderNames.Tvdb, "424536")));
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Each_category_takes_its_own_providers_in_its_own_order()
    {
        Rules(
            (MetadataCategories.Name, ["tmdb", "tvdb"]),
            (MetadataCategories.Overview, ["tvdb", "tmdb"]),
            (MetadataCategories.OfImage("Primary"), ["tmdb"]));

        Assert.Equal(FetchResult.Fetched, await _fetcher.FetchAsync(Show, force: false, None));

        var rows = await _db.Store.GetFieldsAsync([Show], null, None);
        Assert.Equal(("Frieren", "tmdb"), Value(rows, "en-US", MetadataFields.Name));
        Assert.Equal(("TVDB's English synopsis.", "tvdb"), Value(rows, "en-US", MetadataFields.Overview));
        // TVDB has no Spanish synopsis: TMDB's fills the locale.
        Assert.Equal(("Una maga elfa…", "tmdb"), Value(rows, "es-MX", MetadataFields.Overview));

        // Posters only from TMDB: TVDB's Spanish poster is left out even where TMDB has none.
        var images = await _db.Store.GetImagesAsync([Show], null, None);
        Assert.All(images.Where(i => i.ImageType == "Primary"), i => Assert.Equal("tmdb", i.Source));
        Assert.Equal("https://image.tmdb.org/t/p/original/es.jpg", Assert.Single(images, i => i.ImageType == "Primary").Url);
    }

    [Fact]
    public async Task A_category_with_no_provider_stores_nothing()
    {
        Rules((MetadataCategories.Tagline, []), (MetadataCategories.OfImage("Logo"), []));
        _tmdb.Answer = _ => ShowTmdb with { Translations = [new("en-US", "Frieren", null, "A tagline")] };

        await _fetcher.FetchAsync(Show, force: false, None);

        Assert.DoesNotContain(await _db.Store.GetFieldsAsync([Show], null, None), r => r.Field == MetadataFields.Tagline);
        Assert.DoesNotContain(await _db.Store.GetImagesAsync([Show], null, None), i => i.ImageType == "Logo");
    }

    [Fact]
    public async Task A_listed_provider_without_a_key_is_skipped_quietly()
    {
        _config.Current.TvdbApiKey = string.Empty;
        Rules((MetadataCategories.Name, ["tvdb", "tmdb"]));

        Assert.Equal(FetchResult.Fetched, await _fetcher.FetchAsync(Show, force: false, None));

        Assert.Empty(_tvdb.Calls);
        var state = await _db.Store.GetFetchStateAsync(Show, None);
        Assert.Equal((FetchStatus.Ok, (string?)null), (state!.Status, state.LastError));
    }

    [Fact]
    public async Task The_original_language_is_kept_and_recorded_even_when_not_enabled()
    {
        await _fetcher.FetchAsync(Show, force: false, None);

        var rows = await _db.Store.GetFieldsAsync([Show], null, None);
        Assert.Equal(("葬送のフリーレン", "tmdb"), Value(rows, "ja-JP", MetadataFields.Name));
        Assert.Contains(await _db.Store.GetImagesAsync([Show], null, None), i => i.Locale == "ja-JP" && i.ImageType == "Logo");
        Assert.Equal("ja", (await _db.Store.GetFetchStateAsync(Show, None))!.OriginalLanguage);
    }

    [Fact]
    public async Task Without_an_original_language_other_languages_are_dropped()
    {
        _tmdb.Answer = _ => ShowTmdb with { OriginalLanguage = null };

        await _fetcher.FetchAsync(Show, force: false, None);

        Assert.DoesNotContain(await _db.Store.GetFieldsAsync([Show], null, None), r => r.Locale == "ja-JP");
        Assert.Null((await _db.Store.GetFetchStateAsync(Show, None))!.OriginalLanguage);
    }

    [Fact]
    public async Task Jellyfins_original_language_wins_and_widens_tmdbs_image_languages()
    {
        _library.Add(FakeLibrary.WithIds(Show, ItemKind.Series, (ProviderNames.Tmdb, "209867")) with { OriginalLanguage = "ko" });

        await _fetcher.FetchAsync(Show, force: false, None);

        Assert.Equal(["ko"], _tmdb.ExtraImageLanguages.Single());
        Assert.Equal("ko", (await _db.Store.GetFetchStateAsync(Show, None))!.OriginalLanguage);
    }

    [Fact]
    public async Task Tmdb_genres_are_stored_per_locale_and_in_english_as_base_terms()
    {
        _tmdb.GenreNames = (tv, locale) => (tv, locale) switch
        {
            (true, "es-MX") => new Dictionary<int, string> { [16] = "Animación", [10765] = "Sci-Fi & Fantasy" },
            (true, "en-US") => new Dictionary<int, string> { [16] = "Animation", [10765] = "Sci-Fi & Fantasy" },
            (true, "ja") => new Dictionary<int, string> { [16] = "アニメーション" }, // incomplete: left out
            _ => new Dictionary<int, string>(),
        };

        await _fetcher.FetchAsync(Show, force: false, None);

        var genres = (await _db.Store.GetFieldsAsync([Show], null, None))
            .Where(r => r.Field == MetadataFields.Genres && r.Source == "tmdb")
            .ToDictionary(r => r.Locale, r => ListValues.Parse(r.Value));
        Assert.Equal(["Animación", "Sci-Fi & Fantasy"], genres["es-MX"]);
        Assert.Equal(["Animation", "Sci-Fi & Fantasy"], genres["en-US"]);
        Assert.False(genres.ContainsKey("ja"));
        Assert.Contains("genre/tv/list?language=ja", _tmdb.Calls); // the original language is asked too
    }

    [Fact]
    public async Task English_terms_come_from_the_first_provider_listed_for_the_category()
    {
        Rules(
            (MetadataCategories.Genres, ["tvdb", "tmdb"]),
            (MetadataCategories.Tags, ["tmdb", "tvdb"]));

        await _fetcher.FetchAsync(Show, force: false, None);

        var rows = await _db.Store.GetFieldsAsync([Show], ["en"], None);
        var genres = Assert.Single(rows, r => r.Field == MetadataFields.Genres);
        Assert.Equal("tvdb", genres.Source);
        Assert.Equal(["Anime", "Fantasy"], ListValues.Parse(genres.Value));
        var tags = Assert.Single(rows, r => r.Field == MetadataFields.Tags);
        Assert.Equal("tmdb", tags.Source);
        Assert.Equal(["magic", "elf"], ListValues.Parse(tags.Value));
    }

    [Fact]
    public async Task Genre_lists_are_not_asked_for_when_tmdb_isnt_listed_for_genres()
    {
        Rules((MetadataCategories.Genres, ["tvdb"]));

        await _fetcher.FetchAsync(Show, force: false, None);

        Assert.DoesNotContain(_tmdb.Calls, c => c.StartsWith("genre/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Anilist_tags_fill_what_the_others_lack()
    {
        _library.Add(FakeLibrary.WithIds(Show, ItemKind.Series, (ProviderNames.Tmdb, "209867"), (ProviderNames.AniList, "154587")));
        _tmdb.Answer = _ => ShowTmdb with { Keywords = [] };
        _aniList.Answer = _ => [new AniListTranslation("en-US", "Frieren", null, ["Adventure"], ["Elf", "Iyashikei"])];

        await _fetcher.FetchAsync(Show, force: false, None);

        var tags = Assert.Single(await _db.Store.GetFieldsAsync([Show], ["en"], None), r => r.Field == MetadataFields.Tags);
        Assert.Equal("anilist", tags.Source);
        Assert.Equal(["Elf", "Iyashikei"], ListValues.Parse(tags.Value));
    }

    [Fact]
    public async Task Changing_one_category_counts_as_a_change()
    {
        await _fetcher.FetchAsync(Show, force: false, None);
        Assert.Equal(FetchResult.Unchanged, await _fetcher.FetchAsync(Show, force: false, None));

        Rules((MetadataCategories.Overview, ["tvdb"]));
        Assert.Equal(FetchResult.Fetched, await _fetcher.FetchAsync(Show, force: false, None));
    }

    [Fact]
    public void Merge_by_category_keeps_each_categorys_order()
    {
        var a = new LocalizedField(Show, "es-MX", MetadataFields.Name, "A", "tmdb");
        var b = new LocalizedField(Show, "es-MX", MetadataFields.Name, "B", "tvdb");
        var c = new LocalizedField(Show, "es-MX", MetadataFields.Overview, "C", "tmdb");
        var d = new LocalizedField(Show, "es-MX", MetadataFields.Overview, "D", "tvdb");
        var rules = new Dictionary<string, IReadOnlyList<string>>
        {
            [MetadataCategories.Name] = ["tmdb", "tvdb"],
            [MetadataCategories.Overview] = ["tvdb", "tmdb"],
        };
        var rows = new Dictionary<string, (IReadOnlyList<LocalizedField>, IReadOnlyList<LocalizedImage>)>
        {
            ["tmdb"] = ([a, c], []),
            ["tvdb"] = ([b, d], []),
        };

        var (fields, _) = MetadataFetcher.MergeByCategory(rules, rows);

        Assert.Equal(["A", "D"], fields.Select(f => f.Value));
    }

    private void Rules(params (string Category, string[] Providers)[] rules) =>
        _config.Current.ProviderRules = rules.Select(r => new ProviderRule { Category = r.Category, Providers = r.Providers }).ToArray();

    private static (string Value, string Source) Value(IEnumerable<LocalizedField> rows, string locale, string field)
    {
        var row = Assert.Single(rows, r => r.Locale == locale && r.Field == field);
        return (row.Value, row.Source);
    }
}
