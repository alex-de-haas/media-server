using MediaServer.Api.Data;
using MediaServer.Api.Recommendations.Profile;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Recommendations.Generation;

/// <summary>
/// More from the people a viewer's profile is loudest about.
/// </summary>
/// <remarks>
/// The one generator whose recommendations a viewer can predict and still want: "another film by the
/// director of the last three you loved" is a suggestion that explains itself. It is also the only
/// way this engine reaches a director's earlier, less-linked work, which behavioural lists rarely
/// surface because few people have watched it.
/// <para>
/// A filmography changes about as often as a person makes a film, so the cache runs for thirty days
/// rather than the week a recommendation list gets.
/// </para>
/// </remarks>
public sealed class PeopleGenerator(
    MediaServerDbContext database, ITmdbRecommendationSource tmdb) : IRecommendationGenerator
{
    public const string GeneratorKey = "people";

    /// <summary>How many of the profile's top people are asked about. Each is one request, cold.</summary>
    internal const int MaxPeople = 5;

    /// <summary>A filmography is public and slow-moving; a month-old copy is as good as a fresh one.</summary>
    internal static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(30);

    /// <summary>
    /// What a shared person is worth, in the collaborative unit.
    /// </summary>
    /// <remarks>
    /// Below a franchise sibling and well below a strong seed's top pick: a director in common is a
    /// real reason to look, and a weaker one than several of the viewer's own films agreeing.
    /// </remarks>
    internal const double CreditWeight = 1.0;

    public string Key => GeneratorKey;

    public async Task<IReadOnlyList<GeneratedCandidate>> GenerateAsync(
        GeneratorContext context, CancellationToken cancellationToken)
    {
        if (context.Profile.IsEmpty)
        {
            return [];
        }

        var people = await TopPeopleAsync(context, cancellationToken);
        if (people.Count == 0)
        {
            return [];
        }

        var candidates = new List<GeneratedCandidate>();
        foreach (var (personTmdbId, weight) in people)
        {
            foreach (var kind in new[] { RecommendationKind.Movie, RecommendationKind.Series })
            {
                var segment = kind == RecommendationKind.Movie ? "movie" : "tv";
                var titles = await tmdb.ForListAsync(
                    TmdbRecommendationGenerator.People,
                    kind,
                    $"{personTmdbId}:{segment}",
                    $"person/{personTmdbId}/{segment}_credits",
                    CacheLifetime,
                    // Credits arrive split by how the person was involved, and a director who also
                    // acted appears in both.
                    ["cast", "crew"],
                    cancellationToken);

                for (var position = 0; position < titles.Count; position++)
                {
                    var identity = new RecommendationIdentity(kind, titles[position].TmdbId);
                    if (context.SeedIdentities.Contains(identity))
                    {
                        continue;
                    }

                    // No meaningful order inside a filmography — TMDb returns it roughly by date — so
                    // the position decay is gentler than a ranked list's.
                    candidates.Add(new GeneratedCandidate(
                        identity, titles[position], CreditWeight * weight / (1 + (position * 0.1))));
                }
            }
        }

        return candidates;
    }

    /// <summary>
    /// The people the profile weighs most, resolved back to TMDb ids.
    /// </summary>
    /// <remarks>
    /// The profile stores local person ids, because that is what the credit rows carry; TMDb needs its
    /// own. A person this instance knows only from a non-TMDb provider is skipped rather than guessed
    /// at.
    /// </remarks>
    private async Task<IReadOnlyList<(string TmdbId, double Weight)>> TopPeopleAsync(
        GeneratorContext context, CancellationToken cancellationToken)
    {
        var localIds = await database.MediaItemPersons.AsNoTracking()
            .Select(person => person.PersonId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var weighted = localIds
            .Select(id => (Id: id, Weight: context.Profile.Liked(FacetFamily.Person, id.ToString("N"))))
            .Where(entry => entry.Weight > 0)
            .OrderByDescending(entry => entry.Weight)
            .ThenBy(entry => entry.Id)
            .Take(MaxPeople)
            .ToList();
        if (weighted.Count == 0)
        {
            return [];
        }

        var ids = weighted.Select(entry => entry.Id).ToList();
        var providerIds = await database.Persons.AsNoTracking()
            .Where(person => ids.Contains(person.Id) && person.Provider == "tmdb")
            .Select(person => new { person.Id, person.ProviderId })
            .ToDictionaryAsync(person => person.Id, person => person.ProviderId, cancellationToken);

        return [.. weighted
            .Where(entry => providerIds.ContainsKey(entry.Id))
            .Select(entry => (providerIds[entry.Id], entry.Weight))];
    }
}
