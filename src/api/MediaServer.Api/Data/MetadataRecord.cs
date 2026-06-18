namespace MediaServer.Api.Data;

/// <summary>
/// Cached provider metadata for a media item, unique per <c>(MediaItemId, Provider, Language)</c>.
/// Localized fields plus the full provider payload kept as a JSON blob.
/// </summary>
public sealed class MetadataRecord
{
    public Guid Id { get; set; }

    public Guid MediaItemId { get; set; }

    /// <summary>e.g. <c>tmdb</c>.</summary>
    public required string Provider { get; set; }

    /// <summary>e.g. <c>ru-RU</c>.</summary>
    public required string Language { get; set; }

    public string? Title { get; set; }

    public string? Overview { get; set; }

    public string? Tagline { get; set; }

    /// <summary>JSON column.</summary>
    public List<string> Genres { get; set; } = new();

    public string? OfficialRating { get; set; }

    public double? CommunityRating { get; set; }

    public DateTimeOffset? ReleaseDate { get; set; }

    public long? RuntimeTicks { get; set; }

    /// <summary>JSON blob (normalize later if needed).</summary>
    public string? Cast { get; set; }

    /// <summary>JSON blob.</summary>
    public string? Crew { get; set; }

    /// <summary>Full provider payload, JSON blob.</summary>
    public string? Raw { get; set; }

    public DateTimeOffset FetchedAt { get; set; }

    public MediaItem? MediaItem { get; set; }
}
