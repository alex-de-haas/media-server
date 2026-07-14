using System.Collections.Concurrent;
using System.Threading.Channels;

namespace MediaServer.Api.Watchlist;

/// <summary>
/// In-process queue handing tracked-title ids to <see cref="WatchlistSyncWorker"/> for an immediate
/// (on add / manual refresh) date-sync, so user-triggered syncs don't wait for the 24h cadence. A single
/// reader drains it — which also paces provider traffic globally — and a pending set dedupes so spamming
/// refresh on one title queues it once.
/// </summary>
public interface IWatchlistSyncQueue
{
    void Enqueue(Guid trackedTitleId);

    IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken cancellationToken);
}

public sealed class WatchlistSyncQueue : IWatchlistSyncQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<Guid, byte> _pending = new();

    public void Enqueue(Guid trackedTitleId)
    {
        if (_pending.TryAdd(trackedTitleId, 0))
        {
            _channel.Writer.TryWrite(trackedTitleId);
        }
    }

    public async IAsyncEnumerable<Guid> DequeueAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var id in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            _pending.TryRemove(id, out _);
            yield return id;
        }
    }
}
