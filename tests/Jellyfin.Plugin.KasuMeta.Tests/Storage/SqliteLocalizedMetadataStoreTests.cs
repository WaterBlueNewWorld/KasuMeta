using Jellyfin.Plugin.KasuMeta.Storage;

namespace Jellyfin.Plugin.KasuMeta.Tests.Storage;

public class SqliteLocalizedMetadataStoreTests
{
    private static readonly Guid Spirited = Guid.Parse("a1b2c3d4e5f60718293a4b5c6d7e8f90");
    private static readonly Guid Other = Guid.Parse("0f1e2d3c4b5a69788796a5b4c3d2e1f0");
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Fields_round_trip_with_the_write_time()
    {
        using var db = new TestDatabase();

        var written = await db.Store.UpsertFieldsAsync(
            [
                new(Spirited, "es-MX", MetadataFields.Name, "El viaje de Chihiro", MetadataSources.Tmdb),
                new(Spirited, "es-MX", MetadataFields.Overview, "Chihiro, una niña de diez años…", MetadataSources.Tmdb),
            ],
            None);

        var rows = await db.Store.GetFieldsAsync([Spirited], null, None);

        Assert.Equal(2, written);
        var name = Assert.Single(rows, r => r.Field == MetadataFields.Name);
        Assert.Equal("El viaje de Chihiro", name.Value);
        Assert.Equal(MetadataSources.Tmdb, name.Source);
        Assert.False(name.Locked);
        Assert.Equal(db.Time.Now, name.UpdatedAt);
    }

    [Fact]
    public async Task Reads_filter_by_item_and_locale()
    {
        using var db = new TestDatabase();
        await db.Store.UpsertFieldsAsync(
            [
                new(Spirited, "es-MX", MetadataFields.Name, "El viaje de Chihiro", MetadataSources.Tmdb),
                new(Spirited, "ja-JP", MetadataFields.Name, "千と千尋の神隠し", MetadataSources.Tmdb),
                new(Other, "es-MX", MetadataFields.Name, "Otra", MetadataSources.Tmdb),
            ],
            None);

        var rows = await db.Store.GetFieldsAsync([Spirited], ["ja-JP"], None);

        Assert.Equal("千と千尋の神隠し", Assert.Single(rows).Value);
        Assert.Empty(await db.Store.GetFieldsAsync([Spirited], [], None));
        Assert.Empty(await db.Store.GetFieldsAsync([], null, None));
    }

    [Fact]
    public async Task A_provider_write_replaces_an_unlocked_value()
    {
        using var db = new TestDatabase();
        await db.Store.UpsertFieldsAsync([new(Spirited, "es-MX", MetadataFields.Name, "Viejo", MetadataSources.Tmdb)], None);
        db.Time.Now = db.Time.Now.AddDays(1);

        var written = await db.Store.UpsertFieldsAsync([new(Spirited, "es-MX", MetadataFields.Name, "Nuevo", MetadataSources.Tvdb)], None);

        var row = Assert.Single(await db.Store.GetFieldsAsync([Spirited], null, None));
        Assert.Equal(1, written);
        Assert.Equal(("Nuevo", MetadataSources.Tvdb, db.Time.Now), (row.Value, row.Source, row.UpdatedAt));
    }

    [Fact]
    public async Task A_provider_write_never_replaces_a_locked_value()
    {
        using var db = new TestDatabase();
        await db.Store.UpsertFieldsAsync([new(Spirited, "es-MX", MetadataFields.Name, "Curado", MetadataSources.Manual, Locked: true)], None);

        var written = await db.Store.UpsertFieldsAsync([new(Spirited, "es-MX", MetadataFields.Name, "De TMDB", MetadataSources.Tmdb)], None);

        var row = Assert.Single(await db.Store.GetFieldsAsync([Spirited], null, None));
        Assert.Equal(0, written);
        Assert.Equal(("Curado", true), (row.Value, row.Locked));
    }

    [Fact]
    public async Task A_locked_write_replaces_a_locked_value()
    {
        using var db = new TestDatabase();
        await db.Store.UpsertFieldsAsync([new(Spirited, "es-MX", MetadataFields.Name, "Uno", MetadataSources.Manual, Locked: true)], None);

        var written = await db.Store.UpsertFieldsAsync([new(Spirited, "es-MX", MetadataFields.Name, "Dos", MetadataSources.Manual, Locked: true)], None);

        Assert.Equal(1, written);
        Assert.Equal("Dos", Assert.Single(await db.Store.GetFieldsAsync([Spirited], null, None)).Value);
    }

