namespace MediaServer.Api.Data;

/// <summary>Artwork for a media item; binaries cached on disk under the app data directory.</summary>
public sealed class ImageAsset
{
    public Guid Id { get; set; }

    public Guid MediaItemId { get; set; }

    public ImageType ImageType { get; set; }

    /// <summary>Language-tagged or null (neutral).</summary>
    public string? Language { get; set; }

    public required string Provider { get; set; }

    public required string RemotePath { get; set; }

    /// <summary>Cached under the app data dir; null until downloaded.</summary>
    public string? LocalPath { get; set; }

    /// <summary>Hash → Jellyfin <c>ImageTags</c>.</summary>
    public required string Tag { get; set; }

    public int SortOrder { get; set; }

    public MediaItem? MediaItem { get; set; }
}
