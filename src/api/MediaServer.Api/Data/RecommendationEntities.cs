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

/// <summary>
/// One title's poster path, cached.
/// </summary>
/// <remarks>
/// Trakt returns no artwork, so a title only it suggested arrives posterless and would render as a
/// grey box. Looking the poster up costs one TMDb call per title, which is worth caching hard: a
/// poster path changes about as often as the film's title does.
/// </remarks>
public sealed class TmdbPosterCacheEntry
{
    public Guid Id { get; set; }

    public RecommendationKind Kind { get; set; }

    public required string TmdbId { get; set; }

    /// <summary>The path TMDb reports, or null when it has no poster — a cached negative, not a miss.</summary>
    public string? PosterPath { get; set; }

    public DateTimeOffset FetchedAt { get; set; }
}

/// <summary>
/// One title on one user's Jellyfin recommendation shelf, at a fixed rank.
/// </summary>
/// <remarks>
/// A snapshot of a <em>choice</em>, not of data: title, artwork, media sources, watched state and
/// version pins are all read from <see cref="MediaItem"/> at request time and are therefore always
/// current. What is pinned is the only part that is expensive to recompute and must stay still —
/// which titles, in what order — because the client's row and the opened grid are two separate
/// requests that have to agree.
/// <para>
/// Every row is by definition held locally, so unlike the web feed's DTO this stores no TMDb id,
/// poster URL or title: the media item is the better source for all three.
/// </para>
/// <para>
/// The shelf holds candidates, not the finished row. <c>watched</c> and <c>hidden</c> are applied on
/// read instead of invalidating the shelf, so a title leaves it the moment it is played.
/// </para>
/// </remarks>
public sealed class RecommendationShelfItem
{
    public Guid Id { get; set; }

    public int AppUserId { get; set; }

    /// <summary>Position in the fused feed, ascending and dense from zero.</summary>
    public int Rank { get; set; }

    public Guid MediaItemId { get; set; }

    public AppUser? AppUser { get; set; }

    public MediaItem? MediaItem { get; set; }
}

/// <summary>
/// When one user's shelf was last built.
/// </summary>
/// <remarks>
/// Separate from the rows themselves because <em>an empty shelf is still a generation</em>. Hanging
/// the timestamp off the rows would mean a user whose feed legitimately yields nothing — no history
/// yet, or no overlap between the recommendations and the library — has nothing recording that the
/// question was asked, so every view listing would rebuild from scratch. That is the exact cost this
/// whole snapshot exists to avoid, and for a Trakt-backed user it would be upstream API calls on
/// every library refresh.
/// </remarks>
public sealed class RecommendationShelfGeneration
{
    /// <summary>The key: one generation per user, replaced in place.</summary>
    public int AppUserId { get; set; }

    public DateTimeOffset GeneratedAt { get; set; }

    public AppUser? AppUser { get; set; }
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
