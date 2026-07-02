using MediaServer.Api.Data;

namespace MediaServer.Api.Torrents;

/// <summary>Add a torrent to a catalog. Exactly one of <see cref="Magnet"/> or <see cref="TorrentFileBase64"/> must be set.</summary>
public sealed record AddTorrentRequest(
    Guid CatalogId,
    string? Magnet,
    string? TorrentFileBase64,
    bool? KeepSeeding);

public sealed record DownloadResponse(
    Guid Id,
    string InfoHash,
    string? Name,
    Guid CatalogId,
    string State,
    bool KeepSeeding,
    DateTimeOffset AddedAt,
    DateTimeOffset? CompletedAt,
    // Live snapshot (null when the engine has no active manager for this download).
    string? EngineState,
    double? PercentComplete,
    long? DownloadRateBytesPerSecond,
    long? UploadRateBytesPerSecond,
    double? Ratio,
    int? Peers,
    long? SizeBytes,
    // Extended live stats (null when there is no active manager for this download).
    int? Seeds,
    int? Leeches,
    int? AvailablePeers,
    long? DownloadedBytes,
    long? UploadedBytes,
    long? RemainingBytes,
    int? TotalPieces,
    int? CompletePieces,
    long? EtaSeconds)
{
    public static DownloadResponse From(Download download, TorrentSnapshot? snapshot)
    {
        // Once a download reaches a state where the content is fully on disk and library-ready, it is
        // 100% complete by definition. The live engine progress is unreliable here: a no-seed organize
        // stops the manager and unlinks its seed copy, so manager.Progress reports a stale sub-100 value
        // (and after a restart there is no manager at all). Pin progress to 100 so a published download
        // never shows as unfinished.
        var contentComplete = download.State
            is DownloadState.Completed or DownloadState.Seeding or DownloadState.StoppedSeeding;

        return new(
            download.Id,
            download.InfoHash,
            download.Name,
            download.CatalogId,
            download.State.ToString(),
            download.KeepSeeding,
            download.AddedAt,
            download.CompletedAt,
            snapshot?.EngineState,
            contentComplete ? 100 : snapshot?.PercentComplete,
            snapshot?.DownloadRateBytesPerSecond,
            snapshot?.UploadRateBytesPerSecond,
            snapshot?.Ratio,
            snapshot?.Peers,
            snapshot?.SizeBytes,
            snapshot?.Seeds,
            snapshot?.Leeches,
            snapshot?.AvailablePeers,
            snapshot?.DownloadedBytes,
            snapshot?.UploadedBytes,
            // Complete content has nothing left; otherwise mirror the engine.
            contentComplete ? 0 : snapshot?.RemainingBytes,
            snapshot?.TotalPieces,
            snapshot?.CompletePieces,
            // Engine ETA is already null when complete/stalled; never show an ETA on a finished download.
            contentComplete ? null : snapshot?.EtaSeconds);
    }
}

/// <summary>Raised for invalid add requests (bad source, missing catalog, insufficient space).</summary>
public sealed class TorrentRequestException(string message) : Exception(message);
