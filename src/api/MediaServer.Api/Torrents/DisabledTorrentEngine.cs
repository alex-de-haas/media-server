namespace MediaServer.Api.Torrents;

/// <summary>
/// <see cref="ITorrentEngine"/> used when no external <c>torrent-engine</c> dependency is configured
/// (<c>TorrentEngineUrl</c> is unset). Downloading is unavailable, but the rest of the app — the
/// Jellyfin surface, library browsing, identify/probe/enrich — keeps working: this is the graceful
/// degradation that matches Hosty's advisory (non-blocking) dependency model. Parsing
/// (<see cref="Inspect"/>) stays available so callers can still read an info hash offline; every
/// command that needs a running engine fails with a clear message, and queries return empty.
/// </summary>
public sealed class DisabledTorrentEngine : ITorrentEngine
{
    private const string Unavailable = "the torrent-engine dependency is not configured.";

    // Never raised — there is no engine to produce events. Suppress the "unused event" warning.
#pragma warning disable CS0067
    public event EventHandler<string>? MetadataReceived;
    public event EventHandler<string>? DownloadCompleted;
    public event EventHandler<string>? DownloadErrored;
    public event EventHandler<VpnStatus>? VpnStatusChanged;
#pragma warning restore CS0067

    // Pure, offline parse — yields the info hash/size without needing the engine.
    public TorrentDescriptor Inspect(TorrentSource source) => LocalTorrentInspector.Inspect(source);

    public Task<TorrentDescriptor> AddAsync(TorrentSource source, string saveDirectory, TorrentLimits limits, bool autoStart, CancellationToken cancellationToken) =>
        Task.FromException<TorrentDescriptor>(new InvalidOperationException(Unavailable));

    // Commands against a torrent the engine never knew about are no-ops, so stale download rows can still
    // be paused/stopped/removed (the DB-side cleanup runs regardless) without surfacing a 500.
    public Task PauseAsync(string infoHash, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ResumeAsync(string infoHash, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(string infoHash, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RemoveAsync(string infoHash, bool deleteFiles, CancellationToken cancellationToken) => Task.CompletedTask;

    public TorrentSnapshot? GetSnapshot(string infoHash) => null;

    public IReadOnlyList<TorrentSnapshot> GetAllSnapshots() => [];

    public IReadOnlyList<TorrentFileInfo> GetFiles(string infoHash) => [];

    public VpnStatus? GetVpnStatus() => null;
}
