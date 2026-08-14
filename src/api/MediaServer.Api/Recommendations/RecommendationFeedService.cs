using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Recommendations;

/// <summary>One card in the feed.</summary>
/// <param name="Kind">Movie or series.</param>
/// <param name="TmdbId">The shared coordinate every source and the library agree on.</param>
/// <param name="InLibrary">Whether this instance holds it — the difference between "play" and "discover".</param>
/// <param name="MediaItemId">
/// The local item, when held — and what a detail link must use: those routes are declared
/// <c>{id:guid}</c> and resolve by <see cref="MediaItem.Id"/>, so a public id would never match.
/// </param>
public sealed record RecommendationDto(
    string Kind,
    string TmdbId,
    string Title,
    int? Year,
    string? PosterUrl,
    bool InLibrary,
    Guid? MediaItemId,
    /// <summary>Why this card is here, as data the client phrases itself.</summary>
    RecommendationReason? Reason = null);

/// <summary>The feed plus what the UI needs to render its controls honestly.</summary>
/// <param name="Items">The ranked, filtered feed.</param>
/// <param name="PopularityBias">
/// Where this user's <b>Popular ↔ Deep cuts</b> dial sits, so the control can render its own state
/// rather than guessing at it.
/// </param>
/// <param name="MaxPopularityBias">The dial's far end, so the UI need not hardcode the server's range.</param>
public sealed record RecommendationFeedDto(
    IReadOnlyList<RecommendationDto> Items,
    double PopularityBias = 0,
    double MaxPopularityBias = RecommendationPreferenceStore.MaxPopularityBias,
    /// <summary>
    /// Which question the feed ended up answering, so the surface can say so rather than presenting a
    /// weaker answer as if it were the ordinary one. Null when no source had a ladder to report.
    /// </summary>
    string? Rung = null);

