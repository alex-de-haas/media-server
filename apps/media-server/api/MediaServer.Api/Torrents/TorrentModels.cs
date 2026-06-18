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
    long? SizeBytes)
{
    public static DownloadResponse From(Download download, TorrentSnapshot? snapshot) => new(
        download.Id,
        download.InfoHash,
        download.Name,
        download.CatalogId,
        download.State.ToString(),
        download.KeepSeeding,
        download.AddedAt,
        download.CompletedAt,
        snapshot?.EngineState,
        snapshot?.PercentComplete,
        snapshot?.DownloadRateBytesPerSecond,
        snapshot?.UploadRateBytesPerSecond,
        snapshot?.Ratio,
        snapshot?.Peers,
        snapshot?.SizeBytes);
}

/// <summary>Raised for invalid add requests (bad source, missing catalog, insufficient space).</summary>
public sealed class TorrentRequestException(string message) : Exception(message);
