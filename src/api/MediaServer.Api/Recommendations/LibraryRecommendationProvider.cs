using MediaServer.Api.Configuration;
using MediaServer.Api.Data;

namespace MediaServer.Api.Recommendations;

/// <summary>
/// The built-in engine: TMDb's per-title recommendations, seeded by what this user actually watched.
/// </summary>
/// <remarks>
/// Available to everyone with a TMDb key — which the instance needs anyway — so a user who never
/// connects an external account still gets recommendations. TMDb answers "what is like X"; the
/// personalization is entirely in which X's are asked about and how strongly each counts, which is
/// <see cref="RecommendationSeedSelector"/>'s job.
///
/// Agreement across a viewer's own taste is a strong signal — but a factor, not a veto. Ranking by
/// the <em>number</em> of seeds before the strength of any of them, as this once did, let two weak
/// old seeds unconditionally outrank the top pick of a film loved yesterday, and amplified TMDb's
/// popularity bias into the bargain: the titles appearing in the most lists are by construction the
/// most globally linked ones, so every user's feed converged on the same blockbusters.
/// </remarks>
public sealed class LibraryRecommendationProvider(
    RecommendationSeedSelector seeds,
    ITmdbRecommendationSource tmdb,
    RecommendationScorer scorer,
    RecommendationPreferenceStore preferences,
    MediaServerSettings settings,
    ILogger<LibraryRecommendationProvider> logger) : IRecommendationProvider
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
        var selected = await seeds.SelectAsync(appUserId, cancellationToken);
        if (selected.Count == 0)
        {
            // Nothing watched yet: this engine has nothing to say, and saying nothing is correct.
            // Trending-style filler would not be a recommendation.
            return [];
        }

        var popularityBias = await preferences.PopularityBiasAsync(appUserId, cancellationToken);
        var seedIdentities = selected.Select(seed => seed.Identity).ToHashSet();
        var scores = new Dictionary<RecommendationIdentity, Aggregate>();

        foreach (var seed in selected)
        {
            var titles = await tmdb.ForSeedAsync(seed.Identity, TmdbRecommendationGenerator.Seeds, cancellationToken);
            for (var position = 0; position < titles.Count; position++)
            {
                var title = titles[position];
                var identity = new RecommendationIdentity(seed.Identity.Kind, title.TmdbId);

                // A seed cannot recommend itself, and one seed recommending another is not news.
                if (seedIdentities.Contains(identity))
                {
                    continue;
                }

                // Weight by the seed, and decay down that seed's own list: TMDb's order carries real
                // information, so position 1 should not count the same as position 20.
                var contribution = seed.Weight / (position + 1.0);
                if (scores.TryGetValue(identity, out var existing))
                {
                    existing.Add(contribution);
                }
                else
                {
                    scores[identity] = new Aggregate(contribution, title);
                }
            }
        }

        logger.LogDebug(
            "Built-in recommendations: {Seeds} seeds produced {Candidates} candidates.", selected.Count, scores.Count);

        var ranked = scorer.Rank(
            scores.ToDictionary(
                entry => entry.Key,
                entry => new ScoredCandidate(entry.Value.Score, entry.Value.Seeds, entry.Value.Title)),
            popularityBias);

        return [.. ranked
            .Take(limit)
            .Select((entry, rank) => new RecommendationCandidate(
                entry.Key,
                entry.Value.Title.Title,
                entry.Value.Title.Year,
                PosterUrl(entry.Value.Title.PosterPath),
                rank))];
    }

    private static string? PosterUrl(string? posterPath) =>
        string.IsNullOrWhiteSpace(posterPath) ? null : $"https://image.tmdb.org/t/p/w500{posterPath}";

    /// <summary>Running total for one candidate across every seed that recommended it.</summary>
    private sealed class Aggregate(double score, TmdbRecommendedTitle title)
    {
        public double Score { get; private set; } = score;

        /// <summary>How many distinct seeds recommended this. The primary sort — breadth beats depth.</summary>
        public int Seeds { get; private set; } = 1;

        public TmdbRecommendedTitle Title { get; } = title;

        public void Add(double contribution)
        {
            Score += contribution;
            Seeds++;
        }
    }
}