/// <summary>
/// Builds one user's feed: rank with the engine, then answer the questions only the library can —
/// is this already held, already watched, or already dismissed.
/// </summary>
/// <remarks>
/// The engine deliberately knows nothing about the local library's <em>state</em>. Watched and hidden
/// filtering lives here instead, so the ranking stays a statement about taste and the exclusions stay
/// a statement about this user's history.
/// <para>
/// There is one source, so there is nothing to fuse. Rank fusion existed to merge a scored list with
/// a connected account's positions-without-scores, and with that account gone it would only flatten
/// the engine's own shaped order back into ranks and re-derive it — losing, in the process, the
/// diversity the re-ranker had just imposed.
/// </para>
/// </remarks>
public sealed class RecommendationFeedService(
    MediaServerDbContext database,
    RecommendationEngine engine,
    ILogger<RecommendationFeedService> logger)
{
    /// <summary>How many the engine is asked for before filtering. Bounded, since each candidate costs work.</summary>
    internal const int PerRequest = 50;

    /// <summary>
    /// What the shelf asks for instead — an order of magnitude more, because it then discards most of
    /// it: only titles this instance holds survive.
    /// </summary>
    /// <remarks>
    /// Far cheaper than it looks. The `held` generator produces local titles directly, so the shelf no
    /// longer depends on TMDb having linked something to something else; the wide ask simply lets the
    /// ranking choose among the whole library rather than among whatever survived a narrow cut.
    /// </remarks>
    internal const int PerRequestForShelf = 500;

    public async Task<RecommendationFeedDto> BuildAsync(
        int appUserId, RecommendationKind? kind, int limit, CancellationToken cancellationToken)
    {
        // Rank generously, then filter: excluding watched and hidden titles afterwards would otherwise
        // eat into the limit and hand back a short feed.
        var ranked = await engine.RankAsync(appUserId, Math.Max(limit * 4, PerRequest), cancellationToken);
        var items = await ProjectAsync(appUserId, ranked.Candidates, kind, limit, cancellationToken);

        var preference = await database.RecommendationPreferences.AsNoTracking()
            .Where(row => row.AppUserId == appUserId)
            .Select(row => (double?)row.PopularityBias)
            .FirstOrDefaultAsync(cancellationToken);

        return new RecommendationFeedDto(items, preference ?? 0, Rung: ranked.Rung);
    }

    /// <summary>
    /// The held part of the feed, in rank order: the media items backing one user's Jellyfin shelf.
    /// </summary>
    /// <remarks>
    /// Two things separate this from <see cref="BuildAsync"/>, and both follow from the surface it
    /// feeds — one whose only verb is Play.
    /// <para>
    /// The in-library filter runs <em>before</em> the limit. Applying it afterwards would hand back a
    /// nearly empty shelf, because held titles are a small fraction of any provider's list.
    /// </para>
    /// <para>
    /// No poster lookup happens here at all: every surviving row is in the library and therefore has
    /// local artwork, so the TMDb call <see cref="WithPostersAsync"/> makes would buy nothing.
    /// </para>
    /// <para>
    /// Watched and hidden titles are deliberately <em>kept</em>. This is a candidate pool, not a
    /// finished row — the reader excludes them on every read, so a title leaves the shelf the moment
    /// it is played rather than when the shelf next expires.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Guid>> BuildShelfAsync(
        int appUserId, int limit, CancellationToken cancellationToken)
    {
        var ranked = await engine.RankAsync(appUserId, PerRequestForShelf, cancellationToken);
        if (ranked.Candidates.Count == 0)
        {
            return [];
        }

        var library = await LibraryByTmdbIdAsync(cancellationToken);

        var ids = new List<Guid>(limit);
        foreach (var entry in ranked.Candidates)
        {
            if (library.GetValueOrDefault(entry.Identity) is not { } held)
            {
                continue;
            }

            ids.Add(held.Representative.Id);
            if (ids.Count == limit)
            {
                break;
            }
        }

        logger.LogDebug("Shelf for user {User}: {Count} held titles from {Pool} candidates.",
            appUserId, ids.Count, ranked.Candidates.Count);

        return ids;
    }

    private async Task<List<RecommendationDto>> ProjectAsync(
        int appUserId,
        IReadOnlyList<RankedCandidate> ranked,
        RecommendationKind? kind,
        int limit,
        CancellationToken cancellationToken)
    {
        if (ranked.Count == 0)
        {
            return [];
        }

        var hidden = await HiddenAsync(appUserId, cancellationToken);
        var library = await LibraryByTmdbIdAsync(cancellationToken);
        var watched = await WatchedAsync(appUserId, library, cancellationToken);
        var localArtwork = await LocalArtworkAsync(library, cancellationToken);

        var items = new List<RecommendationDto>(limit);
        foreach (var entry in ranked)
        {
            if (kind is { } wanted && entry.Identity.Kind != wanted)
            {
                continue;
            }

            // Dismissed by this user, or already seen: neither belongs in "what next".
            if (hidden.Contains(entry.Identity) || watched.Contains(entry.Identity))
            {
                continue;
            }

            var held = library.GetValueOrDefault(entry.Identity)?.Representative;
            items.Add(new RecommendationDto(
                entry.Identity.Kind.ToString(),
                entry.Identity.TmdbId,
                // The library's own title wins when it holds the item: that is the name the user sees
                // everywhere else in this app.
                held?.Title ?? entry.Candidate.Title.Title,
                entry.Candidate.Title.Year,
                // A locally generated candidate carries no TMDb path — `held` and `collections`
                // synthesize their titles — so the library's own artwork is what it has.
                PosterUrl(entry.Candidate.Title.PosterPath)
                    ?? (held is null ? null : localArtwork.GetValueOrDefault(held.Id)),
                held is not null,
                held?.Id,
                entry.Candidate.Reason));

            if (items.Count == limit)
            {
                break;
            }
        }

        return items;
    }

    /// <summary>
    /// Every candidate carries its own artwork now.
    /// </summary>
    /// <remarks>
    /// There used to be a TMDb lookup and a cache behind it, because a connected account returned no
    /// artwork at all and a title only it suggested would render as a grey box. Both are gone: the
    /// widened cache shape carries <c>poster_path</c> inline on every TMDb candidate, and a title the
    /// library holds is read from <see cref="LocalArtworkAsync"/> instead.
    /// </remarks>
    private static string? PosterUrl(string? posterPath) =>
        string.IsNullOrWhiteSpace(posterPath) ? null : $"https://image.tmdb.org/t/p/w500{posterPath}";

    /// <summary>
    /// The primary artwork of every held title in the pool, so a locally generated candidate has a
    /// poster.
    /// </summary>
    /// <remarks>
    /// One read for the whole feed rather than one per card. `held` and `collections` build their
    /// candidates from library rows and have no TMDb path to carry, so without this every suggestion
    /// the instance already owns would render as "No poster" — the titles most worth showing.
    /// </remarks>
    private async Task<Dictionary<Guid, string>> LocalArtworkAsync(
        IReadOnlyDictionary<RecommendationIdentity, LibraryTitle> library, CancellationToken cancellationToken)
    {
        var itemIds = library.Values.Select(title => title.Representative.Id).Distinct().ToList();
        if (itemIds.Count == 0)
        {
            return [];
        }

        var images = await database.ImageAssets.AsNoTracking()
            .Where(image => itemIds.Contains(image.MediaItemId) && image.ImageType == ImageType.Primary)
            .GroupBy(image => image.MediaItemId)
            .Select(group => new
            {
                MediaItemId = group.Key,
                Url = group.OrderBy(image => image.SortOrder).Select(image => image.RemotePath).First(),
            })
            .ToListAsync(cancellationToken);

        return images.ToDictionary(image => image.MediaItemId, image => image.Url);
    }

    private async Task<HashSet<RecommendationIdentity>> HiddenAsync(
        int appUserId, CancellationToken cancellationToken)
    {
        var rows = await database.RecommendationHides.AsNoTracking()
            .Where(hide => hide.AppUserId == appUserId)
            .Select(hide => new { hide.Kind, hide.TmdbId })
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => new RecommendationIdentity(row.Kind, row.TmdbId))];
    }

    /// <summary>
    /// One title the library holds: every local copy of it, plus the one whose id the card links to.
    /// </summary>
    /// <remarks>
    /// Several catalogs can hold the same title (a 4K edition beside a regular one). Keeping only one
    /// copy would be enough to say "you have this", but not enough to say "you watched this" — a play
    /// recorded against the other copy would be missed and the title recommended anyway.
    /// </remarks>
    private sealed record LibraryTitle(MediaItem Representative, IReadOnlyList<Guid> CopyIds);

    /// <summary>Every movie and series the library holds, keyed by the coordinate the feed speaks.</summary>
    private async Task<Dictionary<RecommendationIdentity, LibraryTitle>> LibraryByTmdbIdAsync(
        CancellationToken cancellationToken)
    {
        // Published only: a tombstone is a deleted title, and "you already have this" must not be
        // claimed for something the user removed (nor may its ghost id become a dead detail link).
        var items = await database.MediaItems.AsNoTracking()
            .Where(item => item.PublicId != null && (item.Kind == MediaKind.Movie || item.Kind == MediaKind.Series))
            .ToListAsync(cancellationToken);

        var copies = new Dictionary<RecommendationIdentity, List<MediaItem>>();
        foreach (var item in items)
        {
            if (RecommendationSeedSelector.TmdbIdOf(item) is not { } tmdbId)
            {
                continue;
            }

            var kind = item.Kind == MediaKind.Movie ? RecommendationKind.Movie : RecommendationKind.Series;
            var identity = new RecommendationIdentity(kind, tmdbId);
            if (copies.TryGetValue(identity, out var existing))
            {
                existing.Add(item);
            }
            else
            {
                copies[identity] = [item];
            }
        }

        return copies.ToDictionary(
            pair => pair.Key,
            // Oldest copy as the representative, so the link a user follows does not change when a
            // second edition is added.
            pair => new LibraryTitle(
                pair.Value.OrderBy(item => item.AddedAt).First(),
                [.. pair.Value.Select(item => item.Id)]));
    }

    /// <summary>
    /// Titles this user has already seen. A movie counts when played; a series counts once any episode
    /// has been — a part-watched show belongs to Next Up, not to discovery.
    /// </summary>
    private async Task<HashSet<RecommendationIdentity>> WatchedAsync(
        int appUserId,
        Dictionary<RecommendationIdentity, LibraryTitle> library,
        CancellationToken cancellationToken)
    {
        if (library.Count == 0)
        {
            return [];
        }

        // Every copy, not just the representative: watching the 4K edition counts.
        var itemIds = library.Values.SelectMany(title => title.CopyIds).ToHashSet();

        var playedItemIds = await database.UserItemData.AsNoTracking()
            .Where(row => row.AppUserId == appUserId && row.Played)
            .Select(row => row.MediaItemId)
            .ToListAsync(cancellationToken);

        // An episode play marks its series watched, which is why this joins through SeriesId.
        var playedSeriesIds = await database.PlaybackHistoryEntries.AsNoTracking()
            .Where(entry => entry.AppUserId == appUserId)
            .Join(
                database.MediaItems.AsNoTracking(),
                entry => entry.MediaItemId,
                item => item.Id,
                (_, item) => item.Kind == MediaKind.Episode && item.SeriesId != null ? item.SeriesId!.Value : item.Id)
            .Distinct()
            .ToListAsync(cancellationToken);

        var seen = playedItemIds.Concat(playedSeriesIds).Where(itemIds.Contains).ToHashSet();

        return [.. library
            .Where(pair => pair.Value.CopyIds.Any(seen.Contains))
            .Select(pair => pair.Key)];
    }

    /// <summary>Hides a title from this user's feed. Idempotent: hiding twice is the same intent.</summary>
    public async Task HideAsync(
        int appUserId, RecommendationIdentity identity, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var exists = await database.RecommendationHides.AnyAsync(
            hide => hide.AppUserId == appUserId && hide.Kind == identity.Kind && hide.TmdbId == identity.TmdbId,
            cancellationToken);

        if (exists)
        {
            return;
        }

        database.RecommendationHides.Add(new RecommendationHide
        {
            Id = Guid.NewGuid(), AppUserId = appUserId, Kind = identity.Kind, TmdbId = identity.TmdbId, CreatedAt = now,
        });

        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Raced with another tab hiding the same card; the intent is satisfied either way.
            database.ChangeTracker.Clear();
        }
    }

    /// <summary>Restores a hidden title — what the undo on the hide toast calls.</summary>
    public async Task UnhideAsync(
        int appUserId, RecommendationIdentity identity, CancellationToken cancellationToken)
    {
        var hide = await database.RecommendationHides.FirstOrDefaultAsync(
            row => row.AppUserId == appUserId && row.Kind == identity.Kind && row.TmdbId == identity.TmdbId,
            cancellationToken);

        if (hide is null)
        {
            return;
        }

        database.RecommendationHides.Remove(hide);
        await database.SaveChangesAsync(cancellationToken);
    }
}
