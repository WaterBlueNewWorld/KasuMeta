using Jellyfin.Plugin.KasuMeta.Api;
using Jellyfin.Plugin.KasuMeta.Api.Models;
using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Library;
using Jellyfin.Plugin.KasuMeta.Storage;
using Jellyfin.Plugin.KasuMeta.Tests.Storage;
using Jellyfin.Plugin.KasuMeta.Tests.Support;

namespace Jellyfin.Plugin.KasuMeta.Tests.Api;

/// <summary>Finding items by a genre or tag as an overlay shows it (<c>GET /KasuMeta/Items/ByTerm</c>).</summary>
public sealed class TermItemsServiceTests : IDisposable
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly IReadOnlySet<ItemKind> MoviesAndSeries = new HashSet<ItemKind> { ItemKind.Movie, ItemKind.Series };
    private static readonly Guid User = Guid.Parse("5eb3da7c3bf744ffa98cb4c5c3011705");
    private static readonly Guid Cabin = Guid.Parse("22970000000000000000000000000000");
    private static readonly Guid Saw = Guid.Parse("00000000000000000000000000000a01");
    private static readonly Guid Comedy = Guid.Parse("00000000000000000000000000000a02");
    private static readonly Guid Hidden = Guid.Parse("00000000000000000000000000000a03");
    private static readonly Guid Revue = Guid.Parse("80834000000000000000000000000000");
    private static readonly Guid RevueEpisode = Guid.Parse("80834000000000000000000000000101");

    private readonly TestDatabase _db = new();
    private readonly FakeLibrary _library = new();
    private readonly FakeConfig _config = new(new PluginConfiguration
    {
        EnabledLocales = ["en-US", "es-ES", "es-MX", "ja-JP"],
        FallbackToBaseLanguage = true,
        FallbackToOriginalLanguage = true,
    });

    private readonly TermDictionary _terms;
    private readonly TermItemsService _service;

    public TermItemsServiceTests()
    {
        _terms = new TermDictionary(_db.Store);
        _service = new TermItemsService(_db.Store, _library, _config, _terms);
        _library.Add(FakeLibrary.Movie(Cabin, "22970") with { Name = "The Cabin in the Woods" });
        _library.Add(FakeLibrary.Movie(Saw, "1") with { Name = "Saw" });
        _library.Add(FakeLibrary.Movie(Comedy, "2") with { Name = "Airplane!" });
        _library.Add(FakeLibrary.Movie(Hidden, "3") with { Name = "Not for everyone" });
        _library.Add(FakeLibrary.Series(Revue, "80834") with { Name = "Revue Starlight" });
        _library.Add(FakeLibrary.Episode(RevueEpisode, "80834", 1, 1) with { Name = "Stage Girls" });
        _library.VisibleTo[User] = [Cabin, Saw, Comedy, Revue, RevueEpisode];
        _db.Store.UpsertFieldsAsync(
            [
                Genres(Cabin, "en-US", "Horror", "Mystery"),
                Genres(Cabin, "es-MX", "Terror", "Misterio"),
                Tags(Cabin, "en", "abandoned house", "woods"),

                // Spanish (Spain) only: a Mexican user gets it through the base-language fallback.
                Genres(Saw, "en-US", "Horror"),
                Genres(Saw, "es-ES", "Terror"),
                Genres(Comedy, "en-US", "Comedy"),
                Genres(Comedy, "es-MX", "Comedia"),
                Genres(Hidden, "es-MX", "Terror"),

                // English only: the dictionary, or the original language, is all a Spanish user gets.
                Genres(Revue, "en-US", "Animation", "Sci-Fi & Fantasy"),
                Tags(Revue, "en", "magical girl", "woods"),
                Tags(RevueEpisode, "en", "woods"),
            ],
            None).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _terms.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task A_genre_is_found_by_the_name_the_callers_locale_shows()
    {
        var response = await FindAsync(TermKinds.Genre, "Terror", "es-MX");

        Assert.Equal((TermKinds.Genre, "Terror", "es-MX", 2, 0), (response.Kind, response.Term, response.Locale, response.Total, response.StartIndex));

        // By Jellyfin's name for the item; Saw has it from es-ES (base-language fallback).
        Assert.Equal(
            [(Saw.ToString("N"), "Movie"), (Cabin.ToString("N"), "Movie")],
            response.Results.Select(r => (r.ItemId, r.Type)));
    }

    [Theory]
    [InlineData("terror")]
    [InlineData("TERROR")]
    [InlineData("Térrór")]
    public async Task Case_and_accents_do_not_matter(string term)
    {
        Assert.Equal(2, (await FindAsync(TermKinds.Genre, term, "es-MX")).Total);
    }

    [Fact]
    public async Task A_name_from_another_language_is_not_what_the_caller_is_shown()
    {
        // A Mexican user is shown "Terror", so "Horror" leads nowhere; an English one is shown "Horror".
        Assert.Equal(0, (await FindAsync(TermKinds.Genre, "Horror", "es-MX")).Total);
        Assert.Equal(2, (await FindAsync(TermKinds.Genre, "Horror", "en-US")).Total);
        Assert.Equal(0, (await FindAsync(TermKinds.Genre, "Terror", "en-US")).Total);
    }

    [Fact]
    public async Task Without_base_language_fallback_only_the_exact_locale_counts()
    {
        _config.Current.FallbackToBaseLanguage = false;

        var result = Assert.Single((await FindAsync(TermKinds.Genre, "Terror", "es-MX")).Results);

        Assert.Equal(Cabin.ToString("N"), result.ItemId);
    }

    [Fact]
    public async Task A_term_the_dictionary_translates_is_found_by_its_translation()
    {
        await _terms.SetAsync(TermKinds.Tag, "es-MX", new Dictionary<string, string?> { ["abandoned house"] = "casa abandonada" }, None);

        var translated = await FindAsync(TermKinds.Tag, "casa abandonada", "es-MX");
        var english = await FindAsync(TermKinds.Tag, "abandoned house", "es-MX");
        var untranslated = await FindAsync(TermKinds.Tag, "woods", "es-MX");

        // The Cabin's tags are served as ["casa abandonada", "woods"]: the English name is gone,
        // and the tag the dictionary lacks stays as it is.
        Assert.Equal(Cabin.ToString("N"), Assert.Single(translated.Results).ItemId);
        Assert.Equal(0, english.Total);
        Assert.Equal(Cabin.ToString("N"), Assert.Single(untranslated.Results).ItemId);
    }

    [Fact]
    public async Task The_original_language_stands_in_when_the_locale_has_nothing()
    {
        Assert.Equal(0, (await FindAsync(TermKinds.Tag, "magical girl", "es-MX")).Total);

        await _db.Store.SetFetchStateAsync(
            new FetchState(Revue, new Dictionary<string, string>(), _db.Time.GetUtcNow(), FetchStatus.Ok, Kind: ItemKind.Series, OriginalLanguage: "en"),
            None);

        Assert.Equal(Revue.ToString("N"), Assert.Single((await FindAsync(TermKinds.Tag, "magical girl", "es-MX")).Results).ItemId);

        _config.Current.FallbackToOriginalLanguage = false;
        Assert.Equal(0, (await FindAsync(TermKinds.Tag, "magical girl", "es-MX")).Total);
    }

    [Fact]
    public async Task Hidden_items_are_neither_listed_nor_counted()
    {
        var asUser = await FindAsync(TermKinds.Genre, "Terror", "es-MX", user: User);
        var asApiKey = await FindAsync(TermKinds.Genre, "Terror", "es-MX", user: null);
        var stranger = await FindAsync(TermKinds.Genre, "Terror", "es-MX", user: Guid.NewGuid());

        Assert.Equal(2, asUser.Total);
        Assert.DoesNotContain(asUser.Results, r => r.ItemId == Hidden.ToString("N"));
        Assert.Equal(3, asApiKey.Total);
        Assert.Equal((0, 0), (stranger.Total, stranger.Results.Count));
    }

    [Fact]
    public async Task Only_the_kinds_asked_for_are_returned()
    {
        var tags = new HashSet<ItemKind> { ItemKind.Movie, ItemKind.Series, ItemKind.Episode };
        await _db.Store.SetFetchStateAsync(
            new FetchState(Revue, new Dictionary<string, string>(), _db.Time.GetUtcNow(), FetchStatus.Ok, Kind: ItemKind.Series, OriginalLanguage: "en"),
            None);
        await _db.Store.SetFetchStateAsync(
            new FetchState(RevueEpisode, new Dictionary<string, string>(), _db.Time.GetUtcNow(), FetchStatus.Ok, Kind: ItemKind.Episode, OriginalLanguage: "en"),
            None);

        var series = await FindAsync(TermKinds.Tag, "woods", "en-US", kinds: new HashSet<ItemKind> { ItemKind.Series });
        var all = await FindAsync(TermKinds.Tag, "woods", "en-US", kinds: tags);

        Assert.Equal((Revue.ToString("N"), "Series"), (Assert.Single(series.Results).ItemId, series.Results[0].Type));
        Assert.Equal(["Series", "Episode", "Movie"], all.Results.Select(r => r.Type));
    }

    [Fact]
    public async Task Results_come_a_page_at_a_time_with_the_whole_count()
    {
        var first = await FindAsync(TermKinds.Genre, "Terror", "es-MX", startIndex: 0, limit: 1);
        var second = await FindAsync(TermKinds.Genre, "Terror", "es-MX", startIndex: 1, limit: 1);
        var past = await FindAsync(TermKinds.Genre, "Terror", "es-MX", startIndex: 5, limit: 1);

        Assert.Equal((2, 0, Saw.ToString("N")), (first.Total, first.StartIndex, Assert.Single(first.Results).ItemId));
        Assert.Equal((2, 1, Cabin.ToString("N")), (second.Total, second.StartIndex, Assert.Single(second.Results).ItemId));
        Assert.Equal((2, 0), (past.Total, past.Results.Count));
    }

    [Fact]
    public async Task What_is_found_is_what_an_overlay_shows()
    {
        await _terms.SetAsync(TermKinds.Genre, "es-MX", new Dictionary<string, string?> { ["Animation"] = "Animación" }, None);
        var overlays = new OverlayService(_db.Store, _config, _terms);

        var batch = await overlays.BuildAsync(
            [Cabin, Saw, Comedy, Revue],
            [Cabin, Saw, Comedy, Revue],
            "es-MX",
            new HashSet<string> { MetadataFields.Genres },
            new HashSet<string>(),
            None);

        // Every genre a page would draw leads back to the item that draws it.
        foreach (var (id, overlay) in batch.Items)
        {
            foreach (var genre in overlay.Genres!.Values)
            {
                var found = await FindAsync(TermKinds.Genre, genre, "es-MX");
                Assert.Contains(found.Results, r => r.ItemId == id);
            }
        }

        Assert.Equal(["Animación", "Sci-Fi & Fantasy"], batch.Items[Revue.ToString("N")].Genres!.Values);
    }

    private static LocalizedField Genres(Guid id, string locale, params string[] values) =>
        new(id, locale, MetadataFields.Genres, ListValues.Serialize(values)!, MetadataSources.Tmdb);

    private static LocalizedField Tags(Guid id, string locale, params string[] values) =>
        new(id, locale, MetadataFields.Tags, ListValues.Serialize(values)!, MetadataSources.Tmdb);

    private Task<TermItemsResponse> FindAsync(
        string kind,
        string term,
        string locale,
        IReadOnlySet<ItemKind>? kinds = null,
        int startIndex = 0,
        int limit = 100) =>
        FindAsync(kind, term, locale, User, kinds, startIndex, limit);

    private Task<TermItemsResponse> FindAsync(
        string kind,
        string term,
        string locale,
        Guid? user,
        IReadOnlySet<ItemKind>? kinds = null,
        int startIndex = 0,
        int limit = 100) =>
        _service.FindAsync(kind, term, locale, kinds ?? MoviesAndSeries, startIndex, limit, user, None);
}
