using Jellyfin.Plugin.KasuMeta.Api;
using Jellyfin.Plugin.KasuMeta.Api.Models;
using Jellyfin.Plugin.KasuMeta.Library;
using Jellyfin.Plugin.KasuMeta.Search;
using Jellyfin.Plugin.KasuMeta.Storage;
using Jellyfin.Plugin.KasuMeta.Tests.Storage;
using Jellyfin.Plugin.KasuMeta.Tests.Support;

namespace Jellyfin.Plugin.KasuMeta.Tests.Api;

public sealed class SearchServiceTests : IDisposable
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly IReadOnlySet<ItemKind> Default = new HashSet<ItemKind> { ItemKind.Movie, ItemKind.Series, ItemKind.Episode };
    private static readonly Guid User = Guid.Parse("5eb3da7c3bf744ffa98cb4c5c3011705");
    private static readonly Guid Cabin = Guid.Parse("22970000000000000000000000000000");
    private static readonly Guid Wolf = Guid.Parse("00000000000000000000000000000a01");
    private static readonly Guid WolfChildren = Guid.Parse("00000000000000000000000000000a02");
    private static readonly Guid LoneWolf = Guid.Parse("00000000000000000000000000000a03");
    private static readonly Guid Revue = Guid.Parse("80834000000000000000000000000000");
    private static readonly Guid RevueSeason = Guid.Parse("80834000000000000000000000000001");
    private static readonly Guid RevueEpisode = Guid.Parse("80834000000000000000000000000101");

    private readonly TestDatabase _db = new();
    private readonly FakeLibrary _library = new();
    private readonly SearchService _service;

    public SearchServiceTests()
    {
        _service = new SearchService(_db.Store, _library);
        _library.Add(FakeLibrary.Movie(Cabin, "22970"));
        _library.Add(FakeLibrary.Movie(Wolf, "1"));
        _library.Add(FakeLibrary.Movie(WolfChildren, "2"));
        _library.Add(FakeLibrary.Movie(LoneWolf, "3"));
        _library.Add(FakeLibrary.Series(Revue, "80834"));
        _library.Add(FakeLibrary.Season(RevueSeason, "80834", 1));
        _library.Add(FakeLibrary.Episode(RevueEpisode, "80834", 1, 1));
        _library.VisibleTo[User] = [Cabin, Wolf, WolfChildren, LoneWolf, Revue, RevueSeason, RevueEpisode];
        _db.Store.UpsertFieldsAsync(
            [
                new(Cabin, "es-MX", MetadataFields.Name, "La cabaña del terror", MetadataSources.Tmdb),
                new(Cabin, "es-ES", MetadataFields.Name, "La cabaña en el bosque", MetadataSources.Tmdb),
                new(Cabin, "ja-JP", MetadataFields.Name, "キャビン", MetadataSources.Tmdb),
                new(Wolf, "en-US", MetadataFields.Name, "Wolf", MetadataSources.Tmdb),
                new(WolfChildren, "es-MX", MetadataFields.Name, "Wolf Children: los niños lobo", MetadataSources.Tmdb),
                new(WolfChildren, "en-US", MetadataFields.Name, "Wolf Children", MetadataSources.Tmdb),
                new(LoneWolf, "es-MX", MetadataFields.Name, "El lobo solitario: Lone Wolf", MetadataSources.Tmdb),
                new(Revue, "es-MX", MetadataFields.Name, "Shoujo Kageki Revue Starlight", MetadataSources.Tmdb),
                new(RevueSeason, "es-MX", MetadataFields.Name, "Revue Starlight: temporada 1", MetadataSources.Tmdb),
                new(RevueEpisode, "es-MX", MetadataFields.Name, "Chicas del escenario de Revue", MetadataSources.Tmdb),
            ],
            None).GetAwaiter().GetResult();
    }

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData("es-MX", "La cabaña del terror", "es-MX")]
    [InlineData("es-ES", "La cabaña en el bosque", "es-ES")]
    public async Task An_item_is_reported_once_under_the_callers_own_name(string locale, string name, string matchedLocale)
    {
        var response = await SearchAsync("cabana", locale);

        var result = Assert.Single(response.Results);
        Assert.Equal((Cabin.ToString("N"), "Movie", name, matchedLocale), (result.ItemId, result.Type, result.MatchedName, result.MatchedLocale));
        Assert.Equal(("cabana", locale), (response.Query, response.Locale));
    }

    [Fact]
    public async Task A_title_is_found_by_its_name_in_another_language()
    {
        var result = Assert.Single((await SearchAsync("キャビン", "es-MX")).Results);

        Assert.Equal((Cabin.ToString("N"), "キャビン", "ja-JP"), (result.ItemId, result.MatchedName, result.MatchedLocale));
    }

    [Fact]
    public async Task Exact_then_starts_with_then_contains_with_own_locale_breaking_ties()
    {
        var response = await SearchAsync("wolf", "es-MX");

        Assert.Equal(
            [
                "Wolf", // exact, though only in English
                "Wolf Children: los niños lobo", // starts with; shown in the caller's locale
                "El lobo solitario: Lone Wolf", // contains
            ],
            response.Results.Select(r => r.MatchedName));
    }

    [Fact]
    public async Task Hidden_items_are_left_out_and_dont_use_up_the_limit()
    {
        _library.VisibleTo[User] = [LoneWolf];

        var response = await SearchAsync("wolf", "es-MX", limit: 1);

        Assert.Equal(LoneWolf.ToString("N"), Assert.Single(response.Results).ItemId);
    }

    [Fact]
    public async Task An_unknown_user_sees_nothing_and_an_api_key_everything()
    {
        Assert.Empty((await _service.SearchAsync(SearchQuery.Parse("wolf"), "es-MX", 20, Default, Guid.NewGuid(), None)).Results);
        Assert.Equal(3, (await _service.SearchAsync(SearchQuery.Parse("wolf"), "es-MX", 20, Default, userId: null, None)).Results.Count);
    }

    [Fact]
    public async Task Seasons_are_left_out_unless_asked_for()
    {
        var byDefault = await SearchAsync("revue", "es-MX");
        var seasons = await _service.SearchAsync(
            SearchQuery.Parse("revue"), "es-MX", 20, new HashSet<ItemKind> { ItemKind.Season }, User, None);

        Assert.Equal(["Episode", "Series"], byDefault.Results.Select(r => r.Type).Order());
        Assert.Equal("Season", Assert.Single(seasons.Results).Type);
    }

    [Fact]
    public async Task A_series_comes_before_an_episode_named_after_it()
    {
        var none = new Dictionary<string, string>();
        await _db.Store.UpsertFieldsAsync(
            [
                new(RevueEpisode, "en-US", MetadataFields.Name, "Revue Starlight", MetadataSources.Tmdb),
                new(Revue, "en-US", MetadataFields.Name, "Revue Starlight", MetadataSources.Tmdb),
            ],
            None);
        await _db.Store.SetFetchStateAsync(new(RevueEpisode, none, _db.Time.Now, FetchStatus.Ok, Kind: ItemKind.Episode), None);
        await _db.Store.SetFetchStateAsync(new(Revue, none, _db.Time.Now, FetchStatus.Ok, Kind: ItemKind.Series), None);

        var response = await SearchAsync("revue starlight", "en-US");

        Assert.Equal(["Series", "Episode"], response.Results.Select(r => r.Type));
    }

    [Fact]
    public async Task The_library_has_the_last_word_on_an_items_kind()
    {
        // The store still says Movie (a stale state); Jellyfin now knows it as an episode.
        await _db.Store.SetFetchStateAsync(
            new(RevueEpisode, new Dictionary<string, string>(), _db.Time.Now, FetchStatus.Ok, Kind: ItemKind.Movie), None);

        var movies = await _service.SearchAsync(
            SearchQuery.Parse("chicas"), "es-MX", 20, new HashSet<ItemKind> { ItemKind.Movie }, User, None);

        Assert.Empty(movies.Results);
    }

    [Fact]
    public async Task The_limit_is_respected()
    {
        Assert.Equal(2, (await SearchAsync("wolf", "es-MX", limit: 2)).Results.Count);
    }

    [Fact]
    public async Task An_item_gone_from_the_library_is_not_reported()
    {
        _library.Items.Remove(Wolf);

        Assert.DoesNotContain((await SearchAsync("wolf", "es-MX")).Results, r => r.ItemId == Wolf.ToString("N"));
    }

    [Fact]
    public void Ranking_keeps_one_hit_per_item_preferring_locale_then_language()
    {
        var a = Guid.NewGuid();
        NameHit[] hits =
        [
            new(a, "en-US", "Antitrust", -3),
            new(a, "es-ES", "Conspiración en la red", -2),
            new(a, "es-MX", "Amenaza Virtual", -1),
        ];

        // "zzz" matches no name better than another, so only the locale preference decides.
        Assert.Equal("es-MX", Assert.Single(SearchService.Rank(hits, "zzz", "es-MX")).Locale);
        Assert.Equal("es-ES", Assert.Single(SearchService.Rank(hits, "zzz", "es-AR")).Locale); // same language, store order
        Assert.Equal("en-US", Assert.Single(SearchService.Rank(hits, "zzz", "ja-JP")).Locale); // store order

        // Between two names in the caller's language, the better match is shown.
        Assert.Equal("es-MX", Assert.Single(SearchService.Rank(hits, "amenaza", "es-AR")).Locale);
    }

    private Task<SearchResponse> SearchAsync(string text, string locale, int limit = 20) =>
        _service.SearchAsync(SearchQuery.Parse(text), locale, limit, Default, User, None);
}
