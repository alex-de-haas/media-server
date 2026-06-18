using Microsoft.AspNetCore.SignalR;

namespace MediaServer.Api.Realtime;

/// <summary>Broadcasts realtime events to all connected clients through the <see cref="ActivityHub"/>.</summary>
public sealed class SignalRRealtimeNotifier(IHubContext<ActivityHub> hub) : IRealtimeNotifier
{
    public Task DownloadProgressAsync(DownloadProgress progress, CancellationToken cancellationToken = default) =>
        hub.Clients.All.SendAsync(RealtimeEvents.DownloadProgress, progress, cancellationToken);

    public Task DownloadStateChangedAsync(DownloadStateChanged change, CancellationToken cancellationToken = default) =>
        hub.Clients.All.SendAsync(RealtimeEvents.DownloadStateChanged, change, cancellationToken);

    public Task IngestStageChangedAsync(IngestStageChanged change, CancellationToken cancellationToken = default) =>
        hub.Clients.All.SendAsync(RealtimeEvents.IngestStageChanged, change, cancellationToken);

    public Task JobChangedAsync(string eventName, JobEvent job, CancellationToken cancellationToken = default) =>
        hub.Clients.All.SendAsync(eventName, job, cancellationToken);
}
