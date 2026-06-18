using System.Threading.Channels;

namespace MediaServer.Api.Pipeline;

/// <summary>
/// In-process work queue that hands ingest-item ids to the orchestrator. Producers (the add-torrent
/// flow, torrent state transitions, operator actions, the reconciler) enqueue an id; the orchestrator
/// drains it. Decouples the torrent engine from the orchestrator and keeps the design event-driven.
/// </summary>
public interface IPipelineQueue
{
    void Enqueue(Guid ingestItemId);

    IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken cancellationToken);
}

public sealed class PipelineQueue : IPipelineQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });

    public void Enqueue(Guid ingestItemId) => _channel.Writer.TryWrite(ingestItemId);

    public IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
