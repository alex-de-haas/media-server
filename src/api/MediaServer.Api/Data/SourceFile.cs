namespace MediaServer.Api.Data;

/// <summary>
/// One playable file flowing through an ingest. It is owned by its <see cref="IngestItem"/> for the whole
/// lifetime; a torrent-sourced file also carries a transient <see cref="DownloadId"/> until the
/// download→identify hand-off drops the download. Each file eventually maps to exactly one movie or one
/// episode; remapping changes the <c>MediaItem</c> assignment and moves the file (no raw copy).
/// </summary>
public sealed class SourceFile
{
    public Guid Id { get; set; }

    /// <summary>Owning ingest item — durable for the file's whole lifetime.</summary>
    public Guid IngestItemId { get; set; }

    /// <summary>Transient torrent download, or null for scan-imported files and after the download→identify
    /// hand-off (the download row is deleted, the file stays owned by the ingest).</summary>
    public Guid? DownloadId { get; set; }

    /// <summary>The file's current location, relative to the catalog root. Starts under
    /// <c>.incoming/&lt;downloadId&gt;/</c> for torrents (or wherever it sits for a scan), then becomes the
    /// canonical path once Organize moves it.</summary>
    public required string RelativePath { get; set; }

    /// <summary>Index in the torrent file list, when available.</summary>
    public int? TorrentFileIndex { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>Optional fingerprint for unmatched identity/reconciliation.</summary>
    public string? ContentHash { get; set; }

    /// <summary>Assigned movie or episode.</summary>
    public Guid? MediaItemId { get; set; }

    public SourceFileAssignmentStatus AssignmentStatus { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public IngestItem? IngestItem { get; set; }

    public Download? Download { get; set; }

    public MediaItem? MediaItem { get; set; }
}
