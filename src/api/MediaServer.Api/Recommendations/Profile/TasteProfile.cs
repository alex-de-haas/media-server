namespace MediaServer.Api.Recommendations.Profile;

/// <summary>
/// What one viewer's history says they like, and what it says they do not.
/// </summary>
/// <remarks>
/// Two vectors rather than one signed vector. A facet can honestly be both — a viewer who has seen
/// forty thrillers and disliked three of them likes thrillers <em>and</em> has told you something
/// about which ones — and collapsing that into a single number would let a handful of one-star films
/// erase a family the viewer demonstrably enjoys.
/// <para>
/// Each family is L2-normalized on its own, so a title carrying sixteen keywords cannot outvote one
/// carrying four, and a cosine against any family is a number between 0 and 1 whatever the family's
/// cardinality.
/// </para>
/// </remarks>
public sealed class TasteProfile
{
    private readonly IReadOnlyDictionary<FacetFamily, IReadOnlyDictionary<string, double>> _liked;
    private readonly IReadOnlyDictionary<FacetFamily, IReadOnlyDictionary<string, double>> _disliked;

    public TasteProfile(
        IReadOnlyDictionary<FacetFamily, IReadOnlyDictionary<string, double>> liked,
        IReadOnlyDictionary<FacetFamily, IReadOnlyDictionary<string, double>> disliked)
    {
        _liked = liked;
        _disliked = disliked;
    }

    /// <summary>A profile with nothing in it: the honest answer for a user with no history at all.</summary>
    public static TasteProfile Empty { get; } = new(
        new Dictionary<FacetFamily, IReadOnlyDictionary<string, double>>(),
        new Dictionary<FacetFamily, IReadOnlyDictionary<string, double>>());

    public bool IsEmpty => _liked.Count == 0 && _disliked.Count == 0;

    /// <summary>The weight this profile puts on one facet, for tests and for explaining a card.</summary>
    public double Liked(FacetFamily family, string value) =>
        _liked.GetValueOrDefault(family)?.GetValueOrDefault(value) ?? 0;

    public double Disliked(FacetFamily family, string value) =>
        _disliked.GetValueOrDefault(family)?.GetValueOrDefault(value) ?? 0;

    /// <summary>
    /// How much a candidate looks like what this viewer likes: the mean per-family cosine, over the
    /// families the candidate actually has.
    /// </summary>
    /// <remarks>
    /// Averaged over families <em>present on the candidate</em>, not over all five. A candidate whose
    /// keywords were never fetched should be judged on its genres and people rather than punished for
    /// a family nobody asked about — the same rule the scorer applies to a candidate with no features
    /// at all, and for the same reason: absent evidence must never read as evidence against.
    /// </remarks>
    public double Affinity(TitleFacets candidate) => Similarity(candidate, _liked);

    /// <summary>The same measure against what the viewer has rejected.</summary>
    public double Aversion(TitleFacets candidate) => Similarity(candidate, _disliked);

    private static double Similarity(
        TitleFacets candidate, IReadOnlyDictionary<FacetFamily, IReadOnlyDictionary<string, double>> profile)
    {
        if (candidate.IsEmpty || profile.Count == 0)
        {
            return 0;
        }

        var total = 0d;
        var families = 0;

        foreach (var group in candidate.Facets.GroupBy(facet => facet.Family))
        {
            if (profile.GetValueOrDefault(group.Key) is not { Count: > 0 } vector)
            {
                continue;
            }

            var dot = 0d;
            var magnitude = 0d;
            foreach (var facet in group)
            {
                dot += facet.Weight * vector.GetValueOrDefault(facet.Value, 0);
                magnitude += facet.Weight * facet.Weight;
            }

            families++;
            if (magnitude > 0)
            {
                // The profile side is already unit length, so dividing by the candidate's magnitude
                // completes the cosine.
                total += dot / Math.Sqrt(magnitude);
            }
        }

        return families == 0 ? 0 : total / families;
    }
}
