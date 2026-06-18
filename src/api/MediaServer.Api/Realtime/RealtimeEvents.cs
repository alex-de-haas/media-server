namespace MediaServer.Api.Realtime;

/// <summary>Names of the SignalR client methods the hub invokes. Kept in one place so the web client stays in sync.</summary>
public static class RealtimeEvents
{
    public const string JobStarted = "jobStarted";
    public const string JobProgress = "jobProgress";
    public const string JobCompleted = "jobCompleted";
    public const string JobFailed = "jobFailed";

    public const string DownloadProgress = "downloadProgress";
    public const string DownloadStateChanged = "downloadStateChanged";

    public const string IngestStageChanged = "ingestStageChanged";
}

/// <summary>Live torrent progress snapshot. Broadcast from the engine's in-memory state; never persisted.</summary>
public sealed record DownloadProgress(
    Guid DownloadId,
    string State,
    double PercentComplete,
    long DownloadRateBytesPerSecond,
    long UploadRateBytesPerSecond,
    double Ratio,
    int Peers,
    long SizeBytes,
    long? EtaSeconds);

/// <summary>A persisted torrent state transition (the trigger for downstream pipeline actions).</summary>
public sealed record DownloadStateChanged(Guid DownloadId, string State, string? Name);

/// <summary>A pipeline stage/status transition for the activity view.</summary>
public sealed record IngestStageChanged(
    Guid IngestItemId,
    Guid? DownloadId,
    Guid CatalogId,
    string Stage,
    string Status,
    string? LastError);

/// <summary>An observable background-job event.</summary>
public sealed record JobEvent(
    Guid JobId,
    string Type,
    string? RelatedType,
    Guid? RelatedId,
    string Status,
    int Progress,
    string? Error);
