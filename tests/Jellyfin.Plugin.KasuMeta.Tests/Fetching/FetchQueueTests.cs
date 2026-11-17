using Jellyfin.Plugin.KasuMeta.Fetching;

namespace Jellyfin.Plugin.KasuMeta.Tests.Fetching;

public class FetchQueueTests
{
    [Fact]
    public async Task An_item_waits_once_and_a_forced_ask_upgrades_it()
    {
        var queue = new FetchQueue();
        var id = Guid.NewGuid();

        Assert.True(queue.Enqueue(id));
        Assert.False(queue.Enqueue(id));
        Assert.False(queue.Enqueue(id, force: true));
        Assert.Equal(1, queue.Pending);

        var taken = await TakeAsync(queue, 1);
        Assert.Equal([(id, true)], taken);
        Assert.Equal(0, queue.Pending);
    }

    [Fact]
    public async Task An_item_can_queue_again_once_work_on_it_started()
    {
        var queue = new FetchQueue();
        var id = Guid.NewGuid();
        queue.Enqueue(id);
        await TakeAsync(queue, 1);

        Assert.True(queue.Enqueue(id));
        Assert.Equal([(id, false)], await TakeAsync(queue, 1));
    }

    [Fact]
    public async Task Items_come_out_in_order()
    {
        var queue = new FetchQueue();
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in ids)
        {
            queue.Enqueue(id);
        }

        Assert.Equal(ids, (await TakeAsync(queue, 5)).Select(t => t.ItemId));
    }

    private static async Task<List<(Guid ItemId, bool Force)>> TakeAsync(FetchQueue queue, int count)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var taken = new List<(Guid, bool)>();
        await foreach (var work in queue.ReadAllAsync(cts.Token))
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
