using Jellyfin.Plugin.KasuMeta.Api;
using Jellyfin.Plugin.KasuMeta.Api.Models;
using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Fetching;
using Jellyfin.Plugin.KasuMeta.Library;
using Jellyfin.Plugin.KasuMeta.Providers;
using Jellyfin.Plugin.KasuMeta.Storage;
using Jellyfin.Plugin.KasuMeta.Tests.Storage;
using Jellyfin.Plugin.KasuMeta.Tests.Support;

namespace Jellyfin.Plugin.KasuMeta.Tests.Api;

public sealed class StatusServiceTests : IDisposable
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly IReadOnlyDictionary<string, string> NoIds = new Dictionary<string, string>();

    private readonly TestDatabase _db = new();
    private readonly FakeLibrary _library = new();
    private readonly FetchQueue _queue = new();
    private readonly ProviderStats _stats;
    private readonly FakeConfig _config = new(new PluginConfiguration { TmdbApiKey = "key", AniListEnabled = false });
    private readonly StatusService _service;

    public StatusServiceTests()
    {
        _stats = new ProviderStats(_db.Time);
        _service = new StatusService(_db.Store, _db.Database, _queue, _stats, _library, _config);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task An_empty_store_reports_zeros_and_the_configuration()
    {
        var status = await _service.BuildAsync(None);

        Assert.Equal(("1.2.3.0", true, false, 0), (status.Version, status.TmdbConfigured, status.AniListEnabled, status.Queued));
        Assert.Equal(new ItemCounts(0, 0, 0, 0, 0), status.Items);
        Assert.Empty(status.RecentErrors);
        Assert.Empty(status.Providers);
        Assert.Equal(_db.Database.SchemaVersion, status.SchemaVersion);
        Assert.True(status.SearchIndex);
    }

    [Fact]
    public async Task Counts_queue_errors_and_providers_are_reported()
    {
        var failedOld = Guid.NewGuid();
        var failedNew = Guid.NewGuid();
        var gone = Guid.NewGuid();
        _library.Add(FakeLibrary.Movie(failedNew, "1") with { Name = "Antitrust" });
        _library.Add(FakeLibrary.Series(failedOld, "2") with { Name = "Magia Record" });
        await State(Guid.NewGuid(), FetchStatus.Ok);
        await State(Guid.NewGuid(), FetchStatus.Ok);
        await State(Guid.NewGuid(), FetchStatus.Skipped);
        await State(failedOld, FetchStatus.Error, "AniList: 503", ItemKind.Series);
        _db.Time.Now = _db.Time.Now.AddHours(1);
        await State(failedNew, FetchStatus.Error, "TMDB rejected the API key (401).", ItemKind.Movie);
        await State(gone, FetchStatus.Error, "boom", null, at: _db.Time.Now.AddHours(-2));
        _queue.Enqueue(Guid.NewGuid());
        _stats.Request(MetadataSources.Tmdb);
        _stats.Failure(MetadataSources.Tmdb, "TMDB rejected the API key (401).");

        var status = await _service.BuildAsync(None);

        Assert.Equal(new ItemCounts(6, 2, 1, 3, 0), status.Items);
        Assert.Equal(1, status.Queued);
        Assert.Equal(
            [
                (failedNew.ToString("N"), (string?)"Antitrust", (string?)"Movie"),
                (failedOld.ToString("N"), "Magia Record", "Series"),
                (gone.ToString("N"), null, null), // no longer in the library: no name
            ],
            status.RecentErrors.Select(e => (e.ItemId, e.Name, e.Type)));
        Assert.Equal("TMDB rejected the API key (401).", status.RecentErrors[0].Error);
        var tmdb = status.Providers[MetadataSources.Tmdb];
        Assert.Equal((1L, 1L, _db.Time.Now), (tmdb.Requests, tmdb.Failures, tmdb.LastFailureAt!.Value));
    }

    [Fact]
    public async Task Recent_errors_are_capped()
    {
        for (var i = 0; i < StatusService.RecentErrorCount + 5; i++)
        {
            await State(Guid.NewGuid(), FetchStatus.Error, "x");
        }

        var status = await _service.BuildAsync(None);

        Assert.Equal(StatusService.RecentErrorCount, status.RecentErrors.Count);
        Assert.Equal(StatusService.RecentErrorCount + 5, status.Items.Error);
    }

    private Task State(Guid id, FetchStatus status, string? error = null, ItemKind? kind = ItemKind.Movie, DateTimeOffset? at = null) =>
        _db.Store.SetFetchStateAsync(new FetchState(id, NoIds, at ?? _db.Time.Now, status, error, MetadataFetcher.FetchVersion, kind), None);
}
