using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Recommendations.Profile;

/// <summary>
/// Builds one user's taste profile from local data, at zero request cost.
/// </summary>
/// <remarks>
/// Everything the engine needs to say something specific about a viewer is already in this database
/// and, until now, none of it was read: the ranking saw a play, a favorite and a rewatch, and nothing
/// about what the watched title actually <em>was</em>.
/// <para>
/// Unlike the seed set this is <b>not capped</b>. Seeds are capped because each one costs a TMDb
/// request; facets cost a join, so the profile is built from the whole history. Unrated watches
/// dominate it by volume and that is fine — the IDF damping is exactly what strips out whatever is
/// common to everything a viewer sees, leaving what distinguishes them, and that lives at the rated
/// end.
/// </para>
/// </remarks>
public sealed class TasteProfileBuilder(
    MediaServerDbContext database,
    TitleFacetReader facets,
    LibraryFacetIndexCache indexCache,
    TimeProvider time)
{
    /// <summary>
    /// A title the viewer put on their watchlist counts as intent, at this fraction of a plain watch.
    /// </summary>
    /// <remarks>
    /// Below a watch on purpose: wanting to see something is a weaker statement than having seen it,
    /// and an aspirational watchlist would otherwise outvote what a viewer actually does. Tracked
    /// titles feed the profile and are never emitted as recommendations — a title already wanted is
    /// not a suggestion.
    /// </remarks>
    internal const double WatchlistWeight = 0.4;

    /// <summary>What a hidden title contributes to the negative profile.</summary>
    /// <remarks>
    /// Well below a low rating: a hide is a judgement about a title the viewer never watched, so it
    /// carries real information about what they will not pick and very little about what they enjoy.
    /// </remarks>
    internal const double HideWeight = 0.5;

    /// <summary>What a started-and-abandoned title contributes to the negative profile.</summary>
    internal const double AbandonedWeight = 0.75;

    /// <summary>Below this fraction watched, a stopped title reads as abandoned rather than paused.</summary>
    internal const double AbandonedBelow = 0.15;

    /// <summary>Two stars: watchable with nothing else on. Faint praise, so it leans negative.</summary>
    internal const double TwoStarWeight = 0.5;

    /// <summary>One star: the time is the loss. The strongest negative the schema can carry.</summary>
    internal const double OneStarWeight = 1.0;

    /// <summary>
    /// A profile from the library itself, for a viewer who has not watched anything yet.
    /// </summary>
    /// <remarks>
    /// The second rung of the cold-start ladder. An operator chose to acquire every title in this
    /// library, and that is taste — weaker and less personal than a viewing history, but a real
    /// answer, and far better than the trending filler the feature refuses to serve. Every held work
    /// counts equally: there is no viewer to weigh them for, and inventing an order here would be
    /// dressing up the catalogue as a preference.
    /// </remarks>
    public async Task<TasteProfile> BuildFromLibraryAsync(CancellationToken cancellationToken)
    {
        var workIds = await database.MediaItems.AsNoTracking()
            .Where(item => item.RemovedAt == null && item.CatalogId != null &&
                (item.Kind == MediaKind.Movie || item.Kind == MediaKind.Series))
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        if (workIds.Count == 0)
        {
            return TasteProfile.Empty;
        }

        var signals = workIds.ToDictionary(id => id, _ => new TitleSignal(1));
        return await AssembleAsync(signals, cancellationToken);
    }

    public async Task<TasteProfile> BuildAsync(int appUserId, CancellationToken cancellationToken)
    {
        var signals = await SignalsAsync(appUserId, cancellationToken);
        return await AssembleAsync(signals, cancellationToken);
    }

    /// <summary>Turns weighted titles into normalized, damped facet vectors.</summary>
    private async Task<TasteProfile> AssembleAsync(
        Dictionary<Guid, TitleSignal> signals, CancellationToken cancellationToken)
    {
        if (signals.Count == 0)
        {
            return TasteProfile.Empty;
        }

        var byItem = await facets.ReadAsync([.. signals.Keys], cancellationToken);
        if (byItem.Count == 0)
        {
            return TasteProfile.Empty;
        }

        var index = await indexCache.GetAsync(database, facets, cancellationToken);

        var liked = new Dictionary<FacetFamily, Dictionary<string, double>>();
        var disliked = new Dictionary<FacetFamily, Dictionary<string, double>>();

        foreach (var (itemId, signal) in signals)
        {
            if (byItem.GetValueOrDefault(itemId) is not { } title)
            {
                continue;
            }

            var target = signal.Weight >= 0 ? liked : disliked;
            var weight = Math.Abs(signal.Weight);

            foreach (var facet in title.Facets)
            {
                var family = target.TryGetValue(facet.Family, out var vector)
                    ? vector
                    : target[facet.Family] = [];

                // Damped as it goes in, so a facet the whole library carries never accumulates into a
                // profile's loudest entry no matter how many titles contributed it.
                var contribution = weight * facet.Weight * index.Damping(facet.Family, facet.Value);
                family[facet.Value] = family.GetValueOrDefault(facet.Value, 0) + contribution;
            }
        }

        return new TasteProfile(Normalize(liked), Normalize(disliked));
    }

    /// <summary>
    /// Unit-length per family, so a cosine against any one of them is comparable with any other.
    /// </summary>
    private static IReadOnlyDictionary<FacetFamily, IReadOnlyDictionary<string, double>> Normalize(
        Dictionary<FacetFamily, Dictionary<string, double>> families)
    {
        var result = new Dictionary<FacetFamily, IReadOnlyDictionary<string, double>>(families.Count);
        foreach (var (family, vector) in families)
        {
            var magnitude = Math.Sqrt(vector.Values.Sum(value => value * value));
            if (magnitude <= 0)
            {
                continue;
            }

            result[family] = vector.ToDictionary(entry => entry.Key, entry => entry.Value / magnitude);
        }

        return result;
    }

    /// <summary>
    /// Every local title this user has said something about, and how strongly. Negative means the
    /// statement was against.
    /// </summary>
    private async Task<Dictionary<Guid, TitleSignal>> SignalsAsync(int appUserId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var signals = new Dictionary<Guid, TitleSignal>();

        var plays = await database.PlaybackHistoryEntries.AsNoTracking()
            .Where(entry => entry.AppUserId == appUserId)
            .Join(
                database.MediaItems.AsNoTracking(),
                entry => entry.MediaItemId,
                item => item.Id,
                (entry, item) => new
                {
                    entry.WatchedAt,
                    item.Kind,
                    // An episode play is a statement about its series, exactly as it is for seeds.
                    WorkId = item.Kind == MediaKind.Episode && item.SeriesId != null ? item.SeriesId!.Value : item.Id,
                })
            .Where(row => row.Kind == MediaKind.Movie || row.Kind == MediaKind.Episode)
            .ToListAsync(cancellationToken);

        var watchedWorkIds = plays.Select(row => row.WorkId).Distinct().ToList();

        var userData = await database.UserItemData.AsNoTracking()
            .Where(row => row.AppUserId == appUserId)
            .Select(row => new
            {
                row.MediaItemId, row.IsFavorite, row.Rating, row.Played, row.PlaybackPositionTicks,
            })
            .ToListAsync(cancellationToken);
        var userDataByItem = userData.ToDictionary(row => row.MediaItemId);

        foreach (var group in plays.GroupBy(row => row.WorkId))
        {
            var own = userDataByItem.GetValueOrDefault(group.Key);
            var rating = own?.Rating;
            var latest = group.Max(row => row.WatchedAt);
            var age = latest is { } when ? now - when : RecommendationSeedSelector.RecencyHalfLife * 4;

            if (rating is 1 or 2)
            {
                // Watched and rejected: the strongest negative available, and unlike a hide it needs
                // no "enough of them exist" threshold — a verdict after watching stands on its own.
                signals[group.Key] = new TitleSignal(-(rating == 1 ? OneStarWeight : TwoStarWeight));
                continue;
            }

            if (RecommendationSeedSelector.WeightOf(rating, own?.IsFavorite ?? false, age) is { } weight)
            {
                signals[group.Key] = new TitleSignal(weight);
            }
        }

        // Started, stopped early, never finished: the viewer's most honest negative that involves no
        // typing at all. Watched titles win the key — a film abandoned once and finished later is not
        // a rejection.
        var watched = watchedWorkIds.ToHashSet();
        var abandonedCandidates = userData
            .Where(row => !row.Played && row.PlaybackPositionTicks > 0 &&
                !watched.Contains(row.MediaItemId) && !signals.ContainsKey(row.MediaItemId))
            .ToList();

        if (abandonedCandidates.Count > 0)
        {
            // One grouped read rather than one per row: this list is unbounded in principle, and a
            // query per abandoned title would make the profile's cost scale with how much the viewer
            // has given up on.
            var candidateIds = abandonedCandidates.Select(row => row.MediaItemId).ToList();
            var runtimes = await database.MediaSources.AsNoTracking()
                .Where(source => candidateIds.Contains(source.MediaItemId))
                .GroupBy(source => source.MediaItemId)
                .Select(group => new { Id = group.Key, Ticks = group.Max(source => source.DurationTicks) })
                .ToDictionaryAsync(entry => entry.Id, entry => entry.Ticks, cancellationToken);

            foreach (var row in abandonedCandidates)
            {
                if (runtimes.GetValueOrDefault(row.MediaItemId) is var runtime && runtime > 0 &&
                    row.PlaybackPositionTicks < runtime * AbandonedBelow)
                {
                    signals[row.MediaItemId] = new TitleSignal(-AbandonedWeight);
                }
            }
        }

        // A hide is keyed by TMDb identity rather than by local item, because most hidden titles are
        // not held. Only the ones that are can contribute facets; the rest are already excluded from
        // the feed and have nothing local to say.
        var hidden = await database.RecommendationHides.AsNoTracking()
            .Where(hide => hide.AppUserId == appUserId)
            .Select(hide => new { hide.Kind, hide.TmdbId })
            .ToListAsync(cancellationToken);
        if (hidden.Count > 0)
        {
            var hiddenIds = hidden.Select(hide => hide.TmdbId).ToHashSet();
            var held = await database.MediaItems.AsNoTracking()
                .Where(item => item.RemovedAt == null && (item.Kind == MediaKind.Movie || item.Kind == MediaKind.Series))
                .Select(item => new { item.Id, item.IdentityProvider, item.IdentityProviderId })
                .ToListAsync(cancellationToken);

            foreach (var item in held)
            {
                if (string.Equals(item.IdentityProvider, "tmdb", StringComparison.OrdinalIgnoreCase) &&
                    item.IdentityProviderId is { } tmdbId && hiddenIds.Contains(tmdbId) &&
                    !signals.ContainsKey(item.Id))
                {
                    signals[item.Id] = new TitleSignal(-HideWeight);
                }
            }
        }

        // Explicit intent. Only tracked titles that resolved to a library item carry facets; a pure
        // wishlist row holds a display snapshot and nothing this can read, and fetching one would
        // break the promise that a profile costs no requests.
        var tracked = await database.WatchlistEntries.AsNoTracking()
            .Where(entry => entry.AppUserId == appUserId)
            .Join(
                database.TrackedTitles.AsNoTracking(),
                entry => entry.TrackedTitleId,
                title => title.Id,
                (entry, title) => title.MediaItemId)
            .Where(mediaItemId => mediaItemId != null)
            .Select(mediaItemId => mediaItemId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        foreach (var itemId in tracked.Where(itemId => !signals.ContainsKey(itemId)))
        {
            signals[itemId] = new TitleSignal(WatchlistWeight);
        }

        return signals;
    }

    private readonly record struct TitleSignal(double Weight);
}
