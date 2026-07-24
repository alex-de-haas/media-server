using MediaServer.Api.Recommendations;

namespace MediaServer.Api.Data;

/// <summary>
/// A title one user does not want suggested again.
/// </summary>
/// <remarks>
/// Keyed by TMDb identity rather than by local media item: most hidden titles are not in the library
/// at all, and a hide must survive the title later being added (or removed).
/// </remarks>
public sealed class RecommendationHide
{
    public Guid Id { get; set; }

    public int AppUserId { get; set; }

    public RecommendationKind Kind { get; set; }

    public required string TmdbId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public AppUser? AppUser { get; set; }
}

/// <summary>
/// One cached TMDb per-title recommendation list.
/// </summary>
/// <remarks>
/// Shared across users on purpose, and safe to share: the row is keyed by the <em>seed title</em>,
/// which is public TMDb data, and holds no trace of who asked. What is personal — which titles seeded
/// the request — never leaves the user's own query.
/// </remarks>
public sealed class TmdbRecommendationCacheEntry
{
    public Guid Id { get; set; }

    public RecommendationKind Kind { get; set; }

    /// <summary>The seed title's TMDb id — the thing recommendations were asked for.</summary>
    public required string TmdbId { get; set; }

    /// <summary>The recommended titles, as JSON. Opaque to the database; shaped by the engine.</summary>
    public required string Payload { get; set; }

    /// <summary>When this was fetched; the reader enforces the TTL, so a stale row is a miss, not a lie.</summary>
    public DateTimeOffset FetchedAt { get; set; }
}

/// <summary>Per-user recommendation settings that must outlive a browser.</summary>
/// <remarks>
/// Server-side rather than browser storage so the choice follows the user between devices — the same
/// reason the calendar keeps its state in the URL rather than in local storage.
/// </remarks>
public sealed class RecommendationPreference
{
    public Guid Id { get; set; }

    public int AppUserId { get; set; }

    /// <summary>
    /// Comma-separated provider keys the user restricted the feed to, or null for "every available
    /// source" — the default, and distinct from an empty string, which would mean "none".
    /// </summary>
    public string? Sources { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public AppUser? AppUser { get; set; }
}
