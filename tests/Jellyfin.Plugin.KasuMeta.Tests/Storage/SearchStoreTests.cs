using Jellyfin.Plugin.KasuMeta.Library;
using Jellyfin.Plugin.KasuMeta.Search;
using Jellyfin.Plugin.KasuMeta.Storage;

namespace Jellyfin.Plugin.KasuMeta.Tests.Storage;

public class SearchStoreTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Guid Cabin = Guid.Parse("22970000000000000000000000000000");
    private static readonly Guid Chihiro = Guid.Parse("00129000000000000000000000000000");
    private static readonly Guid Revue = Guid.Parse("80834000000000000000000000000000");
    private static readonly IReadOnlySet<ItemKind> AllKinds = Enum.GetValues<ItemKind>().ToHashSet();

    [Theory]
    [InlineData("cabana", "es-MX")] // accent folded
    [InlineData("CABAÑA", "es-MX")] // case folded
    [InlineData("la cab", "es-MX")] // last term is a prefix
    [InlineData("terror cabaña", "es-MX")] // order doesn't matter
    [InlineData("woods", "en-US")]
    public async Task Words_match_through_the_index(string text, string locale)
    {
        using var db = await SeededAsync();

        var hits = await SearchAsync(db, text);

        Assert.Contains(hits, h => h.ItemId == Cabin && h.Locale == locale);
    }

    [Theory]
    [InlineData("cabaña zzz")] // every term is required
    [InlineData("bosq terror")] // only the last term may be a prefix
    [InlineData("abin")] // words match from their start, not their middle
    public async Task Words_that_dont_all_match_find_nothing(string text)
    {
        using var db = await SeededAsync();

        Assert.DoesNotContain(await SearchAsync(db, text), h => h.ItemId == Cabin);
    }

    [Theory]
    [InlineData("千尋")] // the middle of a title: full-text search alone can't find this
    [InlineData("神隠し")]
    [InlineData("千と千尋の神隠し")]
    public async Task Text_without_spaces_matches_anywhere(string text)
    {
        using var db = await SeededAsync();

        var hit = Assert.Single(await SearchAsync(db, text));

        Assert.Equal((Chihiro, "ja-JP"), (hit.ItemId, hit.Locale));
    }

    [Fact]
    public async Task Words_with_combining_marks_match()
    {
        using var db = new TestDatabase();
        await db.Store.UpsertFieldsAsync(
            [
                new(Cabin, "hi-IN", MetadataFields.Name, "हिन्दी मीडियम", MetadataSources.Tmdb),
                new(Chihiro, "th-TH", MetadataFields.Name, "สวัสดีครับ คุณครู", MetadataSources.Tmdb),
                new(Revue, "es-MX", MetadataFields.Name, "La cabaña del terror", MetadataSources.Tmdb),
            ],
            None);

        Assert.Equal(Cabin, Assert.Single(await SearchAsync(db, "हिन्दी")).ItemId); // the index splits at marks; a phrase still matches
        Assert.Equal(Cabin, Assert.Single(await SearchAsync(db, "मीडि")).ItemId); // prefix of the second word
        Assert.Equal(Chihiro, Assert.Single(await SearchAsync(db, "สวัสดี")).ItemId); // Thai: substring
        Assert.Equal(Revue, Assert.Single(await SearchAsync(db, "caban\u0303a")).ItemId); // decomposed ñ
    }

    [Fact]
    public async Task A_mixed_query_needs_both_kinds_of_term()
    {
        using var db = await SeededAsync();

        Assert.Equal(Revue, Assert.Single(await SearchAsync(db, "revue 少女")).ItemId);
        Assert.Empty(await SearchAsync(db, "cabaña 少女"));
    }

    [Fact]
    public async Task One_item_can_match_in_several_locales()
    {
        using var db = await SeededAsync();

        var hits = await SearchAsync(db, "cabaña");

        Assert.Equal(["es-ES", "es-MX"], hits.Where(h => h.ItemId == Cabin).Select(h => h.Locale).Order());
    }

    [Fact]
    public async Task Only_names_are_searched()
    {
        using var db = await SeededAsync();

        Assert.Empty(await SearchAsync(db, "jóvenes")); // a word from an overview
    }

    [Fact]
    public async Task The_index_follows_updates_replacements_and_deletes()
    {
        using var db = await SeededAsync();

        // An update (upsert on an existing row).
        await db.Store.UpsertFieldsAsync([new(Cabin, "es-MX", MetadataFields.Name, "Renombrada", MetadataSources.Tmdb)], None);
        Assert.DoesNotContain(await SearchAsync(db, "terror"), h => h.ItemId == Cabin);
        Assert.Contains(await SearchAsync(db, "renombrada"), h => h.ItemId == Cabin);

        // A provider replacement: the es-ES name is gone, a new locale arrives.
        await db.Store.ReplaceProviderFieldsAsync(
            Cabin,
            MetadataSources.Tmdb,
            [new(Cabin, "en-US", MetadataFields.Name, "The Cabin Reborn", MetadataSources.Tmdb)],
            None);
        Assert.Empty(await SearchAsync(db, "bosque"));
        Assert.Empty(await SearchAsync(db, "renombrada"));
        Assert.Contains(await SearchAsync(db, "reborn"), h => h.ItemId == Cabin);

        // The item leaves the library.
        await db.Store.DeleteItemAsync(Cabin, None);
        Assert.Empty(await SearchAsync(db, "reborn"));
        Assert.Equal(0L, await db.ScalarAsync($"SELECT COUNT(*) FROM search_name WHERE item_id = '{Cabin:N}';"));

        // FTS5's own consistency check against the content table.
        await db.ScalarAsync("INSERT INTO localized_search (localized_search, rank) VALUES ('integrity-check', 1);");
    }

    [Fact]
    public async Task A_locked_name_that_a_provider_write_skips_keeps_its_index_entry()
    {
        using var db = await SeededAsync();
        await db.Store.UpsertFieldsAsync([new(Cabin, "es-MX", MetadataFields.Name, "Curada", MetadataSources.Manual, Locked: true)], None);

        await db.Store.UpsertFieldsAsync([new(Cabin, "es-MX", MetadataFields.Name, "De TMDB", MetadataSources.Tmdb)], None);

        Assert.Contains(await SearchAsync(db, "curada"), h => h.ItemId == Cabin);
        Assert.Empty(await SearchAsync(db, "tmdb"));
    }

    [Fact]
    public async Task Without_fts5_search_still_works_by_substring()
    {
        using var db = new TestDatabase(fts5: false);
        await SeedAsync(db);

        Assert.False(db.Database.SearchIndexReady);
        Assert.Contains(await SearchAsync(db, "cabaña"), h => h.ItemId == Cabin);
        Assert.Contains(await SearchAsync(db, "abin wood"), h => h.ItemId == Cabin); // substrings, any position
        Assert.Contains(await SearchAsync(db, "千尋"), h => h.ItemId == Chihiro);
        Assert.Empty(await SearchAsync(db, "cabana")); // the cost: accents aren't folded
    }

    [Fact]
    public async Task A_database_survives_losing_and_regaining_fts5()
    {
        using var db = await SeededAsync();
        Assert.True(db.Database.SearchIndexReady);

        // The file moves to a server without FTS5: writes must not fail on the index triggers.
        using (var without = db.Open(fts5: false))
        {
            var store = new SqliteLocalizedMetadataStore(without, db.Time);
            await store.UpsertFieldsAsync([new(Revue, "es-MX", MetadataFields.Name, "Sin índice", MetadataSources.Manual)], None);
            Assert.False(without.SearchIndexReady);
            Assert.Contains(await store.SearchNamesAsync(SearchQuery.Parse("índice"), AllKinds, 50, None), h => h.ItemId == Revue);
        }

        // Back on a server with FTS5: the index is rebuilt and has the name written meanwhile.
        using var again = db.Open();
        var rebuilt = new SqliteLocalizedMetadataStore(again, db.Time);
        Assert.Contains(await rebuilt.SearchNamesAsync(SearchQuery.Parse("indice"), AllKinds, 50, None), h => h.ItemId == Revue);
        Assert.True(again.SearchIndexReady);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Kinds_are_filtered_in_the_store_and_reported(bool fts5)
    {
        using var db = new TestDatabase(fts5 ? null : false);
        var movie = Guid.NewGuid();
        var episode = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        var none = new Dictionary<string, string>();
        await db.Store.UpsertFieldsAsync(
            [
                new(movie, "en-US", MetadataFields.Name, "Wolf", MetadataSources.Tmdb),
                new(episode, "en-US", MetadataFields.Name, "Wolf at the door", MetadataSources.Tmdb),
                new(unknown, "en-US", MetadataFields.Name, "Wolf of unknown kind", MetadataSources.Tmdb),
            ],
            None);
        await db.Store.SetFetchStateAsync(new(movie, none, db.Time.Now, FetchStatus.Ok, Kind: ItemKind.Movie), None);
        await db.Store.SetFetchStateAsync(new(episode, none, db.Time.Now, FetchStatus.Ok, Kind: ItemKind.Episode), None);

        var movies = await db.Store.SearchNamesAsync(SearchQuery.Parse("wolf"), new HashSet<ItemKind> { ItemKind.Movie }, 50, None);
        var nothing = await db.Store.SearchNamesAsync(SearchQuery.Parse("wolf"), new HashSet<ItemKind>(), 50, None);

        // The item whose kind isn't recorded passes; the caller settles it against the library.
        Assert.Equal(
            new Dictionary<Guid, ItemKind?> { [movie] = ItemKind.Movie, [unknown] = null },
            movies.ToDictionary(h => h.ItemId, h => h.Kind));
        Assert.Empty(nothing);
        Assert.Equal(ItemKind.Episode, (await db.Store.GetFetchStateAsync(episode, None))!.Kind);
    }

    [Fact]
    public async Task Candidates_are_capped_best_first()
    {
        using var db = new TestDatabase();
        await db.Store.UpsertFieldsAsync(
            Enumerable.Range(0, 40)
                .Select(i => new LocalizedField(Guid.NewGuid(), "en-US", MetadataFields.Name, $"Wolf number {i}", MetadataSources.Tmdb))
                .Append(new LocalizedField(Cabin, "en-US", MetadataFields.Name, "Wolf", MetadataSources.Tmdb))
                .ToArray(),
            None);

        var hits = await db.Store.SearchNamesAsync(SearchQuery.Parse("wolf"), AllKinds, 10, None);

        Assert.Equal(10, hits.Count);
        Assert.Equal(Cabin, hits[0].ItemId); // the shortest document ranks best in BM25
    }

    private static Task<IReadOnlyList<NameHit>> SearchAsync(TestDatabase db, string text) =>
        db.Store.SearchNamesAsync(SearchQuery.Parse(text), AllKinds, 50, None);

    private static async Task<TestDatabase> SeededAsync()
    {
        var db = new TestDatabase();
        await SeedAsync(db);
        return db;
    }

    private static Task<int> SeedAsync(TestDatabase db) =>
        db.Store.UpsertFieldsAsync(
            [
                new(Cabin, "es-MX", MetadataFields.Name, "La cabaña del terror", MetadataSources.Tmdb),
                new(Cabin, "es-ES", MetadataFields.Name, "La cabaña en el bosque", MetadataSources.Tmdb),
                new(Cabin, "en-US", MetadataFields.Name, "The Cabin in the Woods", MetadataSources.Tmdb),
                new(Cabin, "es-MX", MetadataFields.Overview, "Cinco jóvenes se preparan…", MetadataSources.Tmdb),
                new(Chihiro, "ja-JP", MetadataFields.Name, "千と千尋の神隠し", MetadataSources.Tmdb),
                new(Chihiro, "es-MX", MetadataFields.Name, "El viaje de Chihiro", MetadataSources.Tmdb),
                new(Revue, "ja-JP", MetadataFields.Name, "少女☆歌劇 レヴュー・スタァライト", MetadataSources.Tmdb),
                new(Revue, "zh-CN", MetadataFields.Name, "少女☆歌剧 Revue Starlight", MetadataSources.Tmdb),
            ],
            None);
}
