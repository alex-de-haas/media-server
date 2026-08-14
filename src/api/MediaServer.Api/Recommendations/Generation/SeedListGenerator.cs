using MediaServer.Api.Data;

namespace MediaServer.Api.Recommendations.Generation;

/// <summary>
/// Candidates from a per-seed TMDb list: what the viewer watched, asked back of TMDb.
/// </summary>
/// <remarks>
/// Two instances exist, and they are genuinely different signals rather than one with a synonym.
/// <c>/recommendations</c> is behavioural — people who watched this also watched that — while
/// <c>/similar</c> is content-based, computed from genres and keywords. Asking both and pooling the
/// answers is the cheapest way this engine has to see a title from two directions, which is also why
/// the cache had to learn to tell them apart before the second one could exist.
/// </remarks>
public sealed class SeedListGenerator(
    ITmdbRecommendationSource tmdb,
    TmdbRecommendationGenerator list,
    string key,
    int seedLimit) : IRecommendationGenerator
{
    public const string SeedsKey = "seeds";

    public const string SimilarKey = "similar";

    /// <summary>How many of the top seeds the content-based list is asked about.</summary>
    /// <remarks>
    /// Fewer than the behavioural list gets. Every seed here is an extra request, and the marginal
    /// value falls off fast: the strongest seeds are where a second opinion is worth paying for.
    /// </remarks>
    internal const int SimilarSeeds = 8;

    public static SeedListGenerator Recommendations(ITmdbRecommendationSource tmdb) =>
        new(tmdb, TmdbRecommendationGenerator.Seeds, SeedsKey, RecommendationSeedSelector.MaxSeeds);

    public static SeedListGenerator Similar(ITmdbRecommendationSource tmdb) =>
        new(tmdb, TmdbRecommendationGenerator.Similar, SimilarKey, SimilarSeeds);

    public string Key => key;

    public async Task<IReadOnlyList<GeneratedCandidate>> GenerateAsync(
        GeneratorContext context, CancellationToken cancellationToken)
    {
        var candidates = new List<GeneratedCandidate>();

        foreach (var seed in context.Seeds.Take(seedLimit))
        {
            var titles = await tmdb.ForSeedAsync(seed.Identity, list, cancellationToken);
            for (var position = 0; position < titles.Count; position++)
            {
                var title = titles[position];
                var identity = new RecommendationIdentity(seed.Identity.Kind, title.TmdbId);

                // A seed cannot recommend itself, and one seed recommending another is not news.
                if (context.SeedIdentities.Contains(identity))
                {
                    continue;
                }

                // Weight by the seed, and decay down that seed's own list: TMDb's order carries real
                // information, so position 1 should not count the same as position 20.
                candidates.Add(new GeneratedCandidate(
                    identity, title, seed.Weight / (position + 1.0), seed.Identity.TmdbId));
            }
        }

        return candidates;
    }
}
