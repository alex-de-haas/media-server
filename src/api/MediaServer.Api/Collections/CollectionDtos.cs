using MediaServer.Api.Library;

namespace MediaServer.Api.Collections;

// UI-facing DTOs for the internal `/api/library/collections` surface. Serialized camelCase by the global
// JSON options, mirroring the rest of the `/api/library` surface. A collection is addressed by its internal
// Guid id (returned by the list), the same shape the library detail routes use.

/// <summary>A franchise/collection tile for the Collections grid: name, artwork, and how many of its movies
/// are in the library.</summary>
public sealed record CollectionSummaryDto(
    Guid Id,
    string Name,
    // The collection's own poster, falling back to a member movie's poster; null when neither has one.
    string? PosterUrl,
    // How many owned movies are in this collection (always ≥ the surfacing threshold).
    int ItemCount);

/// <summary>
/// A collection detail page: the franchise's artwork plus its member movies that are in the library, as the
/// same library cards the movie grids use, ordered chronologically.
/// </summary>
public sealed record CollectionDetailDto(
    Guid Id,
    string Name,
    string? PosterUrl,
    string? BackdropUrl,
    IReadOnlyList<LibraryItemDto> Items);
