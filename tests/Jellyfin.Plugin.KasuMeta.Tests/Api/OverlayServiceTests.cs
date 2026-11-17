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

    public OverlayServiceTests()
    {
        _service = new OverlayService(
            _db.Store,
            new FakeConfig(new PluginConfiguration { EnabledLocales = ["en-US", "es-MX", "ja-JP"], FallbackToBaseLanguage = true }));
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Each_field_falls_back_on_its_own()
    {
        // As TMDB has Magia Record: es-MX title but no es-MX overview; es-ES has both.
        await SeedMagiaAsync();

        var batch = await _service.BuildAsync([Magia], [Magia], "es-MX", MetadataFields.All, None);

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

        var batch = await _service.BuildAsync([Magia, Hidden, NotFetched], visibleIds: [Magia, NotFetched], "es-MX", MetadataFields.All, None);

        Assert.Equal([Key(Magia)], batch.Items.Keys);
        Assert.Equal([Key(Hidden), Key(NotFetched)], batch.Missing);
        Assert.Equal("es-MX", batch.Locale);
    }

    [Fact]
    public async Task No_translation_in_the_language_is_missing()
    {
        await SeedMagiaAsync();

        var batch = await _service.BuildAsync([Magia], [Magia], "pt-BR", MetadataFields.All, None);

        Assert.Empty(batch.Items);
        Assert.Equal([Key(Magia)], batch.Missing);
    }

    [Fact]
    public async Task Only_the_asked_fields_are_served()
    {
        await SeedMagiaAsync();

        var batch = await _service.BuildAsync([Magia], [Magia], "es-MX", new HashSet<string> { MetadataFields.Name }, None);

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

        var batch = await _service.BuildAsync([Magia], [Magia], "ja-JP", MetadataFields.All, None);

        Assert.Equal(later, batch.Items[Key(Magia)].UpdatedAt);
    }

    [Fact]
    public async Task Duplicate_ids_are_answered_once()
    {
        await SeedMagiaAsync();

        var batch = await _service.BuildAsync([Magia, Magia], [Magia], "es-MX", MetadataFields.All, None);

        Assert.Single(batch.Items);
        Assert.Empty(batch.Missing);
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
