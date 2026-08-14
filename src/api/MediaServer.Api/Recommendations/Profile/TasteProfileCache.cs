using System.Collections.Concurrent;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Recommendations.Profile;

/// <summary>
/// One profile per user, rebuilt when — and only when — something it was built from has moved.
/// </summary>
/// <remarks>
/// Stamped rather than invalidated. The plan for this feature called for invalidation on play,
/// favorite, hide, rating and watchlist mutation, plus a library generation; that is six write paths
/// to hook, and a hook nobody adds when a seventh signal arrives fails <em>silently</em> — the feed
/// keeps answering, from a profile describing a viewer who no longer exists.
/// <para>
/// A stamp inverts that. It is derived from the inputs themselves, so a new signal cannot be
/// forgotten: either it moves one of the counts and timestamps below, in which case the profile
/// rebuilds, or it does not, in which case it was not an input. The cost is a handful of aggregates
/// per feed request against indexed columns, which is far less than the profile it protects.
/// </para>
/// </remarks>
public sealed class TasteProfileCache
{
    private readonly ConcurrentDictionary<int, Entry> _profiles = new();

    public async Task<TasteProfile> GetAsync(
        int appUserId, MediaServerDbContext database, TasteProfileBuilder builder, CancellationToken cancellationToken)
    {
        var stamp = await StampOfAsync(appUserId, database, cancellationToken);
        if (_profiles.TryGetValue(appUserId, out var cached) && cached.Stamp == stamp)
        {
            return cached.Profile;
        }

        var profile = await builder.BuildAsync(appUserId, cancellationToken);

        // Last writer wins. Two concurrent builds at the same stamp produce the same profile, and one
        // at a newer stamp is the one worth keeping — either way there is nothing to reconcile.
        _profiles[appUserId] = new Entry(stamp, profile);
        return profile;
    }

    /// <summary>
    /// What this user's inputs looked like: their plays, their per-item state, their hides, their
    /// watchlist, and the shape of the library it is all damped against.
    /// </summary>
    internal static async Task<ProfileStamp> StampOfAsync(
        int appUserId, MediaServerDbContext database, CancellationToken cancellationToken)
    {
        var plays = await database.PlaybackHistoryEntries.AsNoTracking()
            .Where(entry => entry.AppUserId == appUserId)
            .CountAsync(cancellationToken);

        // StateRevision is bumped on every write to a row, so its sum moves for a rating, a favorite,
        // a resume point or a played toggle without needing a column per signal.
        var stateRevisions = await database.UserItemData.AsNoTracking()
            .Where(row => row.AppUserId == appUserId)
            .SumAsync(row => (long)row.StateRevision, cancellationToken);

        var hides = await database.RecommendationHides.AsNoTracking()
            .Where(hide => hide.AppUserId == appUserId)
            .CountAsync(cancellationToken);

        var watchlist = await database.WatchlistEntries.AsNoTracking()
            .Where(entry => entry.AppUserId == appUserId)
            .CountAsync(cancellationToken);

        var library = await LibraryFacetIndexCache.GenerationOfAsync(database, cancellationToken);

        return new ProfileStamp(plays, stateRevisions, hides, watchlist, library);
    }

    private readonly record struct Entry(ProfileStamp Stamp, TasteProfile Profile);
}

/// <summary>The inputs a cached profile was built from, compressed to something comparable.</summary>
public readonly record struct ProfileStamp(
    int Plays, long StateRevisions, int Hides, int WatchlistEntries, LibraryGeneration Library);
