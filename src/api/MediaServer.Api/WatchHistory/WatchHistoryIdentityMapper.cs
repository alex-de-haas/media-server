using System.Globalization;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.WatchHistory;

/// <summary>Why a local item could not be described to a provider.</summary>
public enum WatchHistoryIdentityIssue
{
    /// <summary>Nothing wrong.</summary>
    None,

    /// <summary>Not a movie or episode — a folder, or an extra with no counterpart to sync.</summary>
    UnsupportedKind,

    /// <summary>No TMDb or IMDb id, on the item or its series. Usually an unidentified file.</summary>
    MissingExternalId,

    /// <summary>An episode without canonical season/episode numbers, or with impossible ones.</summary>
    MissingEpisodeNumbering,

    /// <summary>
    /// The item is gone — deleted, or a stale id in queued work. Distinct from being unidentified,
    /// because the remedy is different: nothing about re-identifying a row that no longer exists.
    /// </summary>
    ItemNotFound,

    /// <summary>
    /// More than one local item resolves to the same provider identity. Acting on one of them would
    /// be arbitrary and could clear another edition's resume point, so the caller is told instead.
    /// </summary>
    AmbiguousLocalIdentity,
}

/// <summary>An identity, or the reason there isn't one.</summary>
public sealed record WatchHistoryIdentityResult(WatchHistoryIdentity? Identity, WatchHistoryIdentityIssue Issue)
{
    public bool Resolved => Identity is not null;

    public static WatchHistoryIdentityResult Ok(WatchHistoryIdentity identity) => new(identity, WatchHistoryIdentityIssue.None);

    public static WatchHistoryIdentityResult Failed(WatchHistoryIdentityIssue issue) => new(null, issue);
}

/// <summary>
/// Turns a local <see cref="MediaItem"/> into the provider-neutral identity an adapter can act on.
/// </summary>
/// <remarks>
/// This is the only place that knows how local identification maps onto the coordinates providers
/// use. Episodes are addressed by their <b>series</b> id plus canonical season and episode numbers —
/// providers have no notion of a per-episode external id — and the ingest pipeline already stores the
/// series' TMDb id on every episode row, so the lookup is usually local. A missing id is reported
/// rather than guessed: sending a wrong identity writes a viewing onto someone else's title.
/// </remarks>
public sealed class WatchHistoryIdentityMapper(MediaServerDbContext database)
{
    public async Task<WatchHistoryIdentityResult> MapAsync(Guid mediaItemId, CancellationToken cancellationToken)
    {
        var item = await database.MediaItems.AsNoTracking()
            .FirstOrDefaultAsync(entry => entry.Id == mediaItemId, cancellationToken);

        return item is null
            ? WatchHistoryIdentityResult.Failed(WatchHistoryIdentityIssue.ItemNotFound)
            : await MapAsync(item, cancellationToken);
    }

    public async Task<WatchHistoryIdentityResult> MapAsync(MediaItem item, CancellationToken cancellationToken)
    {
        switch (item.Kind)
        {
            case MediaKind.Movie:
                var (movieTmdb, movieImdb) = ExternalIds(item);
                if (movieTmdb is null && movieImdb is null)
                {
                    return WatchHistoryIdentityResult.Failed(WatchHistoryIdentityIssue.MissingExternalId);
                }

                return WatchHistoryIdentityResult.Ok(new WatchHistoryIdentity
                {
                    Kind = WatchHistoryMediaKind.Movie,
                    TmdbId = movieTmdb,
                    ImdbId = movieImdb,
                });

            case MediaKind.Episode:
                return await MapEpisodeAsync(item, cancellationToken);

            // Seasons and series are expanded to their episodes before they reach a provider, and a
            // video extra has nothing on the other side to record.
            default:
                return WatchHistoryIdentityResult.Failed(WatchHistoryIdentityIssue.UnsupportedKind);
        }
    }

    private async Task<WatchHistoryIdentityResult> MapEpisodeAsync(MediaItem episode, CancellationToken cancellationToken)
    {
        // Canonical numbering first: identification re-maps some releases (anime absolute numbering,
        // for instance) onto the provider's season/episode, and that mapping is what a provider must
        // be told. Same precedence as PublicIdFactory.
        var season = episode.IdentitySeasonNumber ?? episode.ParentIndexNumber;
        var number = episode.IdentityEpisodeNumber ?? episode.IndexNumber;
        if (season is null || number is null)
        {
            return WatchHistoryIdentityResult.Failed(WatchHistoryIdentityIssue.MissingEpisodeNumbering);
        }

        // The ingest pipeline copies the series' ids onto each episode, so this is normally local.
        // Falling back to the series row keeps older or partially identified libraries working.
        var (tmdb, imdb) = ExternalIds(episode);
        if (tmdb is null && imdb is null && episode.SeriesId is { } seriesId)
        {
            var series = await database.MediaItems.AsNoTracking()
                .FirstOrDefaultAsync(entry => entry.Id == seriesId, cancellationToken);
            if (series is not null)
            {
                (tmdb, imdb) = ExternalIds(series);
            }
        }

        if (tmdb is null && imdb is null)
        {
            return WatchHistoryIdentityResult.Failed(WatchHistoryIdentityIssue.MissingExternalId);
        }

        var identity = new WatchHistoryIdentity
        {
            Kind = WatchHistoryMediaKind.Episode,
            TmdbId = tmdb,
            ImdbId = imdb,
            SeasonNumber = season,
            EpisodeNumber = number,
            // Only a genuine range: a file holding one episode leaves this null so nothing downstream
            // has to re-interpret a degenerate value. The range end is a file-level fact, so it is
            // offset from the canonical first episode rather than read from the display numbering.
            EpisodeNumberEnd = episode.IndexNumberEnd > episode.IndexNumber
                ? number + (episode.IndexNumberEnd - episode.IndexNumber)
                : null,
        };

        return identity.IsResolvable
            ? WatchHistoryIdentityResult.Ok(identity)
            : WatchHistoryIdentityResult.Failed(WatchHistoryIdentityIssue.MissingEpisodeNumbering);
    }

