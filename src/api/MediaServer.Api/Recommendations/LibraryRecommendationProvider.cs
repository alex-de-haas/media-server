using MediaServer.Api.Configuration;
using MediaServer.Api.Data;

namespace MediaServer.Api.Recommendations;

/// <summary>
/// The built-in engine: TMDb's per-title recommendations, seeded by what this user actually watched.
/// </summary>
/// <remarks>
/// Available to everyone with a TMDb key — which the instance needs anyway — so a user who never
/// connects an external account still gets recommendations.
/// <para>
/// A thin adapter over <see cref="RecommendationEngine"/>, and deliberately so. This type is a
/// <b>source</b>: it has a stable key the user's stored preference names, it is what the source
/// control counts when deciding whether to appear, and it is what the agreement badge reports on. The
/// strategies behind it are generators, which are none of those things. Keeping the seam here rather
/// than at the provider interface is what lets the engine grow without a stored `library` preference
/// being read as a vanished source — which would switch every other source back on, the exact
/// opposite of what the user chose.
/// </para>
/// </remarks>
public sealed class LibraryRecommendationProvider(
    RecommendationEngine engine,
    MediaServerSettings settings) : IRecommendationProvider
{
    public const string ProviderKey = "library";

    public string Key => ProviderKey;

    public string DisplayName => "Your library";

    public Task<bool> IsAvailableAsync(int appUserId, CancellationToken cancellationToken) =>
        // No per-user setup: if the instance can talk to TMDb at all, every user has this source.
        Task.FromResult(!string.IsNullOrWhiteSpace(settings.TmdbApiKey));

    public async Task<IReadOnlyList<RecommendationCandidate>> GetAsync(
        int appUserId, int limit, CancellationToken cancellationToken)
    {
        var ranked = await engine.RankAsync(appUserId, limit, cancellationToken);

        return [.. ranked.Select((entry, rank) => new RecommendationCandidate(
            entry.Identity,
            entry.Candidate.Title.Title,
            entry.Candidate.Title.Year,
            PosterUrl(entry.Candidate.Title.PosterPath),
            rank))];
    }

    private static string? PosterUrl(string? posterPath) =>
        string.IsNullOrWhiteSpace(posterPath) ? null : $"https://image.tmdb.org/t/p/w500{posterPath}";
}
