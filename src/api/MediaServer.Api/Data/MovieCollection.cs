namespace MediaServer.Api.Data;

/// <summary>
/// A movie franchise/collection as defined by a metadata provider (TMDb <c>belongs_to_collection</c>),
/// deduplicated across the library by its provider identity (<see cref="Provider"/> + <see cref="ProviderId"/>).
/// Movies link to it via <see cref="MediaItem.CollectionId"/>; a movie belongs to at most one collection.
/// This is the movie-grouping analogue of <see cref="Person"/>: cross-item structure the read layer cannot
/// derive from a single item's payload, so it is persisted rather than re-parsed from each member's <c>Raw</c>.
/// </summary>
public sealed class MovieCollection
{
    public Guid Id { get; set; }

    /// <summary>e.g. <c>tmdb</c>.</summary>
    public required string Provider { get; set; }

    /// <summary>The collection's id within <see cref="Provider"/> (TMDb collection id, as a string).</summary>
    public required string ProviderId { get; set; }

    public required string Name { get; set; }

    /// <summary>Raw provider poster path (e.g. <c>/abc.jpg</c>); null when the provider has none.</summary>
    public string? PosterPath { get; set; }

    /// <summary>Absolute, ready-to-render poster URL derived from <see cref="PosterPath"/>.</summary>
    public string? PosterUrl { get; set; }

    public string? BackdropPath { get; set; }

    /// <summary>Absolute, ready-to-render backdrop URL derived from <see cref="BackdropPath"/>.</summary>
    public string? BackdropUrl { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<MediaItem> Movies { get; set; } = new List<MediaItem>();
}
