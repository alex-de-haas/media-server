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

/// <summary>
/// A live, in-memory progress snapshot (never persisted). Mirrors the <c>torrent-engine</c> wire
/// contract: the first ten fields are the original shape; the rest are additive richer stats that
/// default to zero/null when talking to an older engine build that does not send them.
/// <see cref="AvailablePeers"/> high with few <see cref="Peers"/> points at a connectivity/port-forwarding
/// issue rather than a discovery one.
/// </summary>
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
    long SizeBytes,
    // Nullable so an older engine build that omits these deserializes to null (UI omits them) rather than
    // 0, which would read as a real "0 seeds · 0 leeches". A new engine reporting a genuine 0 still shows it.
    int? Seeds = null,
    int? Leeches = null,
    int? AvailablePeers = null,
    long? DownloadedBytes = null,
    long? UploadedBytes = null,
    long? RemainingBytes = null,
    int? TotalPieces = null,
    int? CompletePieces = null,
    long? EtaSeconds = null);

/// <summary>Status of the VPN tunnel the engine runs behind (engine-wide, not per-torrent).
/// <see cref="Connected"/> is the primary signal; <see cref="ExitIp"/>/<see cref="ExitCountry"/> are a
/// best-effort proof of egress and may be <c>null</c>. The last three are the engine's OpenVPN profile trio
/// (<c>torrent-engine</c> 0.8.0+): the profile that runs, the one a switch is moving to, and why the last
/// start or switch failed — all <c>null</c> against an older engine, which does not send them.</summary>
public sealed record VpnStatus(
    bool Connected,
    string? TunnelInterface,
    string? TunnelAddress,
    string? ExitIp,
    string? ExitCountry,
    DateTimeOffset CheckedAt,
    string? Profile = null,
    string? PendingProfile = null,
    string? LastError = null);

/// <summary>Health of the engine's BitTorrent DHT (engine-wide, not per-torrent), mirroring the engine's
/// <c>DhtStatus</c>. Its purpose is telling three look-alike situations apart: DHT switched off, DHT idle
/// because the engine is recycled while nothing is downloading, and DHT enabled but failing to come up —
/// the last being a real degradation that is otherwise completely silent.</summary>
/// <param name="Enabled">The engine's DHT setting.</param>
/// <param name="Running">Enabled <i>and</i> an engine is actually running it.</param>
/// <param name="State"><c>NotReady</c> / <c>Initialising</c> / <c>Ready</c> while running, else <c>null</c>.
/// <c>Initialising</c> is a healthy start-up, so failure is <c>NotReady</c> specifically — never
/// <c>State != "Ready"</c>, which would flag every bootstrap as broken.</param>
/// <param name="NodeCount">Routing-table size; <c>0</c> when not running.</param>
public sealed record DhtStatus(bool Enabled, bool Running, string? State, int NodeCount);

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

    Task<TorrentDescriptor> AddAsync(TorrentSource source, string saveDirectory, bool autoStart, CancellationToken cancellationToken);

    Task PauseAsync(string infoHash, CancellationToken cancellationToken);

    Task ResumeAsync(string infoHash, CancellationToken cancellationToken);

    Task StopAsync(string infoHash, CancellationToken cancellationToken);

    Task RemoveAsync(string infoHash, bool deleteFiles, CancellationToken cancellationToken);

    TorrentSnapshot? GetSnapshot(string infoHash);

    IReadOnlyList<TorrentSnapshot> GetAllSnapshots();

    IReadOnlyList<TorrentFileInfo> GetFiles(string infoHash);

    /// <summary>Current VPN tunnel status, or <c>null</c> when no engine reports one (e.g. downloading disabled).</summary>
    VpnStatus? GetVpnStatus();

    /// <summary>Current DHT health, or <c>null</c> when no engine reports one — downloading disabled, or a
    /// <c>torrent-engine</c> older than 0.7.0, which has no <c>/dht</c> endpoint to read.</summary>
    DhtStatus? GetDhtStatus();

    /// <summary>The engine's OpenVPN profiles and the active one, or <c>null</c> when no engine reports them —
    /// downloading disabled, or a <c>torrent-engine</c> older than 0.8.0, which has no <c>/vpn/profiles</c>.</summary>
    Task<VpnProfiles?> GetVpnProfilesAsync(CancellationToken cancellationToken);

    /// <summary>Asks the engine to switch to another OpenVPN profile. The engine only records the wish and
    /// answers with its <i>current</i> status; the switch itself arrives as <see cref="VpnStatusChanged"/>
    /// events — <c>PendingProfile</c> first, then the new <c>Profile</c>. Throws
    /// <see cref="EngineRequestException"/> when the engine refuses (an unknown or malformed id) or when there
    /// is no engine to switch.</summary>
    Task<VpnStatus> SelectVpnProfileAsync(string id, CancellationToken cancellationToken);

    /// <summary>Raised when a magnet's file list becomes available after metadata download.</summary>
    event EventHandler<string>? MetadataReceived;

    /// <summary>Raised when the VPN tunnel status changes. Only the remote engine raises it.</summary>
    event EventHandler<VpnStatus>? VpnStatusChanged;

    /// <summary>Raised when DHT health changes. Only the remote engine raises it.</summary>
    event EventHandler<DhtStatus>? DhtStatusChanged;

    /// <summary>Raised when a torrent finishes downloading (transition to a complete/seeding state).</summary>
    event EventHandler<string>? DownloadCompleted;

    /// <summary>Raised when a torrent enters an error state.</summary>
    event EventHandler<string>? DownloadErrored;
}
