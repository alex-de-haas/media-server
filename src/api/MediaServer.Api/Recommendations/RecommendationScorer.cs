using MediaServer.Api.Recommendations.Profile;

namespace MediaServer.Api.Recommendations;

/// <summary>What one candidate accumulated across every generator that argued for it.</summary>
/// <param name="Collaborative">Σ w(seed)/(position+1) — the weighted, order-decayed contribution.</param>
/// <param name="Seeds">How many distinct seeds recommended it.</param>
/// <param name="Title">The TMDb list object, carrying whatever features came with the request.</param>
/// <param name="Facets">
/// What the candidate is made of, for comparison against the taste profile. Empty is a normal state
/// and must not be read as dissimilarity.
/// </param>
/// <param name="Generators">Which strategies produced it, in the order they were asked.</param>
/// <param name="TopSeedTmdbId">The seed that argued hardest for it, which is what a reason names.</param>
public sealed record ScoredCandidate(
    double Collaborative,
    int Seeds,
    TmdbRecommendedTitle Title,
    TitleFacets Facets,
    IReadOnlyList<string> Generators,
    string? TopSeedTmdbId = null)
{
    /// <summary>What to name when explaining this card — a person, a franchise. Set by its generator.</summary>
    public string? ReasonDetail { get; init; }

    /// <summary>The composed explanation, filled in after the re-rank so it costs only what is shown.</summary>
    public RecommendationReason? Reason { get; init; }

    public ScoredCandidate(double collaborative, int seeds, TmdbRecommendedTitle title)
        : this(collaborative, seeds, title, TitleFacets.Empty, [])
    {
    }
}

/// <summary>One ranked candidate and why it ranked there.</summary>
public sealed record RankedCandidate(
    RecommendationIdentity Identity, ScoredCandidate Candidate, double Score);

/// <summary>
/// Turns per-generator contributions into one ranked order.
/// </summary>
/// <remarks>
/// Replaces a lexicographic sort — seed count first, strength only as a tiebreak — which made breadth
/// a veto rather than a factor. Everything here is deliberately scale-free: the collaborative term is
/// normalized against the pool before the other terms are added, so the weights below keep their
/// meaning even though seed weights themselves changed when star ratings arrived (a five-star seed is
/// worth 6.5 where the old maximum was about 1.9). Absolute scores are never compared across requests
/// — the output is a rank, and fusion consumes positions.
/// </remarks>
public sealed class RecommendationScorer
{
    /// <summary>Weight of the pooled collaborative signal — the engine's primary evidence.</summary>
    internal const double CollaborativeWeight = 1.0;

    /// <summary>
    /// Weight of how much the candidate looks like what this viewer likes.
    /// </summary>
    /// <remarks>
    /// Substantial, because it is the only term that is about <em>this</em> viewer rather than about
    /// what TMDb links to what. It is also the only term a local-only candidate has, which is what
    /// lets "the library ranked by the profile" share a scale with a discovery feed.
    /// </remarks>
    internal const double AffinityWeight = 0.6;

    /// <summary>How hard a resemblance to what the viewer rejected pushes back.</summary>
    /// <remarks>
    /// Larger than <see cref="AffinityWeight"/> on purpose. A viewer who says "not this" is being
    /// specific in a way that liking something is not, and the cost of ignoring it — suggesting more
    /// of what they just rejected — is the failure people notice.
    /// </remarks>
    internal const double AversionWeight = 0.8;

    /// <summary>
    /// Weight of the smoothed community score. Small on purpose: it is a tiebreak among titles the
    /// viewer's own history already reached, not a reason to recommend something.
    /// </summary>
    internal const double QualityWeight = 0.25;

    /// <summary>
    /// Votes a title needs before its own average outweighs the prior. TMDb reports 10.0 on three
    /// votes, and nothing in the raw number distinguishes that from genuine acclaim.
    /// </summary>
    internal const double VotePrior = 200;

    /// <summary>The prior itself: roughly TMDb's own mean, on its 0–10 scale.</summary>
    internal const double MeanVote = 6.5;