    /// <summary>
    /// Expands a season or series into the episodes a provider can be told about, so a bulk mark
    /// becomes per-episode entries. Unmappable descendants are reported rather than dropped silently.
    /// </summary>
    public async Task<IReadOnlyList<(Guid MediaItemId, WatchHistoryIdentityResult Result)>> MapDescendantsAsync(
        MediaItem folder, CancellationToken cancellationToken)
    {
        var episodes = folder.Kind switch
        {
            MediaKind.Series => await Ordered(database.MediaItems.AsNoTracking()
                .Where(entry => entry.Kind == MediaKind.Episode && entry.SeriesId == folder.Id))
                .ToListAsync(cancellationToken),
            MediaKind.Season => await Ordered(database.MediaItems.AsNoTracking()
                .Where(entry => entry.Kind == MediaKind.Episode && entry.SeasonId == folder.Id))
                .ToListAsync(cancellationToken),
            _ => [],
        };

        var mapped = new List<(Guid, WatchHistoryIdentityResult)>(episodes.Count);
        foreach (var episode in episodes)
        {
            mapped.Add((episode.Id, await MapAsync(episode, cancellationToken)));
        }

        return mapped;
    }

    // Ordered in the database rather than after loading: a long-running series is a lot of rows to
    // sort in memory for no reason.
    private static IQueryable<MediaItem> Ordered(IQueryable<MediaItem> episodes) =>
        episodes
            .OrderBy(entry => entry.IdentitySeasonNumber ?? entry.ParentIndexNumber)
            .ThenBy(entry => entry.IdentityEpisodeNumber ?? entry.IndexNumber);

    /// <summary>
    /// True when another local item in the same catalog scope resolves to the same provider identity.
    /// </summary>
    /// <remarks>
    /// Two editions of one film are one work to a provider. Outbound writes stay independent — a play
    /// is a play — but anything that would <i>apply</i> a remote state locally has to know, because
    /// picking one row is arbitrary and updating both can clear an edition's resume point.
    /// </remarks>
    public async Task<bool> HasAmbiguousLocalIdentityAsync(
        MediaItem item, IReadOnlyCollection<Guid>? catalogScope, CancellationToken cancellationToken)
    {
        var mapped = await MapAsync(item, cancellationToken);
        if (!mapped.Resolved || item.Kind != MediaKind.Movie)
        {
            // Episodes are addressed by series plus coordinates, so a duplicate would have to be a
            // duplicate episode row — a library fault rather than an edition.
            return false;
        }

        var candidates = database.MediaItems.AsNoTracking()
            .Where(entry => entry.Kind == MediaKind.Movie && entry.Id != item.Id);
        if (catalogScope is { Count: > 0 })
        {
            candidates = candidates.Where(entry => catalogScope.Contains(entry.CatalogId));
        }

        // The canonical identity is indexed by (CatalogId, IdentityProvider, IdentityProviderId), so
        // the common case is an index probe rather than reading every movie in the library.
        if (!string.IsNullOrWhiteSpace(item.IdentityProvider) && !string.IsNullOrWhiteSpace(item.IdentityProviderId))
        {
            if (await candidates.AnyAsync(
                    entry => entry.IdentityProvider == item.IdentityProvider
                        && entry.IdentityProviderId == item.IdentityProviderId,
                    cancellationToken))
            {
                return true;
            }

            // Rows written before identification recorded a canonical id still carry the provider map,
            // so fall through to compare those rather than declare them unique.
            candidates = candidates.Where(entry => entry.IdentityProviderId == null);
        }

        var identity = mapped.Identity!;
        foreach (var candidate in await candidates.ToListAsync(cancellationToken))
        {
            var (tmdb, imdb) = ExternalIds(candidate);
            if ((identity.TmdbId is not null && tmdb == identity.TmdbId)
                || (identity.ImdbId is not null && string.Equals(imdb, identity.ImdbId, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static (int? Tmdb, string? Imdb) ExternalIds(MediaItem item)
    {
        int? tmdb = null;
        string? imdb = null;

        // The canonical identity chosen at identification time wins over the display-facing provider
        // map — it is the field the rest of the pipeline keys on (see PublicIdFactory) and, for an
        // episode, it holds the *series* id, which is precisely what a provider needs.
        if (!string.IsNullOrWhiteSpace(item.IdentityProviderId))
        {
            if (string.Equals(item.IdentityProvider, "tmdb", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(item.IdentityProviderId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var canonical)
                && canonical > 0)
            {
                tmdb = canonical;
            }
            else if (string.Equals(item.IdentityProvider, "imdb", StringComparison.OrdinalIgnoreCase))
            {
                imdb = item.IdentityProviderId.Trim();
            }
        }

        foreach (var (key, value) in item.Providers)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            // Provider keys are stored lowercase by the pipeline but compared case-insensitively so a
            // hand-edited row cannot silently stop resolving.
            if (tmdb is null
                && string.Equals(key, "tmdb", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                && parsed > 0)
            {
                tmdb = parsed;
            }
            else if (imdb is null && string.Equals(key, "imdb", StringComparison.OrdinalIgnoreCase))
            {
                imdb = value.Trim();
            }
        }

        return (tmdb, imdb);
    }
}
