using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Recommendations;

/// <summary>A title the user watched, and how much it should count when suggesting others.</summary>
/// <param name="Identity">TMDb coordinates of the seed itself.</param>
/// <param name="Weight">Recency-and-affection weight; higher pulls its recommendations up the feed.</param>
public sealed record RecommendationSeed(RecommendationIdentity Identity, double Weight);

/// <summary>
/// Chooses which watched titles drive the built-in engine.
/// </summary>
/// <remarks>
/// This is where "personalized" actually happens: TMDb only answers "what is like X", so the choice
/// of X — and how strongly each one counts — is the entire personalization. Seeds come from the
/// per-play history, so a title watched last week outweighs one watched last year.
/// </remarks>
public sealed class RecommendationSeedSelector(MediaServerDbContext database, TimeProvider time)
{
    /// <summary>How many seeds fan out to TMDb. Each is one request on a cold cache.</summary>
    internal const int MaxSeeds = 20;

    /// <summary>A seed watched this long ago counts half as much as one watched today.</summary>
    internal static readonly TimeSpan RecencyHalfLife = TimeSpan.FromDays(90);

    /// <summary>A favorite says something an ordinary play does not, so it counts for more.</summary>
    internal const double FavoriteBoost = 1.5;

    /// <summary>Rewatching is the strongest signal a viewer gives without saying anything.</summary>
    internal const double RewatchBoost = 1.25;

    public async Task<IReadOnlyList<RecommendationSeed>> SelectAsync(
        int appUserId, CancellationToken cancellationToken)
    {
        var plays = await database.PlaybackHistoryEntries.AsNoTracking()
            .Where(entry => entry.AppUserId == appUserId)
            .Join(
                database.MediaItems.AsNoTracking(),
                entry => entry.MediaItemId,
                item => item.Id,
                (entry, item) => new
                {
                    entry.WatchedAt,
                    item.Id,
                    item.Kind,
                    // An episode seeds its series: "more like this show", never "more like episode 4".
                    SeedItemId = item.Kind == MediaKind.Episode && item.SeriesId != null ? item.SeriesId!.Value : item.Id,
                })
            .Where(row => row.Kind == MediaKind.Movie || row.Kind == MediaKind.Episode)
            .ToListAsync(cancellationToken);

        if (plays.Count == 0)
        {
            return [];
        }

        var seedItemIds = plays.Select(row => row.SeedItemId).Distinct().ToList();
        var seedItems = await database.MediaItems.AsNoTracking()
            .Where(item => seedItemIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);

        var favorites = await database.UserItemData.AsNoTracking()
            .Where(row => row.AppUserId == appUserId && row.IsFavorite && seedItemIds.Contains(row.MediaItemId))
            .Select(row => row.MediaItemId)
            .ToListAsync(cancellationToken);
        var favorite = favorites.ToHashSet();

        var now = time.GetUtcNow();
        var seeds = new List<(RecommendationIdentity Identity, double Weight, DateTimeOffset? Latest)>();

        foreach (var group in plays.GroupBy(row => row.SeedItemId))
        {
            if (!seedItems.TryGetValue(group.Key, out var item) || TmdbIdOf(item) is not { } tmdbId)
            {
                // Unidentified, or identified by something other than TMDb: nothing to ask TMDb about.
                continue;
            }

            var kind = item.Kind == MediaKind.Movie ? RecommendationKind.Movie : RecommendationKind.Series;

            // Undated plays still count — a manual mark says "watched", and dropping it would make a
            // library migrated from aggregate counts look like nobody had seen anything. They simply
            // carry no recency bonus.
            var latest = group.Max(row => row.WatchedAt);
            var weight = latest is { } when ? Decay(now - when) : Decay(RecencyHalfLife * 4);

            if (favorite.Contains(group.Key))
            {
                weight *= FavoriteBoost;
            }

            // Distinct plays, not distinct episodes: watching a series' episodes is not rewatching.
            var distinctPlays = item.Kind == MediaKind.Movie ? group.Count() : 1;
            if (distinctPlays > 1)
            {
                weight *= RewatchBoost;
            }

            seeds.Add((new RecommendationIdentity(kind, tmdbId), weight, latest));
        }

        return [.. seeds
            .OrderByDescending(seed => seed.Weight)
            // A stable tiebreak so an unchanged library produces an unchanged feed.
            .ThenByDescending(seed => seed.Latest ?? DateTimeOffset.MinValue)
            .ThenBy(seed => seed.Identity.TmdbId, StringComparer.Ordinal)
            .Take(MaxSeeds)
            .Select(seed => new RecommendationSeed(seed.Identity, seed.Weight))];
    }

    /// <summary>Exponential decay on the half-life: today is 1.0, one half-life ago is 0.5.</summary>
    private static double Decay(TimeSpan age) =>
        Math.Pow(0.5, Math.Max(age.TotalDays, 0) / RecencyHalfLife.TotalDays);

    /// <summary>The item's own TMDb id — for a series this is the series id, which is how it is identified.</summary>
    internal static string? TmdbIdOf(MediaItem item) =>
        string.Equals(item.IdentityProvider, "tmdb", StringComparison.OrdinalIgnoreCase)
            ? item.IdentityProviderId
            : item.Providers.GetValueOrDefault("tmdb");
}
