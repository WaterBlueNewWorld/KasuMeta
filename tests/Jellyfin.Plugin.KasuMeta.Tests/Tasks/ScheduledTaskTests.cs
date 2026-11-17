using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Fetching;
using Jellyfin.Plugin.KasuMeta.Storage;
using Jellyfin.Plugin.KasuMeta.Tasks;
using Jellyfin.Plugin.KasuMeta.Tests.Storage;
using Jellyfin.Plugin.KasuMeta.Tests.Support;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.KasuMeta.Tests.Tasks;

public sealed class ScheduledTaskTests : IDisposable
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly IReadOnlyDictionary<string, string> NoIds = new Dictionary<string, string>();

    private readonly TestDatabase _db = new();
    private readonly FakeLibrary _library = new();
    private readonly FetchQueue _queue = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Backfill_queues_only_items_never_seen()
    {
        var seen = _library.Add(FakeLibrary.Movie(Guid.NewGuid(), "1")).Id;
        var fresh = _library.Add(FakeLibrary.Movie(Guid.NewGuid(), "2")).Id;
        await _db.Store.SetFetchStateAsync(
            new FetchState(seen, NoIds, _db.Time.Now, FetchStatus.Ok, FetchVersion: MetadataFetcher.FetchVersion), None);
        var task = new BackfillTask(_library, _db.Store, _queue, NullLogger<BackfillTask>.Instance);

        await task.ExecuteAsync(new Progress<double>(), None);

        Assert.Equal(1, _queue.Pending);
        Assert.Equal([(fresh, false)], await TakeAsync(1));
    }

    [Fact]
    public async Task Backfill_requeues_items_fetched_by_an_older_version()
    {
        var old = _library.Add(FakeLibrary.Movie(Guid.NewGuid(), "1")).Id;
        var current = _library.Add(FakeLibrary.Movie(Guid.NewGuid(), "2")).Id;
        await _db.Store.SetFetchStateAsync(new FetchState(old, NoIds, _db.Time.Now, FetchStatus.Ok, FetchVersion: 1), None);
        await _db.Store.SetFetchStateAsync(
            new FetchState(current, NoIds, _db.Time.Now, FetchStatus.Ok, FetchVersion: MetadataFetcher.FetchVersion), None);
        var task = new BackfillTask(_library, _db.Store, _queue, NullLogger<BackfillTask>.Instance);

        await task.ExecuteAsync(new Progress<double>(), None);

        Assert.Equal([(old, false)], await TakeAsync(1));
        Assert.Equal(0, _queue.Pending);
    }

    [Fact]
    public async Task Refresh_forces_stale_and_failed_items_but_not_skipped_or_fresh_ones()
    {
        var now = _db.Time.Now;
        var stale = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var fresh = Guid.NewGuid();
        var skipped = Guid.NewGuid();
        await _db.Store.SetFetchStateAsync(new FetchState(stale, NoIds, now.AddDays(-8), FetchStatus.Ok), None);
        await _db.Store.SetFetchStateAsync(new FetchState(failed, NoIds, now.AddMinutes(-5), FetchStatus.Error, "TMDB 503"), None);
        await _db.Store.SetFetchStateAsync(new FetchState(fresh, NoIds, now.AddHours(-1), FetchStatus.Ok), None);
        await _db.Store.SetFetchStateAsync(new FetchState(skipped, NoIds, now.AddDays(-30), FetchStatus.Skipped, "No TMDB id"), None);
        var config = new FakeConfig(new PluginConfiguration { RefreshIntervalHours = 24 * 7 });
        var task = new RefreshStaleTask(_db.Store, _queue, config, _db.Time, NullLogger<RefreshStaleTask>.Instance);

        await task.ExecuteAsync(new Progress<double>(), None);

        var queued = await TakeAsync(2);
        Assert.Equal(new HashSet<Guid> { stale, failed }, queued.Select(q => q.ItemId).ToHashSet());
        Assert.All(queued, q => Assert.True(q.Force));
    }

    [Fact]
    public void Tasks_show_under_KasuMeta_with_stable_keys_and_default_triggers()
    {
        IScheduledTask[] tasks =
        [
            new BackfillTask(_library, _db.Store, _queue, NullLogger<BackfillTask>.Instance),
            new RefreshStaleTask(_db.Store, _queue, new FakeConfig(), _db.Time, NullLogger<RefreshStaleTask>.Instance),
        ];

        Assert.Equal(["KasuMetaBackfill", "KasuMetaRefreshStale"], tasks.Select(t => t.Key));
        Assert.All(tasks, t => Assert.Equal("KasuMeta", t.Category));
        Assert.Contains(tasks[0].GetDefaultTriggers(), t => t.Type == TaskTriggerInfoType.StartupTrigger);
        Assert.All(tasks, t => Assert.Contains(t.GetDefaultTriggers(), tr => tr.Type == TaskTriggerInfoType.IntervalTrigger));
    }

    private async Task<List<(Guid ItemId, bool Force)>> TakeAsync(int count)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var taken = new List<(Guid, bool)>();
        await foreach (var work in _queue.ReadAllAsync(cts.Token))
        {
            taken.Add(work);
            if (taken.Count == count)
            {
                break;
            }
        }

        return taken;
    }
}
