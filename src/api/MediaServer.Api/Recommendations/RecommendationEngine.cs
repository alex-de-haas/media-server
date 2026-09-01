using MediaServer.Api.Data;
using MediaServer.Api.Recommendations.Generation;
using MediaServer.Api.Recommendations.Profile;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Recommendations;

/// <summary>Which question the engine ended up answering.</summary>
/// <remarks>
/// Reported rather than hidden, so the surface can say what it did. A feed built from a library
/// nobody has watched yet is a weaker answer than one built from a viewing history, and presenting
/// the two identically would be quietly overstating the second.
/// </remarks>
public static class RecommendationRung
{
    /// <summary>Built from what this viewer watched and said about it. The ordinary case.</summary>
    public const string History = "history";

    /// <summary>Built from what the library holds, for a viewer with no history yet.</summary>
    public const string Library = "library";
}

/// <summary>The engine's answer, and which rung of the cold-start ladder produced it.</summary>
public sealed record EngineResult(IReadOnlyList<RankedCandidate> Candidates, string Rung)
{
    public static readonly EngineResult Empty = new([], RecommendationRung.History);
}

/// <summary>
/// The built-in engine, in three stages: generate, score, re-rank.
/// </summary>
/// <remarks>
/// One stage used to do everything — providers returned ranked lists, fusion merged positions, the
/// feed service filtered. That shape existed because a connected source returns positions without
/// scores, so rank was the only unit two sources had in common. With one engine there is nothing to
/// fuse, and ranking happens here, in real numbers.
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
    LibraryFacetIndexCache facetIndex,
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

    /// <param name="seedOverride">
    /// Seeds to rank from instead of the operator's watch history. TMDb only answers "what is like X",
    /// so supplying X is the whole of "suggest something like this film" — the taste profile stays the
    /// operator's, which is why this replaces the seeds and nothing else.
    /// </param>
    public async Task<EngineResult> RankAsync(
        int appUserId, int limit, CancellationToken cancellationToken,
        IReadOnlyList<RecommendationSeed>? seedOverride = null)
    {
        var selected = seedOverride ?? await seeds.SelectAsync(appUserId, cancellationToken);
        var profile = await profiles.GetAsync(appUserId, database, profileBuilder, cancellationToken);
        var rung = RecommendationRung.History;

        if (selected.Count == 0 && profile.IsEmpty)
        {
            // Nothing watched and nothing said. Rather than the trending filler this feature refuses
            // to serve, fall to what the instance can still answer honestly: an operator chose every
            // title in this library, and that is taste before anything is played.
            profile = await profileBuilder.BuildFromLibraryAsync(cancellationToken);
            rung = RecommendationRung.Library;

            if (profile.IsEmpty)
            {
                return EngineResult.Empty;
            }
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
            return EngineResult.Empty;
        }

        await AttachFacetsAsync(pooled, cancellationToken);

        var popularityBias = await preferences.PopularityBiasAsync(appUserId, cancellationToken);
        var ranked = scorer.Rank(
            pooled.ToDictionary(entry => entry.Key, entry => entry.Value.ToScored()),
            profile,
            popularityBias);

        var grouping = await GroupingsAsync(pooled, cancellationToken);
        var shaped = reranker.Rerank(
            [.. ranked.Take(limit * PoolFactor)],
            limit,
            identity => grouping.GetValueOrDefault(identity) ?? CandidateGrouping.None);

        return new EngineResult(await WithReasonsAsync(appUserId, shaped, cancellationToken), rung);
    }

    /// <summary>
    /// Names why each surviving card is here.
    /// </summary>
    /// <remarks>
    /// Done after the re-rank rather than before it, so the seed lookup only touches titles the viewer
    /// will actually see. A rated seed is the most convincing sentence this feature can print — "you
    /// gave this five stars" is an argument the viewer already agreed with — so it wins over a bare
    /// watch whenever the stars are there.
    /// </remarks>
    private async Task<IReadOnlyList<RankedCandidate>> WithReasonsAsync(
        int appUserId, IReadOnlyList<RankedCandidate> shaped, CancellationToken cancellationToken)
    {
        var seedIds = shaped
            .Select(entry => entry.Candidate.TopSeedTmdbId)
            .Where(seed => seed is not null)
            .Select(seed => seed!)
            .Distinct()
            .ToList();

        var seeds = new Dictionary<string, (string Title, int? Rating)>(StringComparer.Ordinal);
        if (seedIds.Count > 0)
        {
            var rows = await database.MediaItems.AsNoTracking()
                .Where(item => item.IdentityProvider == "tmdb" && item.IdentityProviderId != null &&
                    seedIds.Contains(item.IdentityProviderId))
                .GroupJoin(
                    database.UserItemData.AsNoTracking().Where(data => data.AppUserId == appUserId),
                    item => item.Id,
                    data => data.MediaItemId,
                    (item, data) => new { item.IdentityProviderId, item.Title, Data = data })
                .SelectMany(
                    row => row.Data.DefaultIfEmpty(),
                    (row, data) => new { row.IdentityProviderId, row.Title, Rating = data == null ? null : data.Rating })
                .ToListAsync(cancellationToken);

            foreach (var row in rows.Where(row => row.IdentityProviderId is not null))
            {
                seeds[row.IdentityProviderId!] = (row.Title, row.Rating);
            }
        }

        return [.. shaped.Select(entry => entry with { Candidate = entry.Candidate with { Reason = ReasonFor(entry.Candidate, seeds) } })];
    }

    private static RecommendationReason? ReasonFor(
        ScoredCandidate candidate, IReadOnlyDictionary<string, (string Title, int? Rating)> seeds)
    {
        if (candidate.TopSeedTmdbId is { } seedId && seeds.GetValueOrDefault(seedId) is { Title: not null } seed)
        {
            return seed.Rating is { } stars
                ? new RecommendationReason(RecommendationReason.RatedSeed, seed.Title, stars)
                : new RecommendationReason(RecommendationReason.Seed, seed.Title);
        }

        // No seed behind it, so the strategy that found it is the explanation. Order matters: the
        // most specific claim a generator can make wins over the vaguest one that also applies.
        if (candidate.Generators.Contains(Generation.CollectionsGenerator.GeneratorKey))
        {
            return new RecommendationReason(RecommendationReason.Franchise, candidate.ReasonDetail);
        }

        if (candidate.Generators.Contains(Generation.PeopleGenerator.GeneratorKey))
        {
            return new RecommendationReason(RecommendationReason.Person, candidate.ReasonDetail);
        }

        if (candidate.Generators.Contains(Generation.HeldGenerator.GeneratorKey))
        {
            return new RecommendationReason(RecommendationReason.InLibrary);
        }

        if (candidate.Generators.Contains(Generation.DiscoverGenerator.GeneratorKey))
        {
            return new RecommendationReason(RecommendationReason.Taste);
        }

        return null;
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
            ? await facetIndex.FacetsForAsync(localIds, database, facetReader, cancellationToken)
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
        private string? _reasonDetail = candidate.ReasonDetail;

        public TmdbRecommendedTitle Title { get; private set; } = candidate.Title;

        public Guid? MediaItemId { get; private set; } = candidate.MediaItemId;

        public TitleFacets Facets { get; set; } = TitleFacets.Empty;

        /// <summary>
        /// The distinct watched titles that argued for this candidate.
        /// </summary>
        /// <remarks>
        /// A set rather than a counter, because <c>seeds</c> and <c>similar</c> ask two questions about
        /// the <em>same</em> watched title. Counting each answer would let one film look like two films
        /// agreeing, and the breadth multiplier would reward it as such — the contributions are already
        /// summed, so that would be counting the same evidence twice.
        /// </remarks>
        private readonly HashSet<string> _seeds = candidate.SeedTmdbId is null
            ? []
            : [candidate.SeedTmdbId];

        private double Collaborative { get; set; } = candidate.Contribution;

        private string? TopSeed { get; set; } = candidate.SeedTmdbId;

        private double TopSeedContribution { get; set; } = candidate.Contribution;

        public void Add(GeneratedCandidate other, string generator)
        {
            Collaborative += other.Contribution;
            if (other.SeedTmdbId is not null)
            {
                _seeds.Add(other.SeedTmdbId);
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

            _reasonDetail ??= other.ReasonDetail;

            if (!_generators.Contains(generator))
            {
                _generators.Add(generator);
            }
        }

        public ScoredCandidate ToScored() =>
            new(Collaborative, _seeds.Count, Title, Facets, _generators, TopSeed) { ReasonDetail = _reasonDetail };
    }
}
