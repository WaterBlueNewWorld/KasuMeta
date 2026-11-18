using System.Net;
using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Providers;
using Jellyfin.Plugin.KasuMeta.Providers.AniList;
using Jellyfin.Plugin.KasuMeta.Providers.Tmdb;
using Jellyfin.Plugin.KasuMeta.Storage;
using Jellyfin.Plugin.KasuMeta.Tests.Storage;
using Jellyfin.Plugin.KasuMeta.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.KasuMeta.Tests.Providers;

public class ProviderStatsTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Tmdb_requests_retries_404s_and_failures_are_counted()
    {
        var stats = new ProviderStats(_time);
        var handler = new FakeHandler();
        var client = new TmdbClient(
            new FakeHttpClientFactory(handler),
            new FakeConfig(new PluginConfiguration { TmdbApiKey = "eyJ.token" }),
            stats,
            NullLogger<TmdbClient>.Instance)
        {
            Delay = (_, _) => Task.CompletedTask,
        };
        handler.Respond(HttpStatusCode.ServiceUnavailable, "{}");
        handler.Respond(HttpStatusCode.OK, """{"original_language":"en","translations":{"translations":[]}}""");
        handler.Respond(HttpStatusCode.NotFound, "{}");
        handler.Respond(HttpStatusCode.Unauthorized, "{}");

        await client.GetMovieAsync(1, None);
        await client.GetMovieAsync(2, None);
        await Assert.ThrowsAsync<ProviderAuthenticationException>(() => client.GetMovieAsync(3, None));

        Assert.Equal(
            new ProviderCounters(4, 1, 1, 1, "TMDB rejected the API key (401).", _time.Now),
            stats.Snapshot()[MetadataSources.Tmdb]);
    }

    [Fact]
    public async Task A_missing_key_is_a_failure_without_a_request()
    {
        var stats = new ProviderStats(_time);
        var client = new TmdbClient(new FakeHttpClientFactory(new FakeHandler()), new FakeConfig(), stats, NullLogger<TmdbClient>.Instance);

        await Assert.ThrowsAsync<ProviderNotConfiguredException>(() => client.GetMovieAsync(1, None));

        var counters = stats.Snapshot()[MetadataSources.Tmdb];
        Assert.Equal((0L, 1L), (counters.Requests, counters.Failures));
        Assert.StartsWith("TMDB API key is not set", counters.LastFailure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AniList_is_counted_separately_and_a_cancelled_call_is_no_failure()
    {
        var stats = new ProviderStats(_time);
        var handler = new FakeHandler();
        var client = new AniListClient(new FakeHttpClientFactory(handler), new FakeConfig(), stats, NullLogger<AniListClient>.Instance);
        handler.Respond(HttpStatusCode.NotFound, """{"data":{"Media":null}}""");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await client.GetMediaAsync(1, None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetMediaAsync(2, cancelled.Token));

        Assert.Equal(new ProviderCounters(1, 0, 1, 0, null, null), stats.Snapshot()[MetadataSources.AniList]);
        Assert.False(stats.Snapshot().ContainsKey(MetadataSources.Tmdb));
    }
}
