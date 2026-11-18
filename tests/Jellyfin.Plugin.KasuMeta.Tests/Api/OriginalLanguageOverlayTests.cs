using Jellyfin.Plugin.KasuMeta.Api;
using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Library;
using Jellyfin.Plugin.KasuMeta.Storage;
using Jellyfin.Plugin.KasuMeta.Tests.Storage;
using Jellyfin.Plugin.KasuMeta.Tests.Support;

namespace Jellyfin.Plugin.KasuMeta.Tests.Api;

/// <summary>The serving order per field: target locale, dictionary (lists), original language, nothing.</summary>
public sealed class OriginalLanguageOverlayTests : IDisposable
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Guid Id = Guid.Parse("55555555555555555555555555555555");

    private readonly TestDatabase _db = new();
    private readonly FakeConfig _config = new(new PluginConfiguration
    {
        EnabledLocales = ["es-MX", "es-ES"],
        FallbackToBaseLanguage = true,
        FallbackToOriginalLanguage = true,
    });

    private readonly TermDictionary _terms;
    private readonly OverlayService _service;

    public OriginalLanguageOverlayTests()
    {
        _terms = new TermDictionary(_db.Store);
        _service = new OverlayService(_db.Store, _config, _terms);
    }

    public void Dispose()
    {
        _terms.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task The_original_language_fills_only_what_no_spanish_locale_has()
    {
        await Seed(
            "ja",
            new LocalizedField(Id, "es-ES", MetadataFields.Name, "Frieren", MetadataSources.Tmdb),
            new LocalizedField(Id, "ja-JP", MetadataFields.Name, "葬送のフリーレン", MetadataSources.Tmdb),
            new LocalizedField(Id, "ja-JP", MetadataFields.Overview, "魔王を倒した…", MetadataSources.Tmdb));

        var overlay = await Overlay("es-MX");

        Assert.Equal(("Frieren", "es-ES"), (overlay!.Name!.Value, overlay.Name.ResolvedLocale)); // sibling first
        Assert.Equal(("魔王を倒した…", "ja-JP"), (overlay.Overview!.Value, overlay.Overview.ResolvedLocale));
    }

    [Fact]
    public async Task The_original_language_applies_even_without_base_language_fallback()
    {
        _config.Current.FallbackToBaseLanguage = false;
        await Seed(
            "ja",
            new LocalizedField(Id, "es-ES", MetadataFields.Name, "Frieren", MetadataSources.Tmdb),
            new LocalizedField(Id, "ja-JP", MetadataFields.Name, "葬送のフリーレン", MetadataSources.Tmdb));

        var overlay = await Overlay("es-MX");

        Assert.Equal("ja-JP", overlay!.Name!.ResolvedLocale); // es-ES isn't allowed, Japanese is
    }

    [Fact]
    public async Task With_the_setting_off_or_no_original_language_nothing_is_served()
    {
        await Seed("ja", new LocalizedField(Id, "ja-JP", MetadataFields.Name, "葬送のフリーレン", MetadataSources.Tmdb));
        _config.Current.FallbackToOriginalLanguage = false;
        Assert.Null(await Overlay("es-MX"));

        _config.Current.FallbackToOriginalLanguage = true;
        await _db.Store.SetFetchStateAsync(new FetchState(Id, new Dictionary<string, string>(), _db.Time.GetUtcNow(), FetchStatus.Ok), None);
        Assert.Null(await Overlay("es-MX"));
    }

    [Fact]
    public async Task Images_fall_back_to_the_original_language_too()
    {
        await Seed("ja");
        await _db.Store.UpsertImagesAsync([new LocalizedImage(Id, "ja-JP", "Logo", "https://x/ja.png", MetadataSources.Tmdb)], None);

        var overlay = await Overlay("es-MX");

        Assert.Equal("ja-JP", overlay!.Images!["Logo"].ResolvedLocale);
    }

    [Fact]
    public async Task Lists_go_target_then_dictionary_then_original()
    {
        await Seed(
            "ja",
            new LocalizedField(Id, "en", MetadataFields.Tags, ListValues.Serialize(["Elf", "Magic"])!, MetadataSources.Tvdb),
            new LocalizedField(Id, "ja-JP", MetadataFields.Tags, ListValues.Serialize(["エルフ"])!, MetadataSources.Manual),
            new LocalizedField(Id, "es-MX", MetadataFields.Genres, ListValues.Serialize(["Animación"])!, MetadataSources.Tmdb),
            new LocalizedField(Id, "en", MetadataFields.Genres, ListValues.Serialize(["Animation"])!, MetadataSources.Tmdb));

        // No translation yet: tags come in Japanese, genres in Mexican Spanish.
        var before = await Overlay("es-MX");
        Assert.Equal(["エルフ"], before!.Tags!.Values);
        Assert.Equal("ja-JP", before.Tags.ResolvedLocale);
        Assert.Equal(["Animación"], before.Genres!.Values);

        // One tag translated (for Spanish in general): the dictionary list wins over Japanese.
        await _terms.SetAsync(TermKinds.Tag, "es", new Dictionary<string, string?> { ["elf"] = "Elfa" }, None);
        var after = await Overlay("es-MX");
        Assert.Equal(["Elfa", "Magic"], after!.Tags!.Values);
        Assert.Equal((MetadataSources.Dictionary, "es-MX"), (after.Tags.Source, after.Tags.ResolvedLocale));
    }

    [Fact]
    public async Task The_dictionary_fills_english_names_a_provider_left_in_a_localized_list()
    {
        await Seed(
            "ja",
            new LocalizedField(Id, "es-MX", MetadataFields.Genres, ListValues.Serialize(["Animación", "Sci-Fi & Fantasy"])!, MetadataSources.Tmdb),
            new LocalizedField(Id, "es-MX", MetadataFields.Tags, ListValues.Serialize(["Sci-Fi & Fantasy"])!, MetadataSources.Manual));
        await _terms.SetAsync(TermKinds.Genre, "es", new Dictionary<string, string?> { ["sci-fi & fantasy"] = "Ciencia ficción y fantasía" }, None);
        await _terms.SetAsync(TermKinds.Tag, "es", new Dictionary<string, string?> { ["Sci-Fi & Fantasy"] = "Ciencia ficción y fantasía" }, None);

        var overlay = await Overlay("es-MX");

        Assert.Equal(["Animación", "Ciencia ficción y fantasía"], overlay!.Genres!.Values);
        Assert.Equal((MetadataSources.Dictionary, "es-MX"), (overlay.Genres.Source, overlay.Genres.ResolvedLocale));
        Assert.Equal(["Sci-Fi & Fantasy"], overlay.Tags!.Values); // an admin's list is left alone
    }

    [Fact]
    public async Task English_readers_get_the_base_terms_as_they_are()
    {
        await Seed("ja", new LocalizedField(Id, "en", MetadataFields.Tags, ListValues.Serialize(["Elf"])!, MetadataSources.Tvdb));

        var batch = await _service.BuildAsync([Id], [Id], "en-US", MetadataFields.All, LocalizedImageTypes.All, None);

        Assert.Equal(["Elf"], batch.Items.Values.Single().Tags!.Values);
    }

    [Fact]
    public async Task The_admin_view_shows_the_original_language_and_no_fallback()
    {
        await Seed("ja", new LocalizedField(Id, "ja-JP", MetadataFields.Name, "葬送のフリーレン", MetadataSources.Tmdb));

        var all = await _service.BuildAllLocalesAsync(Id, None);

        Assert.Equal("ja", all.OriginalLanguage);
        Assert.Equal(["ja-JP"], all.Locales.Keys);
    }

    private async Task Seed(string original, params LocalizedField[] fields)
    {
        await _db.Store.SetFetchStateAsync(
            new FetchState(Id, new Dictionary<string, string>(), _db.Time.GetUtcNow(), FetchStatus.Ok, Kind: ItemKind.Series, OriginalLanguage: original),
            None);
        if (fields.Length > 0)
        {
            await _db.Store.UpsertFieldsAsync(fields, None);
        }
    }

    private async Task<Jellyfin.Plugin.KasuMeta.Api.Models.ItemOverlay?> Overlay(string locale)
    {
        var batch = await _service.BuildAsync([Id], [Id], locale, MetadataFields.All, LocalizedImageTypes.All, None);
        return batch.Items.Values.SingleOrDefault();
    }
}
