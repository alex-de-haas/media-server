using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Recommendations.Generation;

/// <summary>
/// The next film in a franchise the viewer has already started, from the local collection graph.
/// </summary>
/// <remarks>
/// Costs no requests and answers a question TMDb's lists answer badly: someone who watched two
/// Mission: Impossible films wants the third, and a behavioural list is as likely to offer a
/// different action franchise. <see cref="MovieCollection"/> already records the grouping, so this is
/// a join.
/// <para>
/// A tracked title in the same collection is <b>not</b> emitted — it is already wanted, and putting
/// it back in the feed as a suggestion tells the viewer something they told the instance.
/// </para>
/// </remarks>
public sealed class CollectionsGenerator(MediaServerDbContext database) : IRecommendationGenerator
{
    public const string GeneratorKey = "collections";

    /// <summary>
    /// What a franchise sibling is worth, in the collaborative unit.
    /// </summary>
    /// <remarks>
    /// High: of everything the engine can infer without asking anyone, "you watched two of these" is
    /// the least speculative. The diversity caps downstream are what stop it from filling the feed
    /// with one franchise.
    /// </remarks>
    internal const double SiblingWeight = 1.5;

    public string Key => GeneratorKey;

    public async Task<IReadOnlyList<GeneratedCandidate>> GenerateAsync(
        GeneratorContext context, CancellationToken cancellationToken)
    {
        var watchedCollections = await database.PlaybackHistoryEntries.AsNoTracking()
            .Where(entry => entry.AppUserId == context.AppUserId)
            .Join(
                database.MediaItems.AsNoTracking(),
                entry => entry.MediaItemId,
                item => item.Id,
                (entry, item) => item.CollectionId)
            .Where(collectionId => collectionId != null)
            .Select(collectionId => collectionId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (watchedCollections.Count == 0)
        {
            return [];
        }

        var siblings = await database.MediaItems.AsNoTracking()
            .Where(item => item.RemovedAt == null && item.CollectionId != null &&
                watchedCollections.Contains(item.CollectionId!.Value) && item.Kind == MediaKind.Movie)
            .Select(item => new
            {
                item.Id, item.Title, item.Year, item.IdentityProvider, item.IdentityProviderId, item.Providers,
            })
            .ToListAsync(cancellationToken);

        var tracked = await database.WatchlistEntries.AsNoTracking()
            .Where(entry => entry.AppUserId == context.AppUserId)
            .Join(
                database.TrackedTitles.AsNoTracking(),
                entry => entry.TrackedTitleId,
                title => title.Id,
                (entry, title) => title.IdentityProviderId)
            .ToListAsync(cancellationToken);
        var trackedIds = tracked.ToHashSet(StringComparer.Ordinal);

        var candidates = new List<GeneratedCandidate>();
        foreach (var sibling in siblings)
        {
            var tmdbId = string.Equals(sibling.IdentityProvider, "tmdb", StringComparison.OrdinalIgnoreCase)
                ? sibling.IdentityProviderId
                : sibling.Providers.GetValueOrDefault("tmdb");
            if (tmdbId is null || trackedIds.Contains(tmdbId))
            {
                continue;
            }

            var identity = new RecommendationIdentity(RecommendationKind.Movie, tmdbId);
            if (context.SeedIdentities.Contains(identity))
            {
                continue;
            }

            candidates.Add(new GeneratedCandidate(
                identity,
                new TmdbRecommendedTitle(tmdbId, sibling.Title, sibling.Year, null),
                SiblingWeight,
                SeedTmdbId: null,
                MediaItemId: sibling.Id));
        }

        return candidates;
    }
}
