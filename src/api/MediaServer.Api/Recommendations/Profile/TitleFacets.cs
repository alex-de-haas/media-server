using MediaServer.Api.Data;

namespace MediaServer.Api.Recommendations.Profile;

/// <summary>
/// The kinds of thing a taste profile is made of.
/// </summary>
/// <remarks>
/// Families are kept apart rather than pooled into one vector because they have wildly different
/// cardinalities: a title carries three or four genres and can carry sixteen keywords, so a single
/// pooled vector would let keyword-rich titles shout down everything else. Each family is normalized
/// on its own and compared on its own.
/// </remarks>
public enum FacetFamily
{
    Genre = 0,
    Keyword = 1,
    Person = 2,
    Decade = 3,
    Language = 4,
}

/// <summary>One facet of one title, and how much of that title it accounts for.</summary>
/// <param name="Value">
/// Identifies the facet inside its family: a genre or keyword name lowercased, a person id, a decade
/// like <c>2010</c>, an ISO language code.
/// </param>
public readonly record struct WeightedFacet(FacetFamily Family, string Value, double Weight);

/// <summary>Everything one title contributes, before any user weighting is applied.</summary>
public sealed record TitleFacets(IReadOnlyList<WeightedFacet> Facets)
{
    public static readonly TitleFacets Empty = new([]);

    public bool IsEmpty => Facets.Count == 0;
}

/// <summary>
/// How much a person counts, by what they did on the title.
/// </summary>
/// <remarks>
/// Presence alone is the wrong measure: a film's director is a far better predictor of whether a
/// viewer will like the next one than its eleventh-billed actor, and both are one row in the same
/// table. <see cref="MediaItemPerson.Order"/>, <see cref="MediaItemPerson.Job"/> and
/// <see cref="MediaItemPerson.Department"/> already carry everything needed to tell them apart, so
/// this is a join rather than a fetch.
/// </remarks>
public static class PersonFacetWeight
{
    /// <summary>The strongest authorial signal a film carries.</summary>
    public const double Director = 1.0;

    public const double Writer = 0.6;

    /// <summary>Anyone else in the crew: a real credit, but a weak predictor on its own.</summary>
    public const double OtherCrew = 0.15;

    /// <summary>The lead's weight; each further billing position is worth less.</summary>
    public const double Lead = 0.8;

    /// <summary>How fast billing order decays. At 0.25 the lead is 0.8 and the eleventh credit 0.23.</summary>
    public const double BillingDecay = 0.25;

    /// <summary>Beyond this many billed roles a credit says more about the production than the taste.</summary>
    public const int MaxCast = 12;

    /// <summary>The weight of one credit, or null when it should not count at all.</summary>
    public static double? Of(PersonRole role, string? job, string? department, int order)
    {
        if (role == PersonRole.Cast)
        {
            return order >= MaxCast ? null : Lead / (1 + (BillingDecay * Math.Max(order, 0)));
        }

        if (IsAny(job, "Director") || IsAny(department, "Directing"))
        {
            return Director;
        }

        if (IsAny(job, "Writer", "Screenplay", "Story") || IsAny(department, "Writing"))
        {
            return Writer;
        }

        return OtherCrew;
    }

    private static bool IsAny(string? value, params string[] candidates) =>
        value is not null && candidates.Any(candidate => string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase));
}
