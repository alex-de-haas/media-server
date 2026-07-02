namespace MediaServer.Api.Realtime;

/// <summary>SSE event names the notifier emits. Kept in one place so the web client stays in sync.</summary>
public static class RealtimeEvents
{
    public const string JobStarted = "jobStarted";
    public const string JobProgress = "jobProgress";
    public const string JobCompleted = "jobCompleted";
    public const string JobFailed = "jobFailed";

    public const string DownloadProgress = "downloadProgress";
    public const string DownloadStateChanged = "downloadStateChanged";

    public const string IngestStageChanged = "ingestStageChanged";

    public const string VpnStatusChanged = "vpnStatusChanged";
}

/// <summary>Live torrent progress snapshot. Broadcast from the engine's in-memory state; never persisted.
/// The fields after <see cref="EtaSeconds"/> are additive richer stats; <see cref="AvailablePeers"/> high
/// with few <see cref="Peers"/> flags a connectivity/port-forwarding issue rather than a discovery one.</summary>
public sealed record DownloadProgress(
    Guid DownloadId,
    string State,
    double PercentComplete,
    long DownloadRateBytesPerSecond,
    long UploadRateBytesPerSecond,
    double Ratio,
    int Peers,
    long SizeBytes,
    long? EtaSeconds,
    int Seeds = 0,
    int Leeches = 0,
    int AvailablePeers = 0,
    long DownloadedBytes = 0,
    long UploadedBytes = 0,
    long RemainingBytes = 0,
    int TotalPieces = 0,
    int CompletePieces = 0);

/// <summary>A persisted torrent state transition (the trigger for downstream pipeline actions).</summary>
public sealed record DownloadStateChanged(Guid DownloadId, string State, string? Name);

/// <summary>Engine-wide VPN tunnel status for the activity view (mirrors the engine's VpnStatus).</summary>
public sealed record VpnStatusChanged(
    bool Connected,
    string? TunnelInterface,
    string? TunnelAddress,
    string? ExitIp,
    string? ExitCountry,
    DateTimeOffset CheckedAt);

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