    [Fact]
    public async Task Images_follow_the_same_lock_rule()
    {
        using var db = new TestDatabase();
        await db.Store.UpsertImagesAsync([new(Spirited, "es-MX", LocalizedImageTypes.Primary, "https://example.test/manual.jpg", MetadataSources.Manual, Locked: true)], None);

        var written = await db.Store.UpsertImagesAsync(
            [
                new(Spirited, "es-MX", LocalizedImageTypes.Primary, "https://image.tmdb.org/t/p/w500/a.jpg", MetadataSources.Tmdb),
                new(Spirited, "es-MX", LocalizedImageTypes.Logo, "https://image.tmdb.org/t/p/w500/logo.png", MetadataSources.Tmdb),
            ],
            None);

        var images = await db.Store.GetImagesAsync([Spirited], ["es-MX"], None);
        Assert.Equal(1, written);
        Assert.Equal("https://example.test/manual.jpg", Assert.Single(images, i => i.ImageType == LocalizedImageTypes.Primary).Url);
        Assert.Contains(images, i => i.ImageType == LocalizedImageTypes.Logo);
    }

    [Fact]
    public async Task Unknown_fields_and_image_types_are_rejected()
    {
        using var db = new TestDatabase();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            db.Store.UpsertFieldsAsync([new(Spirited, "es-MX", "title", "x", MetadataSources.Tmdb)], None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            db.Store.UpsertImagesAsync([new(Spirited, "es-MX", "Poster", "x", MetadataSources.Tmdb)], None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            db.Store.UpsertFieldsAsync([new(Spirited, " ", MetadataFields.Name, "x", MetadataSources.Tmdb)], None));
    }

    [Fact]
    public async Task Reads_of_more_ids_than_one_chunk_return_everything()
    {
        using var db = new TestDatabase();
        var ids = Enumerable.Range(0, SqliteLocalizedMetadataStore.ChunkSize + 37).Select(_ => Guid.NewGuid()).ToArray();
        await db.Store.UpsertFieldsAsync(ids.Select(id => new LocalizedField(id, "en-US", MetadataFields.Name, id.ToString(), MetadataSources.Tmdb)).ToArray(), None);

        var rows = await db.Store.GetFieldsAsync(ids, ["en-US"], None);

        Assert.Equal(ids.Length, rows.Count);
    }

    [Fact]
    public async Task Fetch_state_round_trips()
    {
        using var db = new TestDatabase();
        var state = new FetchState(
            Spirited,
            new Dictionary<string, string> { ["Tmdb"] = "129", ["Imdb"] = "tt0245429" },
            db.Time.Now,
            FetchStatus.Error,
            "TMDB 429");

        await db.Store.SetFetchStateAsync(state, None);
        var read = await db.Store.GetFetchStateAsync(Spirited, None);

        Assert.NotNull(read);
        Assert.Equal(state.ProviderIds, read.ProviderIds);
        Assert.Equal((state.LastFetch, state.Status, state.LastError), (read.LastFetch, read.Status, read.LastError));
        Assert.Null(await db.Store.GetFetchStateAsync(Other, None));
    }

    [Fact]
    public void Provider_id_snapshots_are_order_independent()
    {
        var a = SqliteLocalizedMetadataStore.SerializeProviderIds(new Dictionary<string, string> { ["Tmdb"] = "1", ["AniList"] = "2" });
        var b = SqliteLocalizedMetadataStore.SerializeProviderIds(new Dictionary<string, string> { ["AniList"] = "2", ["Tmdb"] = "1" });

        Assert.Equal(a, b);
    }

    [Fact]
    public async Task Stale_items_are_the_never_fetched_and_the_old_oldest_first()
    {
        using var db = new TestDatabase();
        var now = db.Time.Now;
        var neverFetched = Guid.Parse("00000000000000000000000000000001");
        var old = Guid.Parse("00000000000000000000000000000002");
        var older = Guid.Parse("00000000000000000000000000000003");
        var fresh = Guid.Parse("00000000000000000000000000000004");
        var none = new Dictionary<string, string>();
        await db.Store.SetFetchStateAsync(new(neverFetched, none, null, FetchStatus.Pending), None);
        await db.Store.SetFetchStateAsync(new(old, none, now.AddDays(-8), FetchStatus.Ok), None);
        await db.Store.SetFetchStateAsync(new(older, none, now.AddDays(-30), FetchStatus.Ok), None);
        await db.Store.SetFetchStateAsync(new(fresh, none, now.AddHours(-1), FetchStatus.Ok), None);

        var stale = await db.Store.GetStaleItemIdsAsync(now.AddDays(-7), limit: 10, None);
        var firstTwo = await db.Store.GetStaleItemIdsAsync(now.AddDays(-7), limit: 2, None);

        Assert.Equal([neverFetched, older, old], stale);
        Assert.Equal([neverFetched, older], firstTwo);
    }

    [Fact]
    public async Task Replacing_provider_rows_drops_what_the_provider_no_longer_has()
    {
        using var db = new TestDatabase();
        await db.Store.UpsertFieldsAsync(
            [
                new(Spirited, "es-MX", MetadataFields.Name, "Vieja", MetadataSources.Tmdb),
                new(Spirited, "ja-JP", MetadataFields.Name, "古い", MetadataSources.Tmdb),
                new(Spirited, "es-MX", MetadataFields.Overview, "Curada", MetadataSources.Manual, Locked: true),
                new(Spirited, "en-US", MetadataFields.Name, "From TVDB", MetadataSources.Tvdb),
                new(Other, "ja-JP", MetadataFields.Name, "他", MetadataSources.Tmdb),
            ],
            None);

        var written = await db.Store.ReplaceProviderFieldsAsync(
            Spirited,
            MetadataSources.Tmdb,
            [new(Spirited, "es-MX", MetadataFields.Name, "Nueva", MetadataSources.Tmdb)],
            None);

        var rows = await db.Store.GetFieldsAsync([Spirited, Other], null, None);
        Assert.Equal(1, written);
        Assert.Equal(
            ["Curada", "From TVDB", "Nueva", "他"],
            rows.Select(r => r.Value).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Replacing_refuses_rows_of_another_item_source_or_locked()
    {
        using var db = new TestDatabase();

        await Assert.ThrowsAsync<ArgumentException>(() => db.Store.ReplaceProviderFieldsAsync(
            Spirited, MetadataSources.Tmdb, [new(Other, "es-MX", MetadataFields.Name, "x", MetadataSources.Tmdb)], None));
        await Assert.ThrowsAsync<ArgumentException>(() => db.Store.ReplaceProviderFieldsAsync(
            Spirited, MetadataSources.Tmdb, [new(Spirited, "es-MX", MetadataFields.Name, "x", MetadataSources.Tvdb)], None));
        await Assert.ThrowsAsync<ArgumentException>(() => db.Store.ReplaceProviderFieldsAsync(
            Spirited, MetadataSources.Tmdb, [new(Spirited, "es-MX", MetadataFields.Name, "x", MetadataSources.Tmdb, Locked: true)], None));
    }

    [Fact]
    public async Task Replacing_several_providers_swaps_their_text_and_images_together()
    {
        using var db = new TestDatabase();
        await db.Store.UpsertFieldsAsync(
            [
                new(Spirited, "es-MX", MetadataFields.Name, "Vieja", MetadataSources.Tmdb),
                new(Spirited, "en-US", MetadataFields.Name, "Old TVDB", MetadataSources.Tvdb),
                new(Spirited, "ja-JP", MetadataFields.Name, "Locked", MetadataSources.Tvdb, Locked: true),
                new(Spirited, "ja-Latn", MetadataFields.Name, "Romaji", MetadataSources.AniList),
                new(Other, "es-MX", MetadataFields.Name, "Otra", MetadataSources.Tmdb),
            ],
            None);
        await db.Store.UpsertImagesAsync([new(Spirited, "es-MX", LocalizedImageTypes.Primary, "https://x/old.jpg", MetadataSources.Tmdb)], None);

        var written = await db.Store.ReplaceProvidersAsync(
            Spirited,
            [MetadataSources.Tmdb, MetadataSources.Tvdb],
            [
                new(Spirited, "es-MX", MetadataFields.Name, "Nueva", MetadataSources.Tvdb),
                new(Spirited, "ja-JP", MetadataFields.Name, "Skipped", MetadataSources.Tmdb), // the locked row stays
            ],
            [new(Spirited, "es-MX", LocalizedImageTypes.Primary, "https://x/tvdb.jpg", MetadataSources.Tvdb, Width: 680, Height: 1000)],
            None);

        Assert.Equal(2, written);
        var rows = await db.Store.GetFieldsAsync([Spirited, Other], null, None);
        Assert.Equal(
            ["Locked tvdb", "Nueva tvdb", "Otra tmdb", "Romaji anilist"],
            rows.Select(r => $"{r.Value} {r.Source}").Order(StringComparer.Ordinal));
        var image = Assert.Single(await db.Store.GetImagesAsync([Spirited], null, None));
        Assert.Equal(("https://x/tvdb.jpg", MetadataSources.Tvdb, 680), (image.Url, image.Source, image.Width!.Value));
    }

    [Fact]
    public async Task Replacing_several_providers_refuses_rows_of_other_sources_or_locked()
    {
        using var db = new TestDatabase();
        string[] main = [MetadataSources.Tmdb, MetadataSources.Tvdb];

        await Assert.ThrowsAsync<ArgumentException>(() => db.Store.ReplaceProvidersAsync(
            Spirited, main, [new(Spirited, "es-MX", MetadataFields.Name, "x", MetadataSources.Manual)], [], None));
        await Assert.ThrowsAsync<ArgumentException>(() => db.Store.ReplaceProvidersAsync(
            Spirited, main, [], [new(Spirited, "es-MX", LocalizedImageTypes.Primary, "https://x", MetadataSources.Tvdb, Locked: true)], None));
        await Assert.ThrowsAsync<ArgumentException>(() => db.Store.ReplaceProvidersAsync(Spirited, [], [], [], None));
    }

    [Fact]
    public async Task Image_sizes_round_trip_and_provider_images_are_replaced()
    {
        using var db = new TestDatabase();
        await db.Store.UpsertImagesAsync(
            [
                new(Spirited, "es-MX", LocalizedImageTypes.Primary, "https://x/old.jpg", MetadataSources.Tmdb, Width: 1000, Height: 1500),
                new(Spirited, "ja-JP", LocalizedImageTypes.Logo, "https://x/old-logo.png", MetadataSources.Tmdb),
                new(Spirited, "es-MX", LocalizedImageTypes.Logo, "https://x/manual.png", MetadataSources.Manual, Locked: true),
            ],
            None);

        await db.Store.ReplaceProviderImagesAsync(
            Spirited,
            MetadataSources.Tmdb,
            [new(Spirited, "es-MX", LocalizedImageTypes.Primary, "https://x/new.jpg", MetadataSources.Tmdb, Width: 2000, Height: 3000)],
            None);

        var images = await db.Store.GetImagesAsync([Spirited], null, None);
        Assert.Equal(["https://x/manual.png", "https://x/new.jpg"], images.Select(i => i.Url).Order(StringComparer.Ordinal));
        var poster = Assert.Single(images, i => i.ImageType == LocalizedImageTypes.Primary);
        Assert.Equal((2000, 3000), (poster.Width!.Value, poster.Height!.Value));
        Assert.Null(Assert.Single(images, i => i.Locked).Width);
    }

    [Fact]
    public async Task Known_ids_can_require_a_fetch_version()
    {
        using var db = new TestDatabase();
        var none = new Dictionary<string, string>();
        await db.Store.SetFetchStateAsync(new(Spirited, none, db.Time.Now, FetchStatus.Ok, FetchVersion: 1), None);
        await db.Store.SetFetchStateAsync(new(Other, none, db.Time.Now, FetchStatus.Ok, FetchVersion: 2), None);

        Assert.Equal(2, (await db.Store.GetKnownItemIdsAsync([Spirited, Other], None)).Count);
        Assert.Equal([Other], await db.Store.GetKnownItemIdsAsync([Spirited, Other], None, minFetchVersion: 2));
        Assert.Equal(2, (await db.Store.GetFetchStateAsync(Other, None))!.FetchVersion);
    }

    [Fact]
    public async Task Known_ids_are_those_with_a_fetch_state()
    {
        using var db = new TestDatabase();
        await db.Store.SetFetchStateAsync(new(Spirited, new Dictionary<string, string>(), null, FetchStatus.Pending), None);

        var known = await db.Store.GetKnownItemIdsAsync([Spirited, Other], None);

        Assert.Equal([Spirited], known);
        Assert.Empty(await db.Store.GetKnownItemIdsAsync([], None));
    }

    [Fact]
    public async Task Skipped_and_error_states_round_trip()
    {
        using var db = new TestDatabase();
        await db.Store.SetFetchStateAsync(new(Spirited, new Dictionary<string, string>(), db.Time.Now, FetchStatus.Skipped, "No TMDB id"), None);

        Assert.Equal(FetchStatus.Skipped, (await db.Store.GetFetchStateAsync(Spirited, None))!.Status);
    }

    [Fact]
    public async Task Deleting_an_item_removes_all_its_rows()
    {
        using var db = new TestDatabase();
        await db.Store.UpsertFieldsAsync([new(Spirited, "es-MX", MetadataFields.Name, "x", MetadataSources.Tmdb), new(Other, "es-MX", MetadataFields.Name, "y", MetadataSources.Tmdb)], None);
        await db.Store.UpsertImagesAsync([new(Spirited, "es-MX", LocalizedImageTypes.Primary, "u", MetadataSources.Tmdb)], None);
        await db.Store.SetFetchStateAsync(new(Spirited, new Dictionary<string, string>(), db.Time.Now, FetchStatus.Ok), None);

        await db.Store.DeleteItemAsync(Spirited, None);

        Assert.Empty(await db.Store.GetFieldsAsync([Spirited], null, None));
        Assert.Empty(await db.Store.GetImagesAsync([Spirited], null, None));
        Assert.Null(await db.Store.GetFetchStateAsync(Spirited, None));
        Assert.Single(await db.Store.GetFieldsAsync([Other], null, None));
    }
}
