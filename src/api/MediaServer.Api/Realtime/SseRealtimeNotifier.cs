using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace MediaServer.Api.Realtime;

/// <summary>
/// Fans realtime events out to connected Server-Sent Events subscribers. Replaces the SignalR hub: the
/// activity/downloads surface only pushes server→client, which SSE does directly over the same-origin BFF
/// as a plain streaming HTTP response — no WebSocket upgrade, which the Next.js route-handler BFF cannot
/// proxy. A singleton; each connected client gets a bounded channel (a slow client drops the oldest
/// message rather than growing unbounded — the client reconciles via a refetch on reconnect).
/// </summary>
public sealed class SseRealtimeNotifier : IRealtimeNotifier
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<Guid, Channel<SseMessage>> _subscribers = new();

    public Task DownloadProgressAsync(DownloadProgress progress, CancellationToken cancellationToken = default) =>
        PublishAsync(RealtimeEvents.DownloadProgress, progress);

    public Task DownloadStateChangedAsync(DownloadStateChanged change, CancellationToken cancellationToken = default) =>
        PublishAsync(RealtimeEvents.DownloadStateChanged, change);

    public Task IngestStageChangedAsync(IngestStageChanged change, CancellationToken cancellationToken = default) =>
        PublishAsync(RealtimeEvents.IngestStageChanged, change);

    public Task VpnStatusChangedAsync(VpnStatusChanged status, CancellationToken cancellationToken = default) =>
        PublishAsync(RealtimeEvents.VpnStatusChanged, status);

    public Task JobChangedAsync(string eventName, JobEvent job, CancellationToken cancellationToken = default) =>
        PublishAsync(eventName, job);

    /// <summary>Registers a subscriber. Dispose the returned <see cref="Subscription"/> to detach it.</summary>
    public Subscription Subscribe()
    {
        var channel = Channel.CreateBounded<SseMessage>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        return new Subscription(this, id, channel.Reader);
    }

    private Task PublishAsync<T>(string eventName, T payload)
    {
        if (_subscribers.IsEmpty)
        {
            return Task.CompletedTask;
        }

        var message = new SseMessage(eventName, JsonSerializer.Serialize(payload, SerializerOptions));
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(message);
        }

        return Task.CompletedTask;
    }

    private void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    /// <summary>A connected client's handle on the stream; disposing it detaches the subscriber.</summary>
    public sealed class Subscription(SseRealtimeNotifier owner, Guid id, ChannelReader<SseMessage> reader) : IDisposable
    {
        public ChannelReader<SseMessage> Reader { get; } = reader;

        public void Dispose() => owner.Unsubscribe(id);
    }
}

/// <summary>One SSE frame: the event name and its already-serialized JSON payload.</summary>
public readonly record struct SseMessage(string Event, string Data);
