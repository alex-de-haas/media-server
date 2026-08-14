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

        // Facets as sets, once. The naive version rebuilt them inside the similarity call, which ran
        // once per remaining candidate per already-picked candidate per pick — on a four-thousand
        // title library that was a hundred million allocating LINQ passes, and a request that took
        // nearly two minutes.
        var facets = new FacetSets[ranked.Count];
        var genres = new string[ranked.Count][];
        for (var index = 0; index < ranked.Count; index++)
        {
            facets[index] = FacetSets.Of(ranked[index].Candidate.Facets);
            genres[index] = [.. Genres(ranked[index].Candidate.Facets)];
        }

        // The running penalty: how much each remaining candidate resembles the closest thing already
        // picked. Updated against the one new pick each round rather than recomputed against all of
        // them, which is what turns this from O(picks² × pool) into O(picks × pool).
        var closest = new double[ranked.Count];
        var taken = new bool[ranked.Count];

        var picked = new List<RankedCandidate>(Math.Min(limit, ranked.Count));
        var perCollection = new Dictionary<string, int>(StringComparer.Ordinal);
        var perDirector = new Dictionary<string, int>(StringComparer.Ordinal);
        var perGenre = new Dictionary<string, int>(StringComparer.Ordinal);

        while (picked.Count < limit)
        {
            var bestIndex = -1;
            var bestValue = double.NegativeInfinity;

            for (var index = 0; index < ranked.Count; index++)
            {
                if (taken[index] ||
                    !Allowed(ranked[index], genres[index], groupOf, perCollection, perDirector, perGenre, picked.Count))
                {
                    continue;
                }

                var value = (RelevanceWeight * ranked[index].Score) - ((1 - RelevanceWeight) * closest[index]);
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

            var chosen = ranked[bestIndex];
            taken[bestIndex] = true;
            picked.Add(chosen);

            var grouping = groupOf(chosen.Identity);
            Count(perCollection, grouping.CollectionKey);
            foreach (var director in grouping.DirectorKeys)
            {
                Count(perDirector, director);
            }

            foreach (var genre in genres[bestIndex])
            {
                Count(perGenre, genre);
            }

            for (var index = 0; index < ranked.Count; index++)
            {
                if (!taken[index])
                {
                    closest[index] = Math.Max(closest[index], facets[index].Similarity(facets[bestIndex]));
                }
            }
        }

        return picked;
    }

    private static bool Allowed(
        RankedCandidate candidate,
        IReadOnlyList<string> candidateGenres,
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
        var ceiling = Math.Max((int)Math.Floor(MaxGenreShare * (pickedSoFar + 1)), 1);
        foreach (var genre in candidateGenres)
        {
            if (perGenre.GetValueOrDefault(genre) >= ceiling)
            {
                return false;
            }
        }

        return true;
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

    /// <summary>
    /// One candidate's facets, grouped by family and de-duplicated, ready to intersect.
    /// </summary>
    /// <remarks>
    /// Built once per candidate. Overlap between two titles is the share of facets they have in
    /// common, per family, averaged over the families they both carry — the same measure as before,
    /// with the set-building hoisted out of the inner loop.
    /// </remarks>
    private sealed class FacetSets(List<(FacetFamily Family, HashSet<string> Values)> families)
    {
        private readonly List<(FacetFamily Family, HashSet<string> Values)> _families = families;

        public static FacetSets Of(TitleFacets facets)
        {
            var families = new List<(FacetFamily, HashSet<string>)>(4);
            foreach (var group in facets.Facets.GroupBy(facet => facet.Family))
            {
                families.Add((group.Key, [.. group.Select(facet => facet.Value)]));
            }

            return new FacetSets(families);
        }

        public double Similarity(FacetSets other)
        {
            if (_families.Count == 0 || other._families.Count == 0)
            {
                return 0;
            }

            var total = 0d;
            var shared = 0;

            foreach (var (family, values) in _families)
            {
                HashSet<string>? theirs = null;
                foreach (var (otherFamily, otherValues) in other._families)
                {
                    if (otherFamily == family)
                    {
                        theirs = otherValues;
                        break;
                    }
                }

                if (theirs is null)
                {
                    continue;
                }

                shared++;
                var intersection = 0;
                foreach (var value in values)
                {
                    if (theirs.Contains(value))
                    {
                        intersection++;
                    }
                }

                var union = values.Count + theirs.Count - intersection;
                if (union > 0)
                {
                    total += (double)intersection / union;
                }
            }

            return shared == 0 ? 0 : total / shared;
        }
    }
}

/// <summary>The local groupings a candidate belongs to, for the diversity caps.</summary>
/// <param name="CollectionKey">Its franchise, when the instance knows of one.</param>
/// <param name="DirectorKeys">Its directors, when the instance holds the title and its credits.</param>
public sealed record CandidateGrouping(string? CollectionKey, IReadOnlyList<string> DirectorKeys)
{
    public static readonly CandidateGrouping None = new(null, []);
}
