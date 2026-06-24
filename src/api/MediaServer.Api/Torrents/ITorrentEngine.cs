namespace MediaServer.Api.Torrents;

/// <summary>A file inside a torrent, once the file list is known (immediately for <c>.torrent</c>, after
/// metadata for magnets). <see cref="RelativePath"/> is relative to the catalog <c>files/</c> directory.</summary>
public sealed record TorrentFileInfo(int Index, string RelativePath, long Length);

/// <summary>What is known about a torrent right after it is added.</summary>
public sealed record TorrentDescriptor(
    string InfoHash,
    string? Name,
    long? TotalSize,
    bool HasMetadata,
    IReadOnlyList<TorrentFileInfo> Files);

/// <summary>A live, in-memory progress snapshot (never persisted).</summary>
public sealed record TorrentSnapshot(
    string InfoHash,
    string? Name,
    string EngineState,
    bool Complete,
    double PercentComplete,
    long DownloadRateBytesPerSecond,
    long UploadRateBytesPerSecond,
    double Ratio,
    int Peers,
    long SizeBytes);

/// <summary>Per-torrent rate limits (bytes/sec; 0 = unlimited).</summary>
public sealed record TorrentLimits(int MaxDownloadRate, int MaxUploadRate);

/// <summary>Status of the VPN tunnel the engine runs behind (engine-wide, not per-torrent).
/// <see cref="Connected"/> is the primary signal; <see cref="ExitIp"/>/<see cref="ExitCountry"/> are a
/// best-effort proof of egress and may be <c>null</c>.</summary>
public sealed record VpnStatus(
    bool Connected,
    string? TunnelInterface,
    string? TunnelAddress,
    string? ExitIp,
    string? ExitCountry,
    DateTimeOffset CheckedAt);

/// <summary>
/// Abstraction over the torrent engine. The download surface is the external <c>torrent-engine</c> app
/// (<see cref="RemoteTorrentEngine"/>); <see cref="DisabledTorrentEngine"/> stands in when none is
/// configured. Owns no database state; surfaces the file list and live snapshots, and raises events for
/// the transitions that drive the pipeline. The coordinator translates these into persisted
/// <see cref="Data.DownloadState"/> changes.
/// </summary>
public interface ITorrentEngine
{
    /// <summary>Parses a source to read its info hash and (for <c>.torrent</c>) size/files, without adding
    /// it to the engine — used for the pre-download free-space check.</summary>
    TorrentDescriptor Inspect(TorrentSource source);

    Task<TorrentDescriptor> AddAsync(TorrentSource source, string saveDirectory, TorrentLimits limits, bool autoStart, CancellationToken cancellationToken);

    Task PauseAsync(string infoHash, CancellationToken cancellationToken);

    Task ResumeAsync(string infoHash, CancellationToken cancellationToken);

    Task StopAsync(string infoHash, CancellationToken cancellationToken);

    Task RemoveAsync(string infoHash, bool deleteFiles, CancellationToken cancellationToken);

    TorrentSnapshot? GetSnapshot(string infoHash);

    IReadOnlyList<TorrentSnapshot> GetAllSnapshots();

    IReadOnlyList<TorrentFileInfo> GetFiles(string infoHash);

    /// <summary>Current VPN tunnel status, or <c>null</c> when no engine reports one (e.g. downloading disabled).</summary>
    VpnStatus? GetVpnStatus();

    /// <summary>Raised when a magnet's file list becomes available after metadata download.</summary>
    event EventHandler<string>? MetadataReceived;

    /// <summary>Raised when the VPN tunnel status changes. Only the remote engine raises it.</summary>
    event EventHandler<VpnStatus>? VpnStatusChanged;

    /// <summary>Raised when a torrent finishes downloading (transition to a complete/seeding state).</summary>
    event EventHandler<string>? DownloadCompleted;

    /// <summary>Raised when a torrent enters an error state.</summary>
    event EventHandler<string>? DownloadErrored;
}
