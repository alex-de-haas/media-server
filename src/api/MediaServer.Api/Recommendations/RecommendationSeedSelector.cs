using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Recommendations;

/// <summary>A title the user watched, and how much it should count when suggesting others.</summary>
/// <param name="Identity">TMDb coordinates of the seed itself.</param>
/// <param name="Weight">Rating-and-recency weight; higher pulls its recommendations up the feed.</param>
public sealed record RecommendationSeed(RecommendationIdentity Identity, double Weight);

/// <summary>
/// Chooses which watched titles drive the built-in engine.
/// </summary>
/// <remarks>
/// This is where "personalized" actually happens: TMDb only answers "what is like X", so the choice
/// of X — and how strongly each one counts — is the entire personalization.
/// <para>
/// Seeds come from the per-play history, ranked by what the viewer said about each title and then by
/// when they watched it. A star rating is a standing statement and does not fade; an unrated watch is
/// a moment and decays on a 90-day half-life, so among titles nobody graded one watched last week
/// still outweighs one watched last year.
/// </para>
/// </remarks>
public sealed class RecommendationSeedSelector(
    MediaServerDbContext database, TimeProvider time, RecommendationWeights? weights = null)
{
    /// <summary>How many seeds fan out to TMDb. Each is one request on a cold cache.</summary>
    internal const int MaxSeeds = 20;

    /// <summary>
    /// How many of those seeds are chosen purely by weight. The remainder is the recency reserve below.
    /// </summary>
    internal const int DefaultWeightedSeeds = 16;

    /// <summary>
    /// Slots held for the most recently watched eligible titles that weight alone would never admit.
    /// </summary>
    /// <remarks>
    /// Ratings do not decay (see <see cref="WeightOf"/>), so once twenty titles are rated three stars
    /// or better an unrated watch could never seed again — not rarely, never, however recent. That
    /// follows from the model, but it would leave the feed unable to notice what the viewer watched
    /// last week, and a "what should I watch next" that has not moved in a month is dead. These four
    /// slots are where a film watched recently and not yet rated lives.
    /// </remarks>
    internal int RecencySlots => MaxSeeds - Math.Clamp(_weights.WeightedSeeds, 0, MaxSeeds);

    /// <summary>An <em>unrated</em> seed watched this long ago counts half as much as one watched today.</summary>
    internal static readonly TimeSpan RecencyHalfLife = TimeSpan.FromDays(90);

    /// <summary>
    /// The curve, and everything else about weighting that can be argued over.
    /// </summary>
    /// <remarks>
    /// The rating curve is deliberately not linear. On this scale five stars means "nothing to fault",
    /// four is where most loved films land, and three is "a good film, no regrets" — so the qualitative
    /// break sits between three and four (×2.35) rather than between four and five (×1.6), and the top
    /// of the scale is reserved rather than merely high. One and two stars are absent from the table
    /// because they do not seed at all: asking TMDb what is like a film the viewer would not repeat
    /// spends one of the twenty requests fetching candidates the feed then has to push back down.
    /// <para>
    /// The numbers live in <see cref="RecommendationWeights"/> so the offline harness can sweep them.
    /// Nothing in the running app passes anything but the default.
    /// </para>
    /// </remarks>
    private readonly RecommendationWeights _weights = weights ?? RecommendationWeights.Default;

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

        var signals = await database.UserItemData.AsNoTracking()
            .Where(row => row.AppUserId == appUserId && seedItemIds.Contains(row.MediaItemId) &&
                (row.IsFavorite || row.Rating != null))
            .Select(row => new { row.MediaItemId, row.IsFavorite, row.Rating })
            .ToListAsync(cancellationToken);
        var favorite = signals.Where(row => row.IsFavorite).Select(row => row.MediaItemId).ToHashSet();
        var rating = signals
            .Where(row => row.Rating != null)
            .ToDictionary(row => row.MediaItemId, row => row.Rating!.Value);

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
            var age = latest is { } when ? now - when : RecencyHalfLife * 4;

            if (WeightOf(rating.TryGetValue(group.Key, out var stars) ? stars : null,
                    favorite.Contains(group.Key), age, _weights) is not { } weight)
            {
                // One or two stars: watched, and not worth asking for more of. Still evidence — the
                // taste profile reads these titles' facets as negatives — but never a seed.
                continue;
            }

            // Distinct plays, not distinct episodes: watching a series' episodes is not rewatching.
            var distinctPlays = item.Kind == MediaKind.Movie ? group.Count() : 1;
            if (distinctPlays > 1)
            {
                weight *= _weights.RewatchBoost;
            }

            seeds.Add((new RecommendationIdentity(kind, tmdbId), weight, latest));
        }

        // Weight first, then the reserve. Rated weights are discrete constants now, so the tiebreak is
        // where recency does its work inside a rating band: among forty films rated five stars, the
        // twenty watched most recently take the slots.
        var byWeight = seeds
            .OrderByDescending(seed => seed.Weight)
            // A stable tiebreak so an unchanged library produces an unchanged feed.
            .ThenByDescending(seed => seed.Latest ?? DateTimeOffset.MinValue)
            .ThenBy(seed => seed.Identity.TmdbId, StringComparer.Ordinal)
            .ToList();

        // Clamped, not trusted: the budget exists because each seed is a TMDb request, and a swept
        // configuration asking for more must not quietly spend them.
        var weightedSeeds = Math.Clamp(_weights.WeightedSeeds, 0, MaxSeeds);
        var chosen = byWeight.Take(weightedSeeds).ToList();
        if (byWeight.Count > weightedSeeds)
        {
            // The reserve: the most recently watched of what weight alone left behind. Only titles that
            // reached this list at all are eligible, so a one-star film cannot enter through the back
            // door — it was dropped above.
            chosen.AddRange(byWeight
                .Skip(weightedSeeds)
                .OrderByDescending(seed => seed.Latest ?? DateTimeOffset.MinValue)
                .ThenBy(seed => seed.Identity.TmdbId, StringComparer.Ordinal)
                .Take(RecencySlots));
        }

        return [.. chosen
            .OrderByDescending(seed => seed.Weight)
            .ThenByDescending(seed => seed.Latest ?? DateTimeOffset.MinValue)
            .ThenBy(seed => seed.Identity.TmdbId, StringComparer.Ordinal)
            .Select(seed => new RecommendationSeed(seed.Identity, seed.Weight))];
    }

    /// <summary>
    /// What one watched title is worth as a seed, or null when it should not seed at all.
    /// </summary>
    /// <remarks>
    /// The recency decay applies to the <b>unrated branch only</b>. A rating is a standing statement
    /// about taste and the way to revise it is to re-rate or clear it — decay is the engine guessing
    /// that someone has changed their mind, and once they can say so the guess is unnecessary. Left
    /// decaying, a five-star film from two years ago would be worth 0.02 against 1.0 for something
    /// watched yesterday and never thought about again, which has the statement exactly backwards.
    /// <para>
    /// A favorite still decays, and still counts only where no rating exists: the rating is the more
    /// specific statement about the same feeling, and compounding the two would price a single row at
    /// nearly ten ordinary viewings. Together these keep an instance where nobody rates anything
    /// ranking precisely as it ranked before ratings existed.
    /// </para>
    /// </remarks>
    internal static double? WeightOf(int? rating, bool favorite, TimeSpan age, RecommendationWeights? weights = null)
    {
        var tuning = weights ?? RecommendationWeights.Default;
        if (rating is { } stars)
        {
            return tuning.RatingWeights.TryGetValue(stars, out var weight) ? weight : null;
        }

        return Decay(age) * (favorite ? tuning.FavoriteBoost : tuning.UnratedWeight);
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
