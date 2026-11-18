using Jellyfin.Plugin.KasuMeta.Api;
using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Storage;
using Jellyfin.Plugin.KasuMeta.Tests.Storage;
using Jellyfin.Plugin.KasuMeta.Tests.Support;

namespace Jellyfin.Plugin.KasuMeta.Tests.Api;

public sealed class OverlayServiceTests : IDisposable
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Guid Magia = Guid.Parse("94523000000000000000000000000000");
    private static readonly Guid Hidden = Guid.Parse("00000000000000000000000000000bad");
    private static readonly Guid NotFetched = Guid.Parse("0000000000000000000000000000f00d");

    private readonly TestDatabase _db = new();
    private readonly OverlayService _service;
    private readonly FakeConfig _config =
        new(new PluginConfiguration { EnabledLocales = ["en-US", "es-MX", "ja-JP"], FallbackToBaseLanguage = true });

    public OverlayServiceTests()
    {
        _service = new OverlayService(
            _db.Store,
            _config,
            new TermDictionary(_db.Store));
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Each_field_falls_back_on_its_own()
    {
        // As TMDB has Magia Record: es-MX title but no es-MX overview; es-ES has both.
        await SeedMagiaAsync();

        var batch = await _service.BuildAsync([Magia], [Magia], "es-MX", MetadataFields.All, LocalizedImageTypes.All, None);

        var overlay = Assert.Single(batch.Items).Value;
        Assert.Equal(("Magia Record (MX)", "es-MX"), (overlay.Name!.Value, overlay.Name.ResolvedLocale));
        Assert.Equal(("Sinopsis de España", "es-ES"), (overlay.Overview!.Value, overlay.Overview.ResolvedLocale));
        Assert.Null(overlay.Tagline);
        Assert.Equal(MetadataSources.Tmdb, overlay.Name.Source);
    }

    [Fact]
    public async Task Hidden_unknown_and_unfetched_items_are_all_just_missing()
    {
        await SeedMagiaAsync();
        await _db.Store.UpsertFieldsAsync([new(Hidden, "es-MX", MetadataFields.Name, "Secreto", MetadataSources.Tmdb)], None);

        var batch = await _service.BuildAsync([Magia, Hidden, NotFetched], visibleIds: [Magia, NotFetched], "es-MX", MetadataFields.All, LocalizedImageTypes.All, None);

        Assert.Equal([Key(Magia)], batch.Items.Keys);
        Assert.Equal([Key(Hidden), Key(NotFetched)], batch.Missing);
        Assert.Equal("es-MX", batch.Locale);
    }

    [Fact]
    public async Task No_translation_in_the_language_is_missing()
    {
        await SeedMagiaAsync();

        var batch = await _service.BuildAsync([Magia], [Magia], "pt-BR", MetadataFields.All, LocalizedImageTypes.All, None);

        Assert.Empty(batch.Items);
        Assert.Equal([Key(Magia)], batch.Missing);
    }

    [Fact]
    public async Task Only_the_asked_fields_are_served()
    {
        await SeedMagiaAsync();

        var batch = await _service.BuildAsync([Magia], [Magia], "es-MX", new HashSet<string> { MetadataFields.Name }, new HashSet<string>(), None);

        var overlay = Assert.Single(batch.Items).Value;
        Assert.NotNull(overlay.Name);
        Assert.Null(overlay.Overview);
    }

    [Fact]
    public async Task UpdatedAt_is_the_newest_value_served()
    {
        await _db.Store.UpsertFieldsAsync([new(Magia, "ja-JP", MetadataFields.Name, "マギアレコード", MetadataSources.Tmdb)], None);
        var later = _db.Time.Now.AddHours(3);
        _db.Time.Now = later;
        await _db.Store.UpsertFieldsAsync([new(Magia, "ja-JP", MetadataFields.Overview, "あらすじ", MetadataSources.Tmdb)], None);

        var batch = await _service.BuildAsync([Magia], [Magia], "ja-JP", MetadataFields.All, LocalizedImageTypes.All, None);

        Assert.Equal(later, batch.Items[Key(Magia)].UpdatedAt);
    }

    [Fact]
    public async Task Duplicate_ids_are_answered_once()
    {
        await SeedMagiaAsync();

        var batch = await _service.BuildAsync([Magia, Magia], [Magia], "es-MX", MetadataFields.All, LocalizedImageTypes.All, None);

        Assert.Single(batch.Items);
        Assert.Empty(batch.Missing);
    }

    [Fact]
    public async Task Each_image_type_falls_back_on_its_own()
    {
        await _db.Store.UpsertImagesAsync(
            [
                new(Magia, "ja-JP", LocalizedImageTypes.Primary, "https://image.tmdb.org/t/p/original/ja.jpg", MetadataSources.Tmdb, Width: 2000, Height: 3000),
                new(Magia, "es-ES", LocalizedImageTypes.Logo, "https://image.tmdb.org/t/p/original/es-logo.png", MetadataSources.Tmdb, Width: 558, Height: 92),
                new(Magia, "es-MX", LocalizedImageTypes.Primary, "https://image.tmdb.org/t/p/original/mx.jpg", MetadataSources.Tmdb, Width: 1000, Height: 1500),
            ],
            None);

        var batch = await _service.BuildAsync([Magia], [Magia], "es-MX", MetadataFields.All, LocalizedImageTypes.All, None);

        var images = batch.Items[Key(Magia)].Images!;
        Assert.Equal(("https://image.tmdb.org/t/p/original/mx.jpg", "es-MX", 1000, 1500), (images["Primary"].Url, images["Primary"].ResolvedLocale, images["Primary"].Width, images["Primary"].Height));
        Assert.Equal("es-ES", images["Logo"].ResolvedLocale); // no es-MX logo
        Assert.False(images.ContainsKey("Backdrop"));
    }

    [Fact]
    public async Task An_item_with_only_images_is_not_missing_and_images_can_be_left_out()
    {
        await _db.Store.UpsertImagesAsync(
            [new(NotFetched, "ja-JP", LocalizedImageTypes.Primary, "https://image.tmdb.org/t/p/original/p.jpg", MetadataSources.Tmdb)],
            None);

        var withImages = await _service.BuildAsync([NotFetched], [NotFetched], "ja-JP", MetadataFields.All, LocalizedImageTypes.All, None);
        var textOnly = await _service.BuildAsync([NotFetched], [NotFetched], "ja-JP", MetadataFields.All, new HashSet<string>(), None);

        Assert.Null(withImages.Items[Key(NotFetched)].Name);
        Assert.NotNull(withImages.Items[Key(NotFetched)].Images);
        Assert.Equal([Key(NotFetched)], textOnly.Missing);
    }

    private static string Key(Guid id) => id.ToString("N");

    private Task<int> SeedMagiaAsync() =>
        _db.Store.UpsertFieldsAsync(
            [
                new(Magia, "es-MX", MetadataFields.Name, "Magia Record (MX)", MetadataSources.Tmdb),
                new(Magia, "es-ES", MetadataFields.Name, "Magia Record (ES)", MetadataSources.Tmdb),
                new(Magia, "es-ES", MetadataFields.Overview, "Sinopsis de España", MetadataSources.Tmdb),
                new(Magia, "ja-JP", MetadataFields.Name, "マギアレコード", MetadataSources.Tmdb),
            ],
            None);
}
