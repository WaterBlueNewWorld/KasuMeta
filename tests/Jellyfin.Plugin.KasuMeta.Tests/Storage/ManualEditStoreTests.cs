using Jellyfin.Plugin.KasuMeta.Library;
using Jellyfin.Plugin.KasuMeta.Search;
using Jellyfin.Plugin.KasuMeta.Storage;

namespace Jellyfin.Plugin.KasuMeta.Tests.Storage;

public class ManualEditStoreTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Guid Cabin = Guid.Parse("22970000000000000000000000000000");
    private static readonly Guid Other = Guid.Parse("0f1e2d3c4b5a69788796a5b4c3d2e1f0");
    private static readonly IReadOnlySet<ItemKind> AllKinds = Enum.GetValues<ItemKind>().ToHashSet();

    [Fact]
    public async Task A_manual_edit_replaces_a_locked_value()
    {
        using var db = new TestDatabase();
        await db.Store.UpsertFieldsAsync([new(Cabin, "es-MX", MetadataFields.Name, "Primera", MetadataSources.Manual, Locked: true)], None);
        db.Time.Now = db.Time.Now.AddDays(1);

        var changed = await db.Store.ApplyManualEditAsync(Edit(setFields: [Field("Segunda", locked: false)]), None);

        var row = Assert.Single(await db.Store.GetFieldsAsync([Cabin], null, None));
        Assert.Equal(1, changed);
        Assert.Equal(("Segunda", MetadataSources.Manual, false, db.Time.Now), (row.Value, row.Source, row.Locked, row.UpdatedAt));
    }

    [Fact]
    public async Task A_locked_manual_value_survives_a_provider_refresh()
    {
        using var db = new TestDatabase();
        await db.Store.ApplyManualEditAsync(Edit(setFields: [Field("Curado")]), None);

        await db.Store.ReplaceProviderFieldsAsync(
            Cabin,
            MetadataSources.Tmdb,
            [
                new(Cabin, "es-MX", MetadataFields.Name, "La cabaña del terror", MetadataSources.Tmdb),
                new(Cabin, "es-MX", MetadataFields.Overview, "Cinco amigos…", MetadataSources.Tmdb),
            ],
            None);

        var rows = await db.Store.GetFieldsAsync([Cabin], null, None);
        Assert.Equal("Curado", Assert.Single(rows, r => r.Field == MetadataFields.Name).Value);
        Assert.Equal("Cinco amigos…", Assert.Single(rows, r => r.Field == MetadataFields.Overview).Value);
    }

    [Fact]
    public async Task An_unlocked_manual_value_gives_way_to_the_next_provider_refresh()
    {
        using var db = new TestDatabase();
        await db.Store.ApplyManualEditAsync(Edit(setFields: [Field("Arreglo rápido", locked: false)]), None);

        await db.Store.ReplaceProviderFieldsAsync(
            Cabin,
            MetadataSources.Tmdb,
            [new(Cabin, "es-MX", MetadataFields.Name, "La cabaña del terror", MetadataSources.Tmdb)],
            None);

        var row = Assert.Single(await db.Store.GetFieldsAsync([Cabin], null, None));
        Assert.Equal(("La cabaña del terror", MetadataSources.Tmdb), (row.Value, row.Source));
    }

    [Fact]
    public async Task Removing_deletes_only_that_key_in_that_locale_whatever_its_source()
    {
        using var db = new TestDatabase();
        await db.Store.UpsertFieldsAsync(
            [
                new(Cabin, "es-MX", MetadataFields.Name, "La cabaña del terror", MetadataSources.Tmdb),
                new(Cabin, "es-MX", MetadataFields.Overview, "Cinco amigos…", MetadataSources.Manual, Locked: true),
                new(Cabin, "es-ES", MetadataFields.Name, "La cabaña en el bosque", MetadataSources.Tmdb),
                new(Other, "es-MX", MetadataFields.Name, "Otra", MetadataSources.Tmdb),
            ],
            None);
        await db.Store.UpsertImagesAsync(
            [
                new(Cabin, "es-MX", LocalizedImageTypes.Primary, "https://image.tmdb.org/t/p/original/mx.jpg", MetadataSources.Tmdb),
                new(Cabin, "es-MX", LocalizedImageTypes.Logo, "https://image.tmdb.org/t/p/original/mx.png", MetadataSources.Tmdb),
            ],
            None);

        var changed = await db.Store.ApplyManualEditAsync(
            Edit(removeFields: [MetadataFields.Name, MetadataFields.Overview], removeImages: [LocalizedImageTypes.Primary]),
            None);

        Assert.Equal(3, changed);
        Assert.Equal(
            ["es-ES name", "es-MX name"],
            (await db.Store.GetFieldsAsync([Cabin, Other], null, None)).Select(r => $"{r.Locale} {r.Field}").Order(StringComparer.Ordinal));
        Assert.Equal(LocalizedImageTypes.Logo, Assert.Single(await db.Store.GetImagesAsync([Cabin], null, None)).ImageType);
    }

    [Fact]
    public async Task Removing_what_isnt_stored_changes_nothing()
    {
        using var db = new TestDatabase();

        Assert.Equal(0, await db.Store.ApplyManualEditAsync(Edit(removeFields: [MetadataFields.Tagline]), None));
    }

    [Fact]
    public async Task Manual_images_are_written_without_a_size()
    {
        using var db = new TestDatabase();
        await db.Store.UpsertImagesAsync(
            [new(Cabin, "es-MX", LocalizedImageTypes.Primary, "https://image.tmdb.org/t/p/original/mx.jpg", MetadataSources.Tmdb, Width: 1000, Height: 1500)],
            None);

        await db.Store.ApplyManualEditAsync(
            Edit(setImages: [new(Cabin, "es-MX", LocalizedImageTypes.Primary, "https://example.org/poster.jpg", MetadataSources.Manual, Locked: true)]),
            None);

        var image = Assert.Single(await db.Store.GetImagesAsync([Cabin], null, None));
        Assert.Equal(("https://example.org/poster.jpg", MetadataSources.Manual, true, (int?)null, (int?)null), (image.Url, image.Source, image.Locked, image.Width, image.Height));
    }

    [Fact]
    public async Task A_manual_name_is_searchable_at_once_and_a_removed_one_is_gone()
    {
        using var db = new TestDatabase();

        await db.Store.ApplyManualEditAsync(Edit(setFields: [Field("Refugio maldito")]), None);
        var found = await db.Store.SearchNamesAsync(SearchQuery.Parse("refugio"), AllKinds, 50, None);
        await db.Store.ApplyManualEditAsync(Edit(removeFields: [MetadataFields.Name]), None);
        var gone = await db.Store.SearchNamesAsync(SearchQuery.Parse("refugio"), AllKinds, 50, None);

        Assert.Contains(found, h => h.ItemId == Cabin && h.Locale == "es-MX");
        Assert.Empty(gone);
    }

    [Fact]
    public async Task Rows_that_dont_belong_to_the_edit_are_refused()
    {
        using var db = new TestDatabase();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            db.Store.ApplyManualEditAsync(Edit(setFields: [new(Other, "es-MX", MetadataFields.Name, "x", MetadataSources.Manual)]), None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            db.Store.ApplyManualEditAsync(Edit(setFields: [new(Cabin, "ja-JP", MetadataFields.Name, "x", MetadataSources.Manual)]), None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            db.Store.ApplyManualEditAsync(Edit(setFields: [new(Cabin, "es-MX", MetadataFields.Name, "x", MetadataSources.Tmdb)]), None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            db.Store.ApplyManualEditAsync(Edit(removeFields: ["rating"]), None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            db.Store.ApplyManualEditAsync(Edit(removeImages: ["Poster"]), None));
        Assert.Empty(await db.Store.GetFieldsAsync([Cabin, Other], null, None));
    }

    [Fact]
    public async Task An_edit_is_all_or_nothing()
    {
        using var db = new TestDatabase();
        await db.Store.UpsertFieldsAsync([new(Cabin, "es-MX", MetadataFields.Overview, "Cinco amigos…", MetadataSources.Tmdb)], None);
        // The null value fails after the removal has run; the removal must roll back with it.
        var bad = Edit(
            setFields: [Field("Nuevo"), new(Cabin, "es-MX", MetadataFields.Tagline, null!, MetadataSources.Manual)],
            removeFields: [MetadataFields.Overview]);

        await Assert.ThrowsAnyAsync<Exception>(() => db.Store.ApplyManualEditAsync(bad, None));

        var row = Assert.Single(await db.Store.GetFieldsAsync([Cabin], null, None));
        Assert.Equal("Cinco amigos…", row.Value);
    }

    private static LocalizedField Field(string name, bool locked = true) =>
        new(Cabin, "es-MX", MetadataFields.Name, name, MetadataSources.Manual, locked);

    private static ManualEdit Edit(
        IReadOnlyCollection<LocalizedField>? setFields = null,
        IReadOnlyCollection<LocalizedImage>? setImages = null,
        IReadOnlyCollection<string>? removeFields = null,
        IReadOnlyCollection<string>? removeImages = null) =>
        new(Cabin, "es-MX", setFields ?? [], setImages ?? [], removeFields ?? [], removeImages ?? []);
}
