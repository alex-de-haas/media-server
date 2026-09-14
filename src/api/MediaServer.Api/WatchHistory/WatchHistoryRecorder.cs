using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.WatchHistory;

/// <summary>Stages local per-play history in the caller's transaction alongside its aggregates.</summary>
public sealed class WatchHistoryRecorder(MediaServerDbContext database, TimeProvider time)
{
    /// <summary>Records one completed play per client session, returning the staged entry.</summary>
    public async Task<PlaybackHistoryEntry?> StageCompletionAsync(
        int appUserId, MediaItem item, UserItemData row, string? playSessionId, DateTimeOffset watchedAt,
        CancellationToken cancellationToken)
    {
        if (playSessionId is not null && await database.PlaybackHistoryEntries.AnyAsync(
                entry => entry.AppUserId == appUserId
                    && entry.MediaItemId == item.Id
                    && entry.PlaySessionId == playSessionId,
                cancellationToken))
        {
            return null;
        }

        return StageEntry(appUserId, item.Id, watchedAt, PlaybackHistoryOrigin.LocalPlayback, playSessionId);
    }

    /// <summary>Records a dated viewing stated by the user; each call represents a separate play.</summary>
    public Task<PlaybackHistoryEntry> StageLoggedWatchAsync(
        int appUserId, MediaItem item, UserItemData row, DateTimeOffset watchedAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(StageEntry(appUserId, item.Id, watchedAt, PlaybackHistoryOrigin.Manual));
    }

    /// <summary>Records one timeless mark only when this user has no history for the item.</summary>
    public async Task StageManualWatchedAsync(
        int appUserId, MediaItem item, UserItemData row, CancellationToken cancellationToken)
    {
        if (!await database.PlaybackHistoryEntries.AnyAsync(
                entry => entry.AppUserId == appUserId && entry.MediaItemId == item.Id, cancellationToken))
        {
            StageEntry(appUserId, item.Id, null, PlaybackHistoryOrigin.Manual);
        }
    }

    /// <summary>Removes only local timeless marks; dated plays and imported history survive an unwatch.</summary>
    public async Task StageUnwatchedAsync(
        int appUserId, MediaItem item, UserItemData row, CancellationToken cancellationToken)
    {
        var marks = await database.PlaybackHistoryEntries
            .Where(entry => entry.AppUserId == appUserId
                && entry.MediaItemId == item.Id
                && entry.WatchedAt == null
                && (entry.Origin == PlaybackHistoryOrigin.Manual || entry.Origin == PlaybackHistoryOrigin.Legacy))
            .ToListAsync(cancellationToken);
        database.PlaybackHistoryEntries.RemoveRange(marks);
    }

    /// <summary>Deletes one play, reopens its session, and reprojects the remaining local history.</summary>
    public async Task StageEntryDeletionAsync(
        int appUserId, MediaItem item, UserItemData? row, PlaybackHistoryEntry entry, CancellationToken cancellationToken)
    {
        database.PlaybackHistoryEntries.Remove(entry);

        // The session gate outlives the entry — sessions are kept for 24 hours — and it decides
        // whether a crossing counts by asking whether this session already completed. Left pointing at
        // a play that no longer exists, it would answer "already counted" for the rest of the day: the
        // same client session crossing the threshold again would mark the item played, count nothing,
        // and record no entry at all. Deleting the play has to reopen the session that produced it.
        var completions = await database.PlaybackSessions
            .Where(session => session.AppUserId == appUserId && session.HistoryEntryId == entry.Id)
            .ToListAsync(cancellationToken);

        foreach (var session in completions)
        {
            session.CompletedAt = null;
            session.HistoryEntryId = null;
            // ObservedBelowThreshold is left alone: that the session once played below the threshold
            // is an observation about the session, and deleting a play does not unmake it.
        }

        var remaining = await database.PlaybackHistoryEntries
            .Where(other => other.AppUserId == appUserId
                && other.MediaItemId == item.Id
                && other.Id != entry.Id)
            .Select(other => other.WatchedAt)
            .ToListAsync(cancellationToken);

        if (row is not null)
        {
            Reproject(row, remaining);
        }

    }

    /// <summary>Recomputes one item's aggregates after a play was deleted from it.</summary>
    /// <remarks>
    /// Two invariants, because the count is not a strict projection of the entry table — a mark,
    /// unwatch and re-mark legitimately leaves one entry and a count of two, and a remap merges
    /// history onto a row without recomputing it:
    /// <list type="number">
    /// <item>a deletion never <b>increases</b> the count, however far the two have drifted;</item>
    /// <item>deleting the last entry leaves a clean slate rather than a count with nothing behind it.</item>
    /// </list>
    /// Between those, one deleted play is one fewer play, floored at what is actually left.
    /// </remarks>
    private void Reproject(UserItemData row, IReadOnlyList<DateTimeOffset?> remaining)
    {
        row.PlayCount = remaining.Count == 0
            ? 0
            : Math.Min(row.PlayCount, Math.Max(remaining.Count, row.PlayCount - 1));
        row.LastWatchedAt = remaining.Count == 0 ? null : remaining.Max();

        // Cleared only when nothing is left to say it was watched. Never set: deleting a play cannot
        // make something watched, and an item deliberately unwatched must not flip back because one of
        // its surviving plays was tidied away.
        //
        // Record when the watched flag changes, as with any other explicit state transition.
        if (remaining.Count == 0 && row.Played)
        {
            row.Played = false;
            row.WatchedStateChangedAt = time.GetUtcNow();
        }

        // PlaybackPositionTicks, LastPlayedDate and IsFavorite are untouched: a resume point is still
        // genuinely useful, and neither ordering nor favorites is a claim about this play.
    }

    private PlaybackHistoryEntry StageEntry(
        int appUserId, Guid itemId, DateTimeOffset? watchedAt, PlaybackHistoryOrigin origin, string? sessionId = null)
    {
        var entry = new PlaybackHistoryEntry
        {
            Id = Guid.NewGuid(),
            AppUserId = appUserId,
            MediaItemId = itemId,
            CreatedAt = time.GetUtcNow(),
            WatchedAt = watchedAt,
            Origin = origin,
            PlaySessionId = sessionId,
        };
        database.PlaybackHistoryEntries.Add(entry);
        return entry;
    }
}