    /// <summary>
    /// Ranks candidates, most relevant first.
    /// </summary>
    /// <param name="candidates">The pooled contributions, keyed by identity.</param>
    /// <param name="profile">
    /// The viewer's taste. An empty profile drops both facet terms and leaves the collaborative and
    /// quality ones, which is exactly how the engine ranked before profiles existed.
    /// </param>
    /// <param name="popularityBias">
    /// The user's <b>Popular ↔ Deep cuts</b> dial. Zero leaves TMDb's popularity ordering alone, which
    /// is exactly how the feed behaved before the dial existed.
    /// </param>
    public IReadOnlyList<RankedCandidate> Rank(
        IReadOnlyDictionary<RecommendationIdentity, ScoredCandidate> candidates,
        TasteProfile profile,
        double popularityBias)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var debiased = new Dictionary<RecommendationIdentity, double>(candidates.Count);
        foreach (var (identity, candidate) in candidates)
        {
            debiased[identity] = Collaborative(candidate, popularityBias);
        }

        // Normalizing by the pool's own maximum keeps the terms commensurable without pinning the
        // collaborative term to a scale that changes whenever seed weighting does.
        var peak = debiased.Values.Max();
        var scale = peak > 0 ? 1 / peak : 0;

        return [.. candidates
            .Select(entry => new RankedCandidate(
                entry.Key,
                entry.Value,
                Score(entry.Value, debiased[entry.Key] * scale, profile)))
            .OrderByDescending(entry => entry.Score)
            // Stable across runs: an unchanged library must not reshuffle the feed.
            .ThenBy(entry => entry.Identity.TmdbId, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The whole formula, on one candidate.
    /// </summary>
    /// <remarks>
    /// <b>A candidate with no features is scored on the terms it has.</b> Facet similarity drops to
    /// zero when there is nothing to compare, and an unknown vote count lands on the prior rather than
    /// at the bottom of the scale. The alternative — treating "no features" as "no similarity, no
    /// quality" — would sink every candidate that arrives bare, which is the path a connected
    /// source's suggestions take and would quietly disable the source for the operator paying for it.
    /// </remarks>
    internal static double Score(ScoredCandidate candidate, double normalizedCollaborative, TasteProfile profile) =>
        (CollaborativeWeight * normalizedCollaborative)
        + (AffinityWeight * profile.Affinity(candidate.Facets))
        - (AversionWeight * profile.Aversion(candidate.Facets))
        + (QualityWeight * Quality(candidate.Title));

    /// <summary>
    /// The collaborative term: pooled contributions, widened by breadth and narrowed by fame.
    /// </summary>
    /// <remarks>
    /// <c>1 + ln(seeds)</c> keeps agreement valuable without letting it dominate — two seeds are worth
    /// about 1.7 of one, ten are worth 3.3, where the old sort made any two seeds beat any one.
    /// </remarks>
    internal static double Collaborative(ScoredCandidate candidate, double popularityBias)
    {
        var breadth = 1 + Math.Log(Math.Max(candidate.Seeds, 1));
        var score = candidate.Collaborative * breadth;

        if (popularityBias <= 0 || candidate.Title.Popularity is not { } popularity || popularity <= 0)
        {
            return score;
        }

        return score / (1 + (popularityBias * Math.Log(1 + popularity)));
    }

    /// <summary>
    /// The community score, smoothed toward the mean by how many people voted, on 0–1.
    /// </summary>
    /// <remarks>
    /// A title nobody voted on lands exactly on the prior, which is also what a title with no vote
    /// data at all gets — and that is the point: "no evidence" must mean "average", never "bad".
    /// </remarks>
    internal static double Quality(TmdbRecommendedTitle title)
    {
        var votes = title.VoteCount is { } count && count > 0 ? count : 0;
        var average = title.VoteAverage ?? MeanVote;
        var smoothed = ((votes * average) + (VotePrior * MeanVote)) / (votes + VotePrior);
        return Math.Clamp(smoothed / 10, 0, 1);
    }
}
