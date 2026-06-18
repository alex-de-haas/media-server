namespace MediaServer.Api.Data;

/// <summary>
/// One playable candidate inside a torrent. Each playable file eventually maps to exactly one
/// movie or one episode; remapping changes the <c>MediaItem</c> assignment and rebuilds the clean
/// hardlink without raw filesystem renames.
/// </summary>
public sealed class SourceFile
{
    public Guid Id { get; set; }

    public Guid DownloadId { get; set; }

    /// <summary>Path under the download's <c>files/</c> directory.</summary>
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

    public Download? Download { get; set; }

    public MediaItem? MediaItem { get; set; }
}
