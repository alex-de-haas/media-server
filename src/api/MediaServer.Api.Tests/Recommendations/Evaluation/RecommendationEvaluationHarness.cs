using MediaServer.Api.Data;
using MediaServer.Api.Recommendations;
using MediaServer.Api.Recommendations.Generation;
using MediaServer.Api.Recommendations.Profile;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Recommendations.Evaluation;

/// <summary>What one configuration scored against a held-out future.</summary>
/// <param name="Users">How many users had enough history to evaluate.</param>
/// <param name="HeldOut">How many plays were hidden and then looked for.</param>
/// <param name="RecallAt20">Share of held-out titles the top twenty contained.</param>
/// <param name="NdcgAt20">The same, discounted by where in the twenty each hit landed.</param>
public sealed record EvaluationResult(int Users, int HeldOut, double RecallAt20, double NdcgAt20)
{
    public override string ToString() =>
        $"users={Users} heldout={HeldOut} recall@20={RecallAt20:F4} nDCG@20={NdcgAt20:F4}";
}

/// <summary>
/// Measures the engine against what viewers actually went on to watch.
/// </summary>
/// <remarks>
/// Every weight in this feature is an argument about how much a signal is worth, and until something
/// checks them they are only plausible. This is the check: hide each user's most recent plays, build
/// the engine from what is left, and see whether the titles they went on to watch come back near the
/// top.
/// <para>
/// It lives in the test project on purpose. It is a measuring instrument, not app behaviour, and it
/// is only meaningful against a <b>real</b> history — a sweep over synthetic data measures the
/// generator that produced the data. The synthetic tests beside it check that the arithmetic is
/// right; the numbers that matter come from pointing it at a real database.
/// </para>
/// <para>
/// The held-out plays are deleted inside a transaction that is always rolled back. They have to
/// actually leave, because the engine excludes watched titles: left in place they could never be
/// recommended, and the measurement would be of nothing.
/// </para>
/// </remarks>
public sealed class RecommendationEvaluationHarness(Func<MediaServerDbContext> newContext, TimeProvider time)
{
    /// <summary>The cut every metric here is named for.</summary>
    internal const int At = 20;

    /// <summary>Share of a user's most recent plays held out as the future to predict.</summary>
    internal const double HoldOutShare = 0.2;

    /// <summary>Below this many plays a user cannot be split into a past and a future worth measuring.</summary>
    internal const int MinimumPlays = 5;

    public async Task<EvaluationResult> RunAsync(
        RecommendationWeights weights, CancellationToken cancellationToken = default)
    {
        await using var context = newContext();
        var userIds = await context.PlaybackHistoryEntries.AsNoTracking()
            .Select(entry => entry.AppUserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var users = 0;
        var heldOutTotal = 0;
        var recallSum = 0d;
        var ndcgSum = 0d;

        foreach (var userId in userIds)
        {
            if (await EvaluateUserAsync(userId, weights, cancellationToken) is not { } scored)
            {
                continue;
            }

            users++;
            heldOutTotal += scored.HeldOut;
            recallSum += scored.Recall;
            ndcgSum += scored.Ndcg;
        }

        return users == 0
            ? new EvaluationResult(0, 0, 0, 0)
            : new EvaluationResult(users, heldOutTotal, recallSum / users, ndcgSum / users);
    }

    /// <summary>Runs several configurations over the same history, so the numbers are comparable.</summary>
    public async Task<IReadOnlyList<(string Label, EvaluationResult Result)>> SweepAsync(
        IReadOnlyList<(string Label, RecommendationWeights Weights)> configurations,
        CancellationToken cancellationToken = default)
    {
        var results = new List<(string, EvaluationResult)>(configurations.Count);
        foreach (var (label, weights) in configurations)
        {
            results.Add((label, await RunAsync(weights, cancellationToken)));
        }

        return results;
    }

    private async Task<(int HeldOut, double Recall, double Ndcg)?> EvaluateUserAsync(
        int userId, RecommendationWeights weights, CancellationToken cancellationToken)
    {
        await using var context = newContext();
        var plays = await context.PlaybackHistoryEntries
            .Where(entry => entry.AppUserId == userId && entry.WatchedAt != null)
            .OrderBy(entry => entry.WatchedAt)
            .ToListAsync(cancellationToken);
        if (plays.Count < MinimumPlays)
        {
            return null;
        }

        var holdOutCount = Math.Clamp((int)Math.Round(plays.Count * HoldOutShare), 1, At);
        var heldOut = plays.TakeLast(holdOutCount).ToList();

        var future = await FutureIdentitiesAsync(context, heldOut, cancellationToken);
        if (future.Count == 0)
        {
            // Nothing identifiable to predict: unmatched titles would score every configuration zero
            // and drag the average toward a number about the metadata rather than about the ranking.
            return null;
        }

        // The rollback is what makes this safe to point at a real database.
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            context.PlaybackHistoryEntries.RemoveRange(heldOut);
            await context.SaveChangesAsync(cancellationToken);

            var ranked = await EngineFor(context, weights)
                .RankAsync(userId, At, cancellationToken);
            var predicted = ranked.Candidates.Select(entry => entry.Identity).ToList();

            return (future.Count, Recall(predicted, future), Ndcg(predicted, future));
        }
        finally
        {
            await transaction.RollbackAsync(cancellationToken);
        }
    }

