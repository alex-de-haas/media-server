using MediaServer.Api.Recommendations.Profile;

namespace MediaServer.Api.Recommendations;

/// <summary>
/// Shapes the final list: relevance traded against variety, plus caps nothing may exceed.
/// </summary>
/// <remarks>
/// A feed sorted purely by score is a feed of one thing. The engine's strongest signals — franchise
/// siblings, a director the viewer loves, a genre they watch constantly — all point at neighbours of
/// each other, so the top twenty of an unshaped list is routinely a franchise marathon plus its cast.
/// That is not wrong, exactly: every one of those titles really is a good guess. It is just useless,
/// because the viewer already knows about them.
/// <para>
/// Greedy maximal marginal relevance handles the soft version — each pick is discounted by how much
/// it resembles what has already been picked — and hard caps handle the cases where MMR's arithmetic
/// would still let a franchise through because its members score far above everything else.
/// </para>
/// </remarks>
public sealed class RecommendationReranker
{
    /// <summary>
    /// How much of each pick is relevance rather than novelty.
    /// </summary>
    /// <remarks>
    /// Weighted toward relevance: this is a recommendation feed, and variety that costs accuracy is a
    /// shuffle. At 0.75 a candidate has to be markedly better to beat one that is merely different.
    /// </remarks>
    internal const double RelevanceWeight = 0.75;

    /// <summary>At most this many titles from one franchise, however well they score.</summary>
    internal const int MaxPerCollection = 2;

    /// <summary>At most this many from one director.</summary>
    internal const int MaxPerDirector = 2;

    /// <summary>No genre may take more than this share of the list.</summary>
    /// <remarks>
    /// A share rather than a count, so it means the same thing for a row of six and a page of a
    /// hundred. Applied only once the list is long enough for a share to mean anything.
    /// </remarks>
    internal const double MaxGenreShare = 0.4;

    /// <summary>Below this many picks a share cap would just be an off-by-one argument with itself.</summary>
    internal const int GenreCapAppliesFrom = 5;

    /// <summary>
    /// Re-orders scored candidates into the list a user actually sees.
    /// </summary>
    /// <param name="ranked">Candidates, most relevant first.</param>
    /// <param name="limit">How many are wanted.</param>
    /// <param name="groupOf">
    /// The franchise and director a candidate belongs to, when known. Kept as a callback because it is
    /// a local join the scorer has no business doing.
    /// </param>
    public IReadOnlyList<RankedCandidate> Rerank(
        IReadOnlyList<RankedCandidate> ranked,
        int limit,
        Func<RecommendationIdentity, CandidateGrouping> groupOf)
    {
        if (ranked.Count == 0 || limit <= 0)
        {
            return [];
        }

        var remaining = ranked.ToList();
        var picked = new List<RankedCandidate>(Math.Min(limit, remaining.Count));
        var pickedFacets = new List<TitleFacets>();
        var perCollection = new Dictionary<string, int>(StringComparer.Ordinal);
        var perDirector = new Dictionary<string, int>(StringComparer.Ordinal);
        var perGenre = new Dictionary<string, int>(StringComparer.Ordinal);

        while (picked.Count < limit && remaining.Count > 0)
        {
            var bestIndex = -1;
            var bestValue = double.NegativeInfinity;

            for (var index = 0; index < remaining.Count; index++)
            {
                var candidate = remaining[index];
                if (!Allowed(candidate, groupOf, perCollection, perDirector, perGenre, picked.Count))
                {
                    continue;
                }

                var value = (RelevanceWeight * candidate.Score)
                    - ((1 - RelevanceWeight) * MaxSimilarity(candidate.Candidate.Facets, pickedFacets));
                if (value > bestValue)
                {
                    bestValue = value;
                    bestIndex = index;
                }
            }

            if (bestIndex < 0)
            {
                // Every remaining candidate is capped out. Stopping short beats filling the tail with
                // the franchise the caps exist to hold back.
                break;
            }

            var chosen = remaining[bestIndex];
            remaining.RemoveAt(bestIndex);
            picked.Add(chosen);
            pickedFacets.Add(chosen.Candidate.Facets);

            var grouping = groupOf(chosen.Identity);
            Count(perCollection, grouping.CollectionKey);
            foreach (var director in grouping.DirectorKeys)
            {
                Count(perDirector, director);
            }

            foreach (var genre in Genres(chosen.Candidate.Facets))
            {
                Count(perGenre, genre);
            }
        }

        return picked;
    }

    private static bool Allowed(
        RankedCandidate candidate,
        Func<RecommendationIdentity, CandidateGrouping> groupOf,
        Dictionary<string, int> perCollection,
        Dictionary<string, int> perDirector,
        Dictionary<string, int> perGenre,
        int pickedSoFar)
    {
        var grouping = groupOf(candidate.Identity);

        if (grouping.CollectionKey is { } collection && perCollection.GetValueOrDefault(collection) >= MaxPerCollection)
        {
            return false;
        }

        if (grouping.DirectorKeys.Any(director => perDirector.GetValueOrDefault(director) >= MaxPerDirector))
        {
            return false;
        }

        if (pickedSoFar + 1 < GenreCapAppliesFrom)
        {
            return true;
        }

        // The cap is measured against the list this pick would produce, so it holds at every length
        // rather than only at the end.
        var ceiling = (int)Math.Floor(MaxGenreShare * (pickedSoFar + 1));
        return !Genres(candidate.Candidate.Facets).Any(genre => perGenre.GetValueOrDefault(genre) >= Math.Max(ceiling, 1));
    }

    private static void Count(Dictionary<string, int> counts, string? key)
    {
        if (key is not null)
        {
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
    }

    private static IEnumerable<string> Genres(TitleFacets facets) =>
        facets.Facets.Where(facet => facet.Family == FacetFamily.Genre).Select(facet => facet.Value);

    /// <summary>How much a candidate resembles the most similar thing already picked, on 0–1.</summary>
    private static double MaxSimilarity(TitleFacets candidate, IReadOnlyList<TitleFacets> picked)
    {
        if (candidate.IsEmpty || picked.Count == 0)
        {
            return 0;
        }

        var best = 0d;
        foreach (var other in picked)
        {
            best = Math.Max(best, Similarity(candidate, other));
        }

        return best;
    }

    /// <summary>
    /// Facet overlap between two titles: the share of one's facets the other also carries, per
    /// family, averaged over the families they have in common.
    /// </summary>
    private static double Similarity(TitleFacets left, TitleFacets right)
    {
        var total = 0d;
        var families = 0;

        foreach (var group in left.Facets.GroupBy(facet => facet.Family))
        {
            var other = right.Facets.Where(facet => facet.Family == group.Key).Select(facet => facet.Value).ToHashSet();
            if (other.Count == 0)
            {
                continue;
            }

            families++;
            var shared = group.Count(facet => other.Contains(facet.Value));
            var union = group.Select(facet => facet.Value).Concat(other).Distinct().Count();
            if (union > 0)
            {
                total += (double)shared / union;
            }
        }

        return families == 0 ? 0 : total / families;
    }
}

/// <summary>The local groupings a candidate belongs to, for the diversity caps.</summary>
/// <param name="CollectionKey">Its franchise, when the instance knows of one.</param>
/// <param name="DirectorKeys">Its directors, when the instance holds the title and its credits.</param>
public sealed record CandidateGrouping(string? CollectionKey, IReadOnlyList<string> DirectorKeys)
{
    public static readonly CandidateGrouping None = new(null, []);
}
