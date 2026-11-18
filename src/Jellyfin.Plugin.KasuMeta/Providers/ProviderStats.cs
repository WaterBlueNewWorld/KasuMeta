using System.Collections.Concurrent;

namespace Jellyfin.Plugin.KasuMeta.Providers;

/// <summary>What one provider did since the server started.</summary>
/// <param name="Requests">HTTP requests sent, retries included.</param>
/// <param name="Retries">Requests that were tried again (429, 5xx, network errors).</param>
/// <param name="NotFound">Lookups the provider answered with 404 (an id it doesn't know).</param>
/// <param name="Failures">Lookups that failed for good, including "no key configured".</param>
/// <param name="LastFailure">The last failure's message (never contains a key).</param>
/// <param name="LastFailureAt">When it happened.</param>
public sealed record ProviderCounters(
    long Requests,
    long Retries,
    long NotFound,
    long Failures,
    string? LastFailure,
    DateTimeOffset? LastFailureAt);

/// <summary>
/// In-memory counters per provider for the status page, kept since the server started. Cheap
/// enough to call on every request; nothing is persisted.
/// </summary>
public sealed class ProviderStats
{
    private readonly ConcurrentDictionary<string, Counter> _counters = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public ProviderStats(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>Counts one HTTP request to <paramref name="provider"/>.</summary>
    public void Request(string provider) => Interlocked.Increment(ref Get(provider).Requests);

    /// <summary>Counts a retry.</summary>
    public void Retry(string provider) => Interlocked.Increment(ref Get(provider).Retries);

    /// <summary>Counts a 404.</summary>
    public void NotFound(string provider) => Interlocked.Increment(ref Get(provider).NotFound);

    /// <summary>Counts a lookup that failed for good and remembers why.</summary>
    public void Failure(string provider, string message)
    {
        var counter = Get(provider);
        Interlocked.Increment(ref counter.Failures);
        lock (counter)
        {
            counter.LastFailure = message;
            counter.LastFailureAt = _time.GetUtcNow();
        }
    }

    /// <summary>Gets the counters of every provider used so far.</summary>
    public IReadOnlyDictionary<string, ProviderCounters> Snapshot() =>
        _counters
            .OrderBy(c => c.Key, StringComparer.Ordinal)
            .ToDictionary(
                c => c.Key,
                c =>
                {
                    lock (c.Value)
                    {
                        return new ProviderCounters(
                            Interlocked.Read(ref c.Value.Requests),
                            Interlocked.Read(ref c.Value.Retries),
                            Interlocked.Read(ref c.Value.NotFound),
                            Interlocked.Read(ref c.Value.Failures),
                            c.Value.LastFailure,
                            c.Value.LastFailureAt);
                    }
                },
                StringComparer.Ordinal);

    private Counter Get(string provider) => _counters.GetOrAdd(provider, _ => new Counter());

    private sealed class Counter
    {
#pragma warning disable SA1401, CA1051 // Fields, so Interlocked can take them by reference.
        public long Requests;
        public long Retries;
        public long NotFound;
        public long Failures;
#pragma warning restore SA1401, CA1051
        public string? LastFailure { get; set; }

        public DateTimeOffset? LastFailureAt { get; set; }
    }
}
