namespace MediaServer.Api.Realtime;

/// <summary>
/// Abstraction over the SignalR hub so lower layers (torrent engine, orchestrator, jobs) can
/// broadcast without depending on SignalR types directly — and so tests can capture events.
/// </summary>
public interface IRealtimeNotifier
{
    Task DownloadProgressAsync(DownloadProgress progress, CancellationToken cancellationToken = default);

    Task DownloadStateChangedAsync(DownloadStateChanged change, CancellationToken cancellationToken = default);

    Task IngestStageChangedAsync(IngestStageChanged change, CancellationToken cancellationToken = default);

    Task JobChangedAsync(string eventName, JobEvent job, CancellationToken cancellationToken = default);
}
