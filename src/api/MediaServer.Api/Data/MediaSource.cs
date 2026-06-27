namespace MediaServer.Api.Data;

/// <summary>A playable source for a <see cref="MediaItem"/>, populated from probe.</summary>
public sealed class MediaSource
{
    public Guid Id { get; set; }

    public Guid MediaItemId { get; set; }

    public Guid? SourceFileId { get; set; }

    /// <summary>Label shown in the client's version picker when a movie/episode has more than one source
    /// (e.g. "Black &amp; White", "HDR"). Null for single-source items, which fall back to the item title.</summary>
    public string? VersionName { get; set; }

    public required string Container { get; set; }

    /// <summary>Absolute or catalog-relative path to the library file.</summary>
    public required string Path { get; set; }

    public long SizeBytes { get; set; }

    public int? Bitrate { get; set; }

    public long DurationTicks { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public MediaItem? MediaItem { get; set; }

    public ICollection<MediaStream> Streams { get; set; } = new List<MediaStream>();
}

/// <summary>A single stream inside a <see cref="MediaSource"/>.</summary>
public sealed class MediaStream
{
    public Guid Id { get; set; }

    public Guid MediaSourceId { get; set; }

    public StreamType StreamType { get; set; }

    public int Index { get; set; }

    public string? Codec { get; set; }

    public string? Profile { get; set; }

    public string? Language { get; set; }

    /// <summary>Free-text track label from the container (ffprobe <c>tags.title</c>), e.g. "Director's
    /// Commentary", "SDH", "Forced". Null when the file doesn't tag the stream.</summary>
    public string? Title { get; set; }

    // Video
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? FrameRate { get; set; }
    public int? BitDepth { get; set; }
    public string? HdrFormat { get; set; }

    // Audio
    public int? Channels { get; set; }
    public int? SampleRate { get; set; }

    public bool IsDefault { get; set; }
    public bool IsForced { get; set; }
    public bool IsExternal { get; set; }

    /// <summary>External subtitle path, when applicable.</summary>
    public string? ExternalPath { get; set; }

    public MediaSource? MediaSource { get; set; }
}
