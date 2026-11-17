using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Fetching;
using Jellyfin.Plugin.KasuMeta.Library;
using Jellyfin.Plugin.KasuMeta.Providers;
using Jellyfin.Plugin.KasuMeta.Providers.Tmdb;
using Jellyfin.Plugin.KasuMeta.Storage;
using Jellyfin.Plugin.KasuMeta.Tests.Storage;
using Jellyfin.Plugin.KasuMeta.Tests.Support;

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
    private readonly FakeTmdb _tmdb = new();
    private readonly FakeConfig _config = new(new PluginConfiguration { EnabledLocales = ["en-US", "es-MX", "ja-JP"] });
    private readonly MetadataFetcher _fetcher;

    public MetadataFetcherTests()
    {
        _fetcher = new MetadataFetcher(_library, _db.Store, _tmdb, _config, _db.Time);
        _tmdb.Answer = _ => Antitrust_;
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
}
