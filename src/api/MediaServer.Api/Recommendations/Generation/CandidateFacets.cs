using MediaServer.Api.Recommendations.Profile;

namespace MediaServer.Api.Recommendations.Generation;

/// <summary>
/// Turns a TMDb list object into facets the taste profile can be compared against.
/// </summary>
/// <remarks>
/// Free, and that is the whole point. Widening the cached projection means every candidate already
/// carries its genre ids, original language and release year, so three of the five facet families
/// come out of a request that was going to be made anyway. Keywords and people are not in a list
/// object, and a candidate simply goes without them — <see cref="TasteProfile.Affinity"/> judges a
/// candidate on the families it has, so a missing family costs nothing rather than reading as
/// dissimilarity.
/// <para>
/// A title the library holds is read properly instead, through <see cref="TitleFacetReader"/>: it has
/// the person graph and the keywords, and using them is a join.
/// </para>
/// </remarks>
public static class CandidateFacets
{
    /// <summary>
    /// TMDb's genre ids, which the profile stores by name because that is what the library stores.
    /// </summary>
    /// <remarks>
    /// Hardcoded rather than fetched from <c>/genre/{type}/list</c>. These ids have been stable for
    /// the API's whole life and there are thirty-odd of them; a request per instance to learn a frozen
    /// table would be a request spent on nothing, and a cache for it a table to keep warm. An id this
    /// does not know simply contributes no genre facet, which is the same graceful nothing a missing
    /// family already produces.
    /// </remarks>
    private static readonly Dictionary<int, string> GenreNames = new()
    {
        // Movie genres.
        [28] = "action",
        [12] = "adventure",
        [16] = "animation",
        [35] = "comedy",
        [80] = "crime",
        [99] = "documentary",
        [18] = "drama",
        [10751] = "family",
        [14] = "fantasy",
        [36] = "history",
        [27] = "horror",
        [10402] = "music",
        [9648] = "mystery",
        [10749] = "romance",
        [878] = "science fiction",
        [10770] = "tv movie",
        [53] = "thriller",
        [10752] = "war",
        [37] = "western",

        // Television genres that do not overlap the list above.
        [10759] = "action & adventure",
        [10762] = "kids",
        [10763] = "news",
        [10764] = "reality",
        [10765] = "sci-fi & fantasy",
        [10766] = "soap",
        [10767] = "talk",
        [10768] = "war & politics",
    };

    /// <summary>What a TMDb list object can say about itself, or empty when it carries no features.</summary>
    public static TitleFacets Of(TmdbRecommendedTitle title)
    {
        var facets = new List<WeightedFacet>();

        foreach (var id in title.GenreIds ?? [])
        {
            if (GenreNames.TryGetValue(id, out var name))
            {
                facets.Add(new WeightedFacet(FacetFamily.Genre, name, 1));
            }
        }

        if (TitleFacetReader.Decade(title.Year) is { } decade)
        {
            facets.Add(new WeightedFacet(FacetFamily.Decade, decade, 1));
        }

        if (!string.IsNullOrWhiteSpace(title.OriginalLanguage))
        {
            facets.Add(new WeightedFacet(FacetFamily.Language, title.OriginalLanguage.ToLowerInvariant(), 1));
        }

        return facets.Count == 0 ? TitleFacets.Empty : new TitleFacets(facets);
    }

    /// <summary>The genre name TMDb's id stands for, for the diversity caps. Null when unknown.</summary>
    public static string? GenreName(int id) => GenreNames.GetValueOrDefault(id);
}
