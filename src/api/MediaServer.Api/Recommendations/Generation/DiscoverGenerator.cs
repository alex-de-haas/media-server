using System.Security.Cryptography;
using System.Text;
using MediaServer.Api.Data;
using MediaServer.Api.Recommendations.Profile;

namespace MediaServer.Api.Recommendations.Generation;

/// <summary>
/// Titles nothing links to anything the viewer has watched, found by describing their taste instead.
/// </summary>
/// <remarks>
/// Every other generator reaches candidates through a title: a seed's list, a franchise, a person's
/// filmography. That is a graph, and a graph can only reach what someone has already connected —
/// which is why a feed built from lists converges on the well-linked middle of the catalogue. Asking
/// <c>/discover</c> for the genres and decade the profile is loudest about is the only way this
/// engine walks in from outside that graph.
/// <para>
/// Cached by a hash of the query it built, so a stable profile costs one request per kind and a
/// changed one costs another. That is also the honest bound on the cost: the query only moves when
/// the viewer's taste does.
/// </para>
/// </remarks>
public sealed class DiscoverGenerator(ITmdbRecommendationSource tmdb) : IRecommendationGenerator
{
    public const string GeneratorKey = "discover";

    /// <summary>How many of the profile's top genres describe the query.</summary>
    /// <remarks>
    /// Two, joined as "and". More would produce a query nothing matches; one would produce a query
    /// half the catalogue matches, which is a popularity list wearing a disguise.
    /// </remarks>
    internal const int GenresInQuery = 2;

    internal static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);

    /// <summary>
    /// What a discovery is worth, in the collaborative unit.
    /// </summary>
    /// <remarks>
    /// The lowest of any generator, and deliberately: nobody has voted for these, no seed reached
    /// them, and the only claim being made is that they match a description. They earn their place
    /// from the profile terms in the scorer, which is exactly the evidence that produced them.
    /// </remarks>
    internal const double DiscoveryWeight = 0.5;

    public string Key => GeneratorKey;

    public async Task<IReadOnlyList<GeneratedCandidate>> GenerateAsync(
        GeneratorContext context, CancellationToken cancellationToken)
    {
        if (context.Profile.IsEmpty)
        {
            return [];
        }

        var candidates = new List<GeneratedCandidate>();

        foreach (var kind in new[] { RecommendationKind.Movie, RecommendationKind.Series })
        {
            if (QueryFor(context.Profile, kind) is not { } query)
            {
                continue;
            }

            var segment = kind == RecommendationKind.Movie ? "movie" : "tv";
            var titles = await tmdb.ForListAsync(
                TmdbRecommendationGenerator.Discover,
                kind,
                Signature(query),
                $"discover/{segment}?{query}",
                CacheLifetime,
                ["results"],
                cancellationToken);

            for (var position = 0; position < titles.Count; position++)
            {
                var identity = new RecommendationIdentity(kind, titles[position].TmdbId);
                if (context.SeedIdentities.Contains(identity))
                {
                    continue;
                }

                candidates.Add(new GeneratedCandidate(
                    identity, titles[position], DiscoveryWeight / (position + 1.0)));
            }
        }

        return candidates;
    }

    /// <summary>
    /// The query string a profile describes, or null when it has nothing specific enough to ask.
    /// </summary>
    /// <remarks>
    /// Sorted by vote count rather than by popularity: popularity is the bias this generator exists to
    /// escape, and asking TMDb for the most popular films in a genre would hand back the same
    /// blockbusters every other path already found.
    /// </remarks>
    internal static string? QueryFor(TasteProfile profile, RecommendationKind kind)
    {
        var genres = TopGenreIds(profile);
        if (genres.Count == 0)
        {
            return null;
        }

        var parts = new List<string>
        {
            $"with_genres={string.Join(',', genres)}",
            "sort_by=vote_count.desc",
            "vote_count.gte=200",
            "page=1",
        };

        if (kind == RecommendationKind.Movie)
        {
            parts.Add("include_adult=false");
        }

        return string.Join('&', parts);
    }

    /// <summary>The TMDb ids of the genres this profile weighs most.</summary>
    private static List<int> TopGenreIds(TasteProfile profile)
    {
        var ids = new List<(int Id, double Weight)>();
        foreach (var (id, name) in CandidateFacets.KnownGenres)
        {
            var weight = profile.Liked(FacetFamily.Genre, name);
            if (weight > 0)
            {
                ids.Add((id, weight));
            }
        }

        return [.. ids
            .OrderByDescending(entry => entry.Weight)
            .ThenBy(entry => entry.Id)
            .Take(GenresInQuery)
            .Select(entry => entry.Id)];
    }

    /// <summary>
    /// A stable, short key for the query. The cache column holds a title id's worth of characters, and
    /// a discovery query is longer than that, so it is hashed rather than truncated — truncation
    /// would collide two different tastes onto one cached answer.
    /// </summary>
    internal static string Signature(string query) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(query)))[..32];
}
