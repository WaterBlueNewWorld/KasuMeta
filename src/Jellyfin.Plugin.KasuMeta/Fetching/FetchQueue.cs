using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Jellyfin.Plugin.KasuMeta.Fetching;

/// <summary>Items waiting to be fetched.</summary>
public interface IFetchQueue
{
    /// <summary>Gets how many items are waiting.</summary>
    int Pending { get; }

    /// <summary>
    /// Queues an item. An item already waiting isn't queued twice; asking again with
    /// <paramref name="force"/> upgrades the waiting entry to a forced fetch.
    /// </summary>
    /// <param name="itemId">The Jellyfin item id.</param>
    /// <param name="force">
    /// Fetch even if nothing changed since the last fetch (stale refresh, admin request).
    /// Otherwise the fetcher skips items whose provider ids match the last successful fetch.
    /// </param>
    /// <returns>True if the item was newly queued.</returns>
    bool Enqueue(Guid itemId, bool force = false);

    /// <summary>Waits for the next item and takes it off the queue, with its force flag.</summary>
    IAsyncEnumerable<(Guid ItemId, bool Force)> ReadAllAsync(CancellationToken cancellationToken);
}

/// <summary>An unbounded, de-duplicating in-memory queue. Lost on restart: the backfill task rebuilds it.</summary>
public sealed class FetchQueue : IFetchQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false,
    });

    private readonly ConcurrentDictionary<Guid, bool> _pending = new();

    /// <inheritdoc />
    public int Pending => _pending.Count;

    /// <inheritdoc />
    public bool Enqueue(Guid itemId, bool force = false)
    {
        var added = false;
        _pending.AddOrUpdate(
            itemId,
            _ =>
            {
                added = true;
                return force;
            },
            (_, existing) => existing || force);

        if (added)
        {
            _channel.Writer.TryWrite(itemId);
        }

        return added;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<(Guid ItemId, bool Force)> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var id in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            // Taken off "pending" as work starts, so an update arriving mid-fetch queues it again.
            if (_pending.TryRemove(id, out var force))
            {
                yield return (id, force);
            }
        }
    }
}