    /// <summary>The TMDb identities behind the held-out plays — what the engine had to guess.</summary>
    private static async Task<HashSet<RecommendationIdentity>> FutureIdentitiesAsync(
        MediaServerDbContext context,
        IReadOnlyList<PlaybackHistoryEntry> heldOut,
        CancellationToken cancellationToken)
    {
        var itemIds = heldOut.Select(entry => entry.MediaItemId).Distinct().ToList();
        var items = await context.MediaItems.AsNoTracking()
            .Where(item => itemIds.Contains(item.Id))
            .Select(item => new { item.Id, item.Kind, item.SeriesId })
            .ToListAsync(cancellationToken);

        // An episode play is a statement about its series, exactly as it is for seeds.
        var workIds = items
            .Select(item => item.Kind == MediaKind.Episode && item.SeriesId is { } series ? series : item.Id)
            .Distinct()
            .ToList();

        var works = await context.MediaItems.AsNoTracking()
            .Where(item => workIds.Contains(item.Id))
            .Select(item => new { item.Kind, item.IdentityProvider, item.IdentityProviderId })
            .ToListAsync(cancellationToken);

        return [.. works
            .Where(work => string.Equals(work.IdentityProvider, "tmdb", StringComparison.OrdinalIgnoreCase) &&
                work.IdentityProviderId is not null)
            .Select(work => new RecommendationIdentity(
                work.Kind == MediaKind.Movie ? RecommendationKind.Movie : RecommendationKind.Series,
                work.IdentityProviderId!))];
    }

    /// <summary>Share of the future the top twenty contained.</summary>
    internal static double Recall(
        IReadOnlyList<RecommendationIdentity> predicted, IReadOnlySet<RecommendationIdentity> future) =>
        future.Count == 0 ? 0 : (double)predicted.Take(At).Count(future.Contains) / future.Count;

    /// <summary>
    /// The same, discounted by position: a hit at rank one is worth more than one at rank twenty.
    /// </summary>
    /// <remarks>
    /// Normalized against the best achievable ordering for this user, so a viewer with three held-out
    /// titles and a viewer with twenty are on the same scale.
    /// </remarks>
    internal static double Ndcg(
        IReadOnlyList<RecommendationIdentity> predicted, IReadOnlySet<RecommendationIdentity> future)
    {
        if (future.Count == 0)
        {
            return 0;
        }

        var dcg = 0d;
        var top = predicted.Take(At).ToList();
        for (var index = 0; index < top.Count; index++)
        {
            if (future.Contains(top[index]))
            {
                dcg += 1 / Math.Log2(index + 2);
            }
        }

        var ideal = 0d;
        for (var index = 0; index < Math.Min(future.Count, At); index++)
        {
            ideal += 1 / Math.Log2(index + 2);
        }

        return ideal == 0 ? 0 : dcg / ideal;
    }

    /// <summary>
    /// The whole engine, wired the way the app wires it, minus the generators that reach the network.
    /// </summary>
    /// <remarks>
    /// A run against a real history must not spend the operator's TMDb budget or depend on being
    /// online, so `similar`, `people` and `discover` are left out and the cached `seeds` lists carry
    /// the collaborative signal. That is a real limitation of the measurement and is worth saying:
    /// this scores the ranking and the profile, not the reach of the generators.
    /// </remarks>
    private RecommendationEngine EngineFor(MediaServerDbContext context, RecommendationWeights weights)
    {
        var facets = new TitleFacetReader(context);
        var indexCache = new LibraryFacetIndexCache();
        var source = new CachedOnlyTmdbSource(context, time);

        return new RecommendationEngine(
            context,
            new RecommendationSeedSelector(context, time, weights),
            [
                SeedListGenerator.Recommendations(source),
                new CollectionsGenerator(context),
                new HeldGenerator(context, facets),
            ],
            facets,
            new TasteProfileCache(),
            new TasteProfileBuilder(context, facets, indexCache, time),
            new RecommendationScorer(weights),
            new RecommendationReranker(),
            new RecommendationPreferenceStore(context),
            NullLogger<RecommendationEngine>.Instance);
    }

    /// <summary>
    /// Reads the recommendation cache and never the network.
    /// </summary>
    /// <remarks>
    /// Stale rows are served rather than refreshed: for a measurement, a week-old list that every
    /// configuration sees identically is better than a fresh one that costs requests and makes two
    /// runs incomparable.
    /// </remarks>
    private sealed class CachedOnlyTmdbSource(MediaServerDbContext context, TimeProvider time) : ITmdbRecommendationSource
    {
        public async Task<IReadOnlyList<TmdbRecommendedTitle>> ForSeedAsync(
            RecommendationIdentity seed, TmdbRecommendationGenerator generator, CancellationToken cancellationToken)
        {
            var row = await context.TmdbRecommendationCache.AsNoTracking().FirstOrDefaultAsync(
                entry => entry.Generator == generator && entry.Kind == seed.Kind && entry.TmdbId == seed.TmdbId,
                cancellationToken);

            return row is null ? [] : Read(row.Payload);
        }

        public Task<IReadOnlyList<TmdbRecommendedTitle>> ForListAsync(
            TmdbRecommendationGenerator generator,
            RecommendationKind kind,
            string cacheKey,
            string path,
            TimeSpan lifetime,
            IReadOnlyList<string> arrays,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TmdbRecommendedTitle>>([]);

        private static IReadOnlyList<TmdbRecommendedTitle> Read(string payload)
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<List<TmdbRecommendedTitle>>(
                    payload, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) ?? [];
            }
            catch (System.Text.Json.JsonException)
            {
                return [];
            }
        }
    }
}
