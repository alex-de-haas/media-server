using MediaServer.Api.Recommendations.Profile;

namespace MediaServer.Api.Recommendations.Generation;

/// <summary>One candidate, as a generator produced it.</summary>
/// <param name="Contribution">
/// How strongly this generator argues for the candidate, in the collaborative unit: a seed's weight
/// decayed by the candidate's position in that seed's list. Zero for generators that argue from the
/// local library instead, which are ranked by the taste profile rather than by anyone's list.
/// </param>
/// <param name="SeedTmdbId">The seed that produced it, when there was one — the raw material of a reason.</param>
/// <param name="MediaItemId">Set when the candidate is a title this instance already holds.</param>
public sealed record GeneratedCandidate(
    RecommendationIdentity Identity,
    TmdbRecommendedTitle Title,
    double Contribution,
    string? SeedTmdbId = null,
    Guid? MediaItemId = null);

/// <summary>Everything a generator is allowed to know about the user it is generating for.</summary>
/// <param name="SeedIdentities">
/// The seeds themselves, so a generator never proposes a title the viewer has already watched.
/// </param>
public sealed record GeneratorContext(
    int AppUserId,
    IReadOnlyList<RecommendationSeed> Seeds,
    IReadOnlySet<RecommendationIdentity> SeedIdentities,
    TasteProfile Profile,
    int Limit);

/// <summary>
/// A strategy for finding candidates, inside the built-in engine.
/// </summary>
/// <remarks>
/// Deliberately <b>not</b> an <see cref="IRecommendationProvider"/>. A provider is a source the user
/// can switch on and off, with a stored preference and a place in the fusion; a generator is an
/// implementation detail of one source. Nobody can meaningfully choose between "seeds" and
/// "discover", and exposing them as toggles would turn a feed into a control panel — while also
/// breaking the stored `library` preference, the source control's second-source condition, and the
/// agreement badge, all of which count sources rather than strategies.
/// </remarks>
public interface IRecommendationGenerator
{
    /// <summary>Stable key, used in logs and in a card's reason. Never a user-facing toggle.</summary>
    string Key { get; }

    /// <summary>
    /// Candidates this strategy argues for. An empty list is a normal answer — a generator with
    /// nothing to say must never cost the user their feed.
    /// </summary>
    Task<IReadOnlyList<GeneratedCandidate>> GenerateAsync(
        GeneratorContext context, CancellationToken cancellationToken);
}
