using MediaServer.Api.Data;
using MediaServer.Api.Recommendations.Generation;
using MediaServer.Api.Recommendations.Profile;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Recommendations;

/// <summary>
/// The built-in engine, in three stages: generate, score, re-rank.
/// </summary>
/// <remarks>
/// One stage used to do everything — providers returned ranked lists, fusion merged positions, the
/// feed service filtered. That shape existed because a connected source returns positions without
/// scores, so rank was the only unit two sources had in common. It is still the right unit
/// <em>between</em> sources, and <see cref="RecommendationFusion"/> still does that job; what changed
/// is that it is no longer how this engine ranks within itself, where it has real numbers.
/// <list type="number">
/// <item><b>Generate</b> — several strategies contribute candidates and a reason, with no claim about
/// global order.</item>
/// <item><b>Score</b> — one scorer ranks the pooled candidates in a single unit.</item>
/// <item><b>Re-rank</b> — diversity and caps shape what is actually shown.</item>
/// </list>
/// </remarks>
public sealed class RecommendationEngine(
    MediaServerDbContext database,
    RecommendationSeedSelector seeds,
    IEnumerable<IRecommendationGenerator> generators,
    TitleFacetReader facetReader,
    TasteProfileCache profiles,
    TasteProfileBuilder profileBuilder,
    RecommendationScorer scorer,
    RecommendationReranker reranker,
    RecommendationPreferenceStore preferences,
    ILogger<RecommendationEngine> logger)
{
    /// <summary>
    /// How many candidates are scored before the re-rank trims to the limit.
    /// </summary>
    /// <remarks>
    /// Generous, because the diversity caps discard rather than reorder: a pool only as large as the
    /// limit would come back short the moment a franchise hit its cap.
    /// </remarks>
    internal const int PoolFactor = 6;

    public async Task<IReadOnlyList<RankedCandidate>> RankAsync(
        int appUserId, int limit, CancellationToken cancellationToken)
    {
        var selected = await seeds.SelectAsync(appUserId, cancellationToken);
        var profile = await profiles.GetAsync(appUserId, database, profileBuilder, cancellationToken);

        if (selected.Count == 0 && profile.IsEmpty)
        {
            // Nothing watched and nothing said: this engine has nothing to offer, and trending-style
            // filler would not be a recommendation.
            return [];
        }

        var context = new GeneratorContext(
            appUserId,
            selected,
            selected.Select(seed => seed.Identity).ToHashSet(),
            profile,
            limit);

        var pooled = await GenerateAsync(context, cancellationToken);
        if (pooled.Count == 0)
        {
            return [];
        }

        await AttachFacetsAsync(pooled, cancellationToken);

        var popularityBias = await preferences.PopularityBiasAsync(appUserId, cancellationToken);
        var ranked = scorer.Rank(
            pooled.ToDictionary(entry => entry.Key, entry => entry.Value.ToScored()),
            profile,
            popularityBias);

        var grouping = await GroupingsAsync(pooled, cancellationToken);
        return reranker.Rerank(
            [.. ranked.Take(limit * PoolFactor)],
            limit,
            identity => grouping.GetValueOrDefault(identity) ?? CandidateGrouping.None);
    }

    /// <summary>Runs every generator and pools what they produce, keyed by identity.</summary>
    /// <remarks>
    /// A generator that throws is skipped rather than propagated: one strategy failing — a TMDb
    /// endpoint gone, a payload that will not parse — must cost its own contribution and nothing else.
    /// </remarks>
    private async Task<Dictionary<RecommendationIdentity, Pooled>> GenerateAsync(
        GeneratorContext context, CancellationToken cancellationToken)
    {
        var pooled = new Dictionary<RecommendationIdentity, Pooled>();

        foreach (var generator in generators)
        {
            IReadOnlyList<GeneratedCandidate> produced;
            try
            {
                produced = await generator.GenerateAsync(context, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Recommendation generator {Generator} failed; skipping it.", generator.Key);
                continue;
            }

            foreach (var candidate in produced)
            {
                if (pooled.TryGetValue(candidate.Identity, out var existing))
                {
                    existing.Add(candidate, generator.Key);
                }
                else
                {
                    pooled[candidate.Identity] = new Pooled(candidate, generator.Key);
                }
            }

            logger.LogDebug(
                "Generator {Generator} produced {Count} candidates.", generator.Key, produced.Count);
        }

        return pooled;
    }

    /// <summary>
    /// Gives every candidate the best facets available: the full local read when the instance holds
    /// the title, and what the TMDb list object carried otherwise.
    /// </summary>
    private async Task AttachFacetsAsync(
        Dictionary<RecommendationIdentity, Pooled> pooled, CancellationToken cancellationToken)
    {
        var localIds = pooled.Values
            .Where(entry => entry.MediaItemId is not null)
            .Select(entry => entry.MediaItemId!.Value)
            .ToList();
        var local = localIds.Count > 0
            ? await facetReader.ReadAsync(localIds, cancellationToken)
            : new Dictionary<Guid, TitleFacets>();

        foreach (var entry in pooled.Values)
        {
            entry.Facets = entry.MediaItemId is { } id && local.GetValueOrDefault(id) is { } held
                ? held
                : CandidateFacets.Of(entry.Title);
        }
    }

    /// <summary>
    /// The franchise and directors behind each candidate the instance holds, for the diversity caps.
    /// </summary>
    /// <remarks>
    /// Only local titles can be grouped: a discovery's collection and credits are not in this
    /// database, and fetching them would spend requests on shaping a list rather than on filling it.
    /// A candidate with no grouping is uncapped, which is the right failure — the caps exist to stop a
    /// known franchise from marching down the page, not to punish unknowns.
    /// </remarks>
    private async Task<Dictionary<RecommendationIdentity, CandidateGrouping>> GroupingsAsync(
        Dictionary<RecommendationIdentity, Pooled> pooled, CancellationToken cancellationToken)
    {
        var byItem = pooled
            .Where(entry => entry.Value.MediaItemId is not null)
            .ToDictionary(entry => entry.Value.MediaItemId!.Value, entry => entry.Key);
        if (byItem.Count == 0)
        {
            return [];
        }

        var itemIds = byItem.Keys.ToList();
        var collections = await database.MediaItems.AsNoTracking()
            .Where(item => itemIds.Contains(item.Id) && item.CollectionId != null)
            .Select(item => new { item.Id, CollectionId = item.CollectionId!.Value })
            .ToListAsync(cancellationToken);

        var directors = await database.MediaItemPersons.AsNoTracking()
            .Where(person => itemIds.Contains(person.MediaItemId) && person.Role == PersonRole.Crew &&
                person.Job == "Director")
            .Select(person => new { person.MediaItemId, person.PersonId })
            .ToListAsync(cancellationToken);
        var directorsByItem = directors
            .GroupBy(row => row.MediaItemId)
            .ToDictionary(group => group.Key, group => group.Select(row => row.PersonId.ToString("N")).ToList());

        var result = new Dictionary<RecommendationIdentity, CandidateGrouping>();
        foreach (var (itemId, identity) in byItem)
        {
            var collection = collections.FirstOrDefault(row => row.Id == itemId)?.CollectionId.ToString("N");
            var itemDirectors = directorsByItem.GetValueOrDefault(itemId) ?? [];
            if (collection is not null || itemDirectors.Count > 0)
            {
                result[identity] = new CandidateGrouping(collection, itemDirectors);
            }
        }

        return result;
    }

    /// <summary>One candidate as it accumulates across the generators that produced it.</summary>
    private sealed class Pooled(GeneratedCandidate candidate, string generatorKey)
    {
        private readonly List<string> _generators = [generatorKey];

        public TmdbRecommendedTitle Title { get; private set; } = candidate.Title;

        public Guid? MediaItemId { get; private set; } = candidate.MediaItemId;

        public TitleFacets Facets { get; set; } = TitleFacets.Empty;

        private double Collaborative { get; set; } = candidate.Contribution;

        private int Seeds { get; set; } = candidate.SeedTmdbId is null ? 0 : 1;

        private string? TopSeed { get; set; } = candidate.SeedTmdbId;

        private double TopSeedContribution { get; set; } = candidate.Contribution;

        public void Add(GeneratedCandidate other, string generator)
        {
            Collaborative += other.Contribution;
            if (other.SeedTmdbId is not null)
            {
                Seeds++;
                if (other.Contribution > TopSeedContribution)
                {
                    TopSeedContribution = other.Contribution;
                    TopSeed = other.SeedTmdbId;
                }
            }

            // Whichever generator knew the title was local wins: local facets beat a list object's.
            MediaItemId ??= other.MediaItemId;

            // Prefer the richer projection — a local generator synthesizes a bare title, while a TMDb
            // list carries votes, popularity and genres.
            if (Title.VoteCount is null && other.Title.VoteCount is not null)
            {
                Title = other.Title;
            }

            if (!_generators.Contains(generator))
            {
                _generators.Add(generator);
            }
        }

        public ScoredCandidate ToScored() =>
            new(Collaborative, Seeds, Title, Facets, _generators, TopSeed);
    }
}
