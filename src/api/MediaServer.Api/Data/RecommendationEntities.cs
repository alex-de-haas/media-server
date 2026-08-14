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

    /// <summary>
    /// Which question was asked of this seed. Part of the key, because TMDb answers more than one.
    /// </summary>
    /// <remarks>
    /// Without it a <c>/similar</c> payload and a <c>/recommendations</c> payload for the same title
    /// collide on <c>(Kind, TmdbId)</c>: whichever was written first would answer both generators, and
    /// the second signal would silently become a copy of the first — the worst kind of bug, since the
    /// feed would look fine and simply stop learning anything new.
    /// </remarks>
    public TmdbRecommendationGenerator Generator { get; set; }

    public RecommendationKind Kind { get; set; }

    /// <summary>The seed title's TMDb id — the thing recommendations were asked for.</summary>
    public required string TmdbId { get; set; }

    /// <summary>The recommended titles, as JSON. Opaque to the database; shaped by the engine.</summary>
    public required string Payload { get; set; }

    /// <summary>
    /// Which shape <see cref="Payload"/> was written in. A row at any other version is read as a miss.
    /// </summary>
    /// <remarks>
    /// The projection only ever grows, so an old payload deserializes <em>successfully</em> into the
    /// current shape with every new field null — which the scorer would read as "this title has no
    /// votes and no genres" rather than "nobody asked TMDb for them yet". A version is the difference
    /// between refetching once and quietly ranking half the catalogue as featureless forever.
    /// </remarks>
    public int PayloadVersion { get; set; }

    /// <summary>When this was fetched; the reader enforces the TTL, so a stale row is a miss, not a lie.</summary>
    public DateTimeOffset FetchedAt { get; set; }
}

/// <summary>Which TMDb list a cached payload came from.</summary>
/// <remarks>
/// Values are pinned because they are persisted as integers: renumbering would silently relabel every
/// cached row. <c>/recommendations</c> and <c>/similar</c> are genuinely different signals — the first
/// is behavioural ("people who watched this also watched"), the second is content-based — so the
/// engine wants both rather than treating one as a synonym for the other.
/// </remarks>
public enum TmdbRecommendationGenerator
{
    /// <summary><c>/{type}/{id}/recommendations</c>. The value every pre-existing cache row carries.</summary>
    Seeds = 0,

    /// <summary><c>/{type}/{id}/similar</c>.</summary>
    Similar = 1,

    /// <summary><c>/person/{id}/{movie,tv}_credits</c> — "more from this director".</summary>
    People = 2,

    /// <summary>
    /// <c>/discover/{movie,tv}</c> from the profile's own facets. Keyed by a hash of the facet
    /// signature rather than by a title id, because the question is not about any one title.
    /// </summary>
    Discover = 3,
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

    /// <summary>Position in the ranked feed, ascending and dense from zero.</summary>
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
/// whole snapshot exists to avoid — for a large library the shelf build ranks the whole catalogue.
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
/// <para>
/// This once also held which sources the user had narrowed the feed to. With one engine there is
/// nothing to narrow, so the column is gone rather than left as a setting that reads back as a
/// preference nobody can express.
/// </para>
/// </remarks>
public sealed class RecommendationPreference
{
    public Guid Id { get; set; }

    public int AppUserId { get; set; }

    /// <summary>
    /// How hard to push against TMDb's popularity bias: 0 leaves it alone, higher favours deep cuts.
    /// </summary>
    /// <remarks>
    /// Surfaced as a <b>Popular ↔ Deep cuts</b> control. There is no defensible single default — how
    /// much of the mainstream a viewer wants is a taste question, not a correctness one — so it starts
    /// at zero, which is exactly the behaviour the feed had before the dial existed, and the operator
    /// moves it.
    /// </remarks>
    public double PopularityBias { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public AppUser? AppUser { get; set; }
}
