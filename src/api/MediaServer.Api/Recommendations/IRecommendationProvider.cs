namespace MediaServer.Api.Recommendations;

/// <summary>What a recommendation is about: a whole movie or a whole series, never one episode.</summary>
/// <remarks>
/// "Watch this next" is a title-level answer. An episode-level recommendation would either duplicate
/// Next Up (which already knows where the user is in a series) or suggest starting mid-season.
/// </remarks>
public enum RecommendationKind
{
    Movie,
    Series,
}

/// <summary>
/// A recommended title, addressed the way every source here addresses one: by TMDb id.
/// </summary>
/// <remarks>
/// Both providers speak TMDb ids — the built-in engine because it asks TMDb directly, the Trakt
/// adapter because Trakt returns them alongside its own. That shared coordinate is what lets the
/// merge stage recognize the same title from two sources, and what maps a candidate onto the
/// library.
/// </remarks>
public readonly record struct RecommendationIdentity(RecommendationKind Kind, string TmdbId)
{
    public override string ToString() => $"{Kind}:{TmdbId}";
}

/// <summary>One title a provider suggests, at the position that provider put it in.</summary>
/// <param name="Identity">TMDb coordinates.</param>
/// <param name="Title">Display title as the source knows it; the library's own title wins later when the item is held.</param>
/// <param name="Year">Release/first-air year, when known.</param>
/// <param name="PosterUrl">Absolute poster URL, when the source supplied one.</param>
/// <param name="Rank">0-based position in this provider's ranked list. Fusion reads position, not score.</param>
public sealed record RecommendationCandidate(
    RecommendationIdentity Identity,
    string Title,
    int? Year,
    string? PosterUrl,
    int Rank);

/// <summary>
/// One source of recommendations for a single user.
/// </summary>
/// <remarks>
/// Mirrors the watched-history provider boundary: adapters are resolved by stable key, and callers
/// hold keys rather than types, so nothing outside an adapter names a source. A provider answers for
/// one user at a time — recommendations are personal, and a shared cache across users would leak
/// what someone watched.
/// </remarks>
public interface IRecommendationProvider
{
    /// <summary>Stable key, used in API responses and the per-user source preference.</summary>
    string Key { get; }

    /// <summary>Name for the user's eyes.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Whether this provider can answer for this user right now — a connected and healthy account, a
    /// configured credential, whatever the source needs.
    /// </summary>
    /// <remarks>
    /// Availability is per user, not per instance: the built-in engine is available to everyone,
    /// while Trakt is available only to users who connected it.
    /// </remarks>
    Task<bool> IsAvailableAsync(int appUserId, CancellationToken cancellationToken);

    /// <summary>
    /// This provider's ranked suggestions for the user, best first, bounded by <paramref name="limit"/>.
    /// </summary>
    /// <remarks>
    /// Returns an empty list rather than throwing when the source has nothing to say or is briefly
    /// unreachable: a feed built from several providers must not fail because an optional one did.
    /// The caller applies library, watched, and hidden filtering — a provider does not need to know
    /// about the local library.
    /// </remarks>
    Task<IReadOnlyList<RecommendationCandidate>> GetAsync(
        int appUserId, int limit, CancellationToken cancellationToken);
}
