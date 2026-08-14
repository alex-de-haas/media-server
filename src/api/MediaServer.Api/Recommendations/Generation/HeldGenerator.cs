using MediaServer.Api.Data;
using MediaServer.Api.Recommendations.Profile;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Recommendations.Generation;

/// <summary>
/// Titles this instance already holds and the viewer has not watched, ranked by the taste profile.
/// </summary>
/// <remarks>
/// Costs no requests, and is the only generator that can fill the Jellyfin shelf reliably. The shelf
/// used to be <em>the discovery feed intersected with the library</em>, which could surface a local
/// title only when TMDb happened to link it to something watched — after the in-library filter the
/// pool was, in the feed service's own words, a handful. Asking the library directly turns that
/// around: every row is playable, which is the only verb that surface has.
/// </remarks>
public sealed class HeldGenerator(
    MediaServerDbContext database, TitleFacetReader facets) : IRecommendationGenerator
{
    public const string GeneratorKey = "held";

    public string Key => GeneratorKey;

    public async Task<IReadOnlyList<GeneratedCandidate>> GenerateAsync(
        GeneratorContext context, CancellationToken cancellationToken)
    {
        if (context.Profile.IsEmpty)
        {
            // Nothing to rank against. Returning the library in arbitrary order would be a list, not a
            // recommendation, and the cold-start ladder is where that case belongs.
            return [];
        }

        var works = await database.MediaItems.AsNoTracking()
            .Where(item => item.RemovedAt == null && item.CatalogId != null &&
                (item.Kind == MediaKind.Movie || item.Kind == MediaKind.Series))
            .Select(item => new
            {
                item.Id, item.Kind, item.Title, item.Year, item.IdentityProvider, item.IdentityProviderId, item.Providers,
            })
            .ToListAsync(cancellationToken);
        if (works.Count == 0)
        {
            return [];
        }

        var watched = await WatchedWorkIdsAsync(context.AppUserId, cancellationToken);
        var eligible = works
            .Where(work => !watched.Contains(work.Id))
            .ToList();
        if (eligible.Count == 0)
        {
            return [];
        }

        var facetsByItem = await facets.ReadAsync([.. eligible.Select(work => work.Id)], cancellationToken);

        var candidates = new List<GeneratedCandidate>();
        foreach (var work in eligible)
        {
            var tmdbId = string.Equals(work.IdentityProvider, "tmdb", StringComparison.OrdinalIgnoreCase)
                ? work.IdentityProviderId
                : work.Providers.GetValueOrDefault("tmdb");
            if (tmdbId is null)
            {
                // Nothing downstream could merge it with a TMDb candidate or de-duplicate it.
                continue;
            }

            if (facetsByItem.GetValueOrDefault(work.Id) is not { } titleFacets ||
                context.Profile.Affinity(titleFacets) <= 0)
            {
                // A title the profile has nothing to say about is not a recommendation.
                continue;
            }

            var kind = work.Kind == MediaKind.Movie ? RecommendationKind.Movie : RecommendationKind.Series;
            var identity = new RecommendationIdentity(kind, tmdbId);
            if (context.SeedIdentities.Contains(identity))
            {
                continue;
            }

            // Contribution zero on purpose: this generator makes no collaborative claim at all. Its
            // candidates earn their place from the profile terms, which is exactly what "the library
            // ranked by the profile" means.
            candidates.Add(new GeneratedCandidate(
                identity,
                new TmdbRecommendedTitle(tmdbId, work.Title, work.Year, null),
                Contribution: 0,
                SeedTmdbId: null,
                MediaItemId: work.Id));
        }

        return candidates;
    }

    /// <summary>
    /// Works this user has watched — a movie played, or a series with any episode played.
    /// </summary>
    /// <remarks>A part-watched show belongs to Next Up, not to discovery.</remarks>
    private async Task<HashSet<Guid>> WatchedWorkIdsAsync(int appUserId, CancellationToken cancellationToken)
    {
        var played = await database.PlaybackHistoryEntries.AsNoTracking()
            .Where(entry => entry.AppUserId == appUserId)
            .Join(
                database.MediaItems.AsNoTracking(),
                entry => entry.MediaItemId,
                item => item.Id,
                (entry, item) => item.Kind == MediaKind.Episode && item.SeriesId != null ? item.SeriesId!.Value : item.Id)
            .Distinct()
            .ToListAsync(cancellationToken);

        var marked = await database.UserItemData.AsNoTracking()
            .Where(row => row.AppUserId == appUserId && row.Played)
            .Join(
                database.MediaItems.AsNoTracking(),
                row => row.MediaItemId,
                item => item.Id,
                (row, item) => item.Kind == MediaKind.Episode && item.SeriesId != null ? item.SeriesId!.Value : item.Id)
            .Distinct()
            .ToListAsync(cancellationToken);

        return [.. played, .. marked];
    }
}
