namespace MediaServer.Api.Data;

/// <summary>
/// A torrent download. Only the durable facts and <see cref="State"/> transitions are persisted;
/// live progress/speed/ratio/ETA are in-memory engine values broadcast over the realtime stream (SSE).
/// </summary>
public sealed class Download
{
    public Guid Id { get; set; }

    public required string InfoHash { get; set; }

    public string? Name { get; set; }

    public Guid CatalogId { get; set; }

    public TorrentSourceType SourceType { get; set; }

    public DownloadState State { get; set; }

    /// <summary>Per-torrent override of the catalog default seeding policy.</summary>
    public bool KeepSeeding { get; set; }

    /// <summary>Directory under <c>&lt;catalog.root&gt;/files/</c>.</summary>
    public required string SavePath { get; set; }

    /// <summary>Original magnet URI or stored .torrent path; lets the engine re-add on restart.</summary>
    public string? SourceUri { get; set; }

    public DateTimeOffset AddedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public Catalog? Catalog { get; set; }

    public ICollection<SourceFile> SourceFiles { get; set; } = new List<SourceFile>();
}
