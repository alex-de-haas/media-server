using MediaServer.Api.Data;
using MediaServer.Api.Library;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.WatchHistory;

/// <summary>A recorded viewing; a null timestamp explicitly means the date is unknown.</summary>
public sealed record MovieWatchEntryDto(Guid Id, DateTimeOffset? WatchedAt);

/// <summary>A bounded dated page and the movie's undated marks, with entry-based totals.</summary>
public sealed record MovieWatchHistoryDto(
    IReadOnlyList<MovieWatchEntryDto> Entries, IReadOnlyList<MovieWatchEntryDto> Undated,
    int Total, int DatedTotal, int Offset, int Limit);

/// <summary>Reads a caller's movie diary without loading calendar data for unrelated titles.</summary>
public sealed class MovieWatchHistoryService(MediaServerDbContext database)
{
    /// <summary>Returns null for an unknown or inaccessible movie. Page bounds are validated by the route.</summary>
    public async Task<MovieWatchHistoryDto?> LoadAsync(
        int appUserId, Guid id, int offset, int limit, CancellationToken cancellationToken)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset must be nonnegative.");
        if (limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 100.");
        if (!await MovieDetailAccess.Query(database, appUserId)
                .AnyAsync(item => item.Id == id && item.Kind == MediaKind.Movie, cancellationToken))
            return null;

        var query = database.PlaybackHistoryEntries.AsNoTracking()
            .Where(entry => entry.AppUserId == appUserId && entry.MediaItemId == id);
        var undated = await query.Where(entry => entry.WatchedAt == null).OrderBy(entry => entry.Id)
            .Select(entry => new MovieWatchEntryDto(entry.Id, null)).ToListAsync(cancellationToken);
        var dated = query.Where(entry => entry.WatchedAt != null);
        var total = await dated.CountAsync(cancellationToken);
        var entries = await dated.OrderByDescending(entry => entry.WatchedAt).ThenBy(entry => entry.Id)
            .Skip(offset).Take(limit)
            .Select(entry => new MovieWatchEntryDto(entry.Id, entry.WatchedAt)).ToListAsync(cancellationToken);
        return new(entries, undated, total + undated.Count, total, offset, limit);
    }
}
