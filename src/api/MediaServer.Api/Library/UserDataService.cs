using MediaServer.Api.Data;
using MediaServer.Api.Jellyfin;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Library;

/// <summary>
/// Per-user playback state for the Jellyfin surface (M3): resume position, watched flag, play count and
/// favorites, keyed to the internal <see cref="MediaItem.Id"/>. Reads project <see cref="UserItemDataDto"/>
/// for batches of items — with season/series watched rollups computed from descendant episodes — and the
/// report/mark methods apply the watched threshold and resume-reset policy. See
/// <c>docs/features/jellyfin-compatibility.md</c> ("Playback Progress and User Data").
/// </summary>
public sealed class UserDataService(MediaServerDbContext database, TimeProvider time)
{
    /// <summary>Crossing this fraction of the runtime marks the item watched and clears its resume point.</summary>
    internal const double WatchedThreshold = 0.90;

    /// <summary>On stop, a position below this fraction is treated as "not started" — no resume point kept.</summary>
    internal const double MinResumeThreshold = 0.05;

    /// <summary>How long a playback session row is kept after its last report before cleanup.</summary>
    internal static readonly TimeSpan SessionRetention = TimeSpan.FromHours(24);

    private const int SessionKeyMaxLength = 200;

    /// <summary>
    /// Projects user data for a batch of items keyed by internal id. Leaf items (movie/episode) carry their
    /// own row; season/series folders carry a rollup (played when every child episode is played, with an
    /// unplayed count and watched percentage). Returns defaults when the user is anonymous.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, UserItemDataDto>> LoadAsync(
        int? appUserId, IReadOnlyList<MediaItem> items, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, UserItemDataDto>(items.Count);
        if (items.Count == 0)
        {
            return result;
        }

        if (appUserId is not { } userId)
        {
            foreach (var item in items)
            {
                result[item.Id] = new UserItemDataDto(Key: item.PublicId ?? item.Id.ToString("N"));
            }

            return result;
        }

        var leafIds = items
            .Where(item => item.Kind is MediaKind.Movie or MediaKind.Episode or MediaKind.Video)
            .Select(item => item.Id)
            .ToList();
        var seriesIds = items.Where(item => item.Kind == MediaKind.Series).Select(item => item.Id).ToList();
        var seasonIds = items.Where(item => item.Kind == MediaKind.Season).Select(item => item.Id).ToList();

        // Descendant episodes feed the folder rollups; query once for every folder in the batch.
        var children = seriesIds.Count > 0 || seasonIds.Count > 0
            ? await database.MediaItems.AsNoTracking()
                .Where(episode => episode.Kind == MediaKind.Episode && episode.PublicId != null &&
                    ((episode.SeriesId != null && seriesIds.Contains(episode.SeriesId.Value)) ||
                     (episode.SeasonId != null && seasonIds.Contains(episode.SeasonId.Value))))
                .Select(episode => new ChildEpisode(episode.Id, episode.SeriesId, episode.SeasonId))
                .ToListAsync(cancellationToken)
            : [];

        var dataItemIds = leafIds
            .Concat(seriesIds)
            .Concat(seasonIds)
            .Concat(children.Select(child => child.Id))
            .Distinct()
            .ToList();

        var rows = await database.UserItemData.AsNoTracking()
            .Where(data => data.AppUserId == userId && dataItemIds.Contains(data.MediaItemId))
            .ToListAsync(cancellationToken);
        var rowByItem = rows.ToDictionary(row => row.MediaItemId);

        var runtimeByItem = await RuntimeTicksAsync(leafIds, cancellationToken);

        foreach (var item in items)
        {
            result[item.Id] = item.Kind switch
            {
                MediaKind.Series => FolderDto(item, children.Where(child => child.SeriesId == item.Id), rowByItem),
                MediaKind.Season => FolderDto(item, children.Where(child => child.SeasonId == item.Id), rowByItem),
                MediaKind.Movie or MediaKind.Episode or MediaKind.Video => LeafDto(
                    item.PublicId!, rowByItem.GetValueOrDefault(item.Id), runtimeByItem.GetValueOrDefault(item.Id)),
                _ => new UserItemDataDto(Key: item.PublicId ?? item.Id.ToString("N")),
            };
        }

        return result;
    }

    /// <summary>Records a Jellyfin playback start/progress/stopped report against the resume + watched policy.</summary>
    public Task ReportPlaybackAsync(
        int appUserId, string itemPublicId, long positionTicks, bool isStopped, CancellationToken cancellationToken) =>
        ReportPlaybackAsync(appUserId, itemPublicId, positionTicks, isStopped, playSessionId: null, diagnostics: null, cancellationToken);

    /// <summary>
    /// Same, with the client's <c>PlaySessionId</c> (so one viewing counts once however many times it
    /// crosses the threshold) and an optional Phase 0 diagnostic record. The observation is
    /// write-only: it cannot change what this method does.
    /// </summary>
    public async Task ReportPlaybackAsync(
        int appUserId, string itemPublicId, long positionTicks, bool isStopped, string? playSessionId,
        PlaybackDiagnostics? diagnostics, CancellationToken cancellationToken)
    {
        var item = await FindItemAsync(itemPublicId, cancellationToken);
        if (item is null || item.Kind is not (MediaKind.Movie or MediaKind.Episode or MediaKind.Video))
        {
            return;
        }

        var runtime = await RuntimeTicksAsync(item.Id, cancellationToken);
        var row = await GetOrCreateRowAsync(appUserId, item.Id, cancellationToken);
        var playedBefore = row.Played;
        var playCountBefore = row.PlayCount;
        var positionBefore = row.PlaybackPositionTicks;
        var now = time.GetUtcNow();
        row.LastPlayedDate = now;

        var fraction = runtime > 0 ? (double)positionTicks / runtime : 0d;
        var atOrAboveThreshold = runtime > 0 && fraction >= WatchedThreshold;
        // Not simply !atOrAboveThreshold: with an unknown runtime the threshold is not computable, so
        // such a report is no evidence of watching. Conflating the two would let a session started
        // before the item was probed count a play the moment the runtime appears — the very case the
        // below-threshold requirement exists to catch.
        var observedBelow = runtime > 0 && fraction < WatchedThreshold;
        var session = await TrackSessionAsync(appUserId, item.Id, playSessionId, observedBelow, now, cancellationToken);

        if (atOrAboveThreshold)
        {
            // Count the viewing only the first time this session crosses, and only if the session ever
            // saw playback below the threshold. Without the first condition a rewind past 90% and a
            // second climb counts twice (observed: one session took an episode from 0 to 3 plays);
            // without the second, resuming straight into the final 10% counts a viewing nobody watched.
            // `Played` is still set either way — seeking to the end is a legitimate way to mark an item.
            var counts = session is null
                ? !row.Played
                : session.CompletedAt is null && session.ObservedBelowThreshold;
            MarkWatched(row, now, counts);
            if (counts && session is not null)
            {
                session.CompletedAt = now;
            }
        }
        else if (positionTicks <= 0)
        {
            // Opening from the start (common for an already-watched item): no resume point, keep Played as-is.
            row.PlaybackPositionTicks = 0;
        }
        else if (runtime > 0 && fraction < MinResumeThreshold)
        {
            // Below the minimum resume point: discard it on stop, otherwise keep the live position — but
            // never treat such a brief view as a re-watch, so an already-watched item stays watched.
            row.PlaybackPositionTicks = isStopped ? 0 : positionTicks;
        }
        else
        {
            // A genuine resume point; a fresh in-progress watch clears any prior watched flag.
            row.PlaybackPositionTicks = positionTicks;
            row.Played = false;
        }

        await database.SaveChangesAsync(cancellationToken);
        diagnostics?.ObserveState(
            runtime, playedBefore, row.Played, playCountBefore, row.PlayCount, positionBefore, row.PlaybackPositionTicks);
    }

    /// <summary>Marks an item played/unplayed (recursively for season/series folders) by public id (Jellyfin).</summary>
    public async Task<UserItemDataDto?> SetPlayedAsync(
        int appUserId, string itemPublicId, bool played, DateTimeOffset? playedAt, CancellationToken cancellationToken) =>
        await SetPlayedCoreAsync(appUserId, await FindItemAsync(itemPublicId, cancellationToken), played, playedAt, diagnostics: null, cancellationToken);

    /// <summary>Same, additionally reporting before/after state to an open Phase 0 diagnostic record.</summary>
    public async Task<UserItemDataDto?> SetPlayedAsync(
        int appUserId, string itemPublicId, bool played, DateTimeOffset? playedAt, PlaybackDiagnostics? diagnostics, CancellationToken cancellationToken) =>
        await SetPlayedCoreAsync(appUserId, await FindItemAsync(itemPublicId, cancellationToken), played, playedAt, diagnostics, cancellationToken);

    /// <summary>Same, keyed by the internal item id (UI surface).</summary>
    public async Task<UserItemDataDto?> SetPlayedAsync(
        int appUserId, Guid mediaItemId, bool played, DateTimeOffset? playedAt, CancellationToken cancellationToken) =>
        await SetPlayedCoreAsync(appUserId, await FindItemByIdAsync(mediaItemId, cancellationToken), played, playedAt, diagnostics: null, cancellationToken);

    /// <summary>Same, additionally reporting before/after state to an open Phase 0 diagnostic record.</summary>
    public async Task<UserItemDataDto?> SetPlayedAsync(
        int appUserId, Guid mediaItemId, bool played, DateTimeOffset? playedAt, PlaybackDiagnostics? diagnostics, CancellationToken cancellationToken) =>
        await SetPlayedCoreAsync(appUserId, await FindItemByIdAsync(mediaItemId, cancellationToken), played, playedAt, diagnostics, cancellationToken);

    private async Task<UserItemDataDto?> SetPlayedCoreAsync(
        int appUserId, MediaItem? item, bool played, DateTimeOffset? playedAt, PlaybackDiagnostics? diagnostics, CancellationToken cancellationToken)
    {
        if (item is null)
        {
            return null;
        }

        // A folder mark writes descendant episode rows, never the folder's own, so there is no leaf
        // before/after to compare for one — reporting its absent row would log a convincing
        // `played=false, playCount=0` that never happened. Folders report their fan-out instead.
        //
        // Gate on Enabled, not on non-null: DI always injects a recorder, so testing for null would
        // buy the before-row read, the runtime lookup and the after-row read on every played toggle
        // even with diagnostics off — which is the default, and the path the UI uses constantly.
        var isFolder = item.Kind is MediaKind.Series or MediaKind.Season;
        var observeRow = diagnostics is { Enabled: true } && !isFolder;
        var before = observeRow
            ? await database.UserItemData.AsNoTracking()
                .FirstOrDefaultAsync(data => data.AppUserId == appUserId && data.MediaItemId == item.Id, cancellationToken)
            : null;
        var runtime = observeRow ? await RuntimeTicksAsync(item.Id, cancellationToken) : 0;

        var now = playedAt ?? time.GetUtcNow();
        if (isFolder)
        {
            var episodeIds = await DescendantEpisodeIdsAsync(item, cancellationToken);
            await ApplyPlayedAsync(appUserId, episodeIds, played, now, cancellationToken);
            diagnostics?.ObserveFanOut(episodeIds.Count);
        }
        else
        {
            await ApplyPlayedAsync(appUserId, [item.Id], played, now, cancellationToken);
        }

        await database.SaveChangesAsync(cancellationToken);

        if (observeRow)
        {
            var after = await database.UserItemData.AsNoTracking()
                .FirstOrDefaultAsync(data => data.AppUserId == appUserId && data.MediaItemId == item.Id, cancellationToken);
            diagnostics!.ObserveState(
                runtime,
                before?.Played ?? false,
                after?.Played ?? false,
                before?.PlayCount ?? 0,
                after?.PlayCount ?? 0,
                before?.PlaybackPositionTicks ?? 0,
                after?.PlaybackPositionTicks ?? 0);
        }

        return await LoadOneAsync(appUserId, item, cancellationToken);
    }

    /// <summary>Sets or clears the favorite flag on any item (leaf or folder) by public id (Jellyfin).</summary>
    public async Task<UserItemDataDto?> SetFavoriteAsync(
        int appUserId, string itemPublicId, bool favorite, CancellationToken cancellationToken) =>
        await SetFavoriteCoreAsync(appUserId, await FindItemAsync(itemPublicId, cancellationToken), favorite, cancellationToken);

    /// <summary>Same, keyed by the internal item id (UI surface).</summary>
    public async Task<UserItemDataDto?> SetFavoriteAsync(
        int appUserId, Guid mediaItemId, bool favorite, CancellationToken cancellationToken) =>
        await SetFavoriteCoreAsync(appUserId, await FindItemByIdAsync(mediaItemId, cancellationToken), favorite, cancellationToken);

    private async Task<UserItemDataDto?> SetFavoriteCoreAsync(
        int appUserId, MediaItem? item, bool favorite, CancellationToken cancellationToken)
    {
        if (item is null)
        {
            return null;
        }

        var row = await GetOrCreateRowAsync(appUserId, item.Id, cancellationToken);
        row.IsFavorite = favorite;
        await database.SaveChangesAsync(cancellationToken);
        return await LoadOneAsync(appUserId, item, cancellationToken);
    }

    private async Task<UserItemDataDto> LoadOneAsync(int appUserId, MediaItem item, CancellationToken cancellationToken)
    {
        var data = await LoadAsync(appUserId, [item], cancellationToken);
        return data.GetValueOrDefault(item.Id, new UserItemDataDto(Key: item.PublicId ?? item.Id.ToString("N")));
    }

    private async Task ApplyPlayedAsync(
        int appUserId, IReadOnlyList<Guid> itemIds, bool played, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (itemIds.Count == 0)
        {
            return;
        }

        var existing = await database.UserItemData
            .Where(data => data.AppUserId == appUserId && itemIds.Contains(data.MediaItemId))
            .ToListAsync(cancellationToken);
        var existingByItem = existing.ToDictionary(row => row.MediaItemId);

        foreach (var itemId in itemIds)
        {
            if (!existingByItem.TryGetValue(itemId, out var row))
            {
                row = new UserItemData { Id = Guid.NewGuid(), AppUserId = appUserId, MediaItemId = itemId };
                database.UserItemData.Add(row);
            }

            if (played)
            {
                // An explicit mark is idempotent: marking an already-watched item again is a no-op,
                // not another viewing. Only playback reports decide counting per session.
                MarkWatched(row, now, countsAsPlay: !row.Played);
            }
            else
            {
                row.Played = false;
                row.PlaybackPositionTicks = 0;
            }
        }
    }

    /// <summary>
    /// Upserts the session row for this report and records whether playback has been seen below the
    /// watched threshold. Returns null when the client sent no <c>PlaySessionId</c>, in which case the
    /// caller keeps the historical flag-based rule — Infuse always sends one, so inventing a synthetic
    /// session for the rest would add correlation guesswork for no observed consumer.
    /// </summary>
    private async Task<PlaybackSession?> TrackSessionAsync(
        int appUserId, Guid mediaItemId, string? playSessionId, bool observedBelowThreshold, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var key = playSessionId?.Trim();
        // An overlong key falls back to the historical rule rather than being truncated: two ids
        // sharing a prefix would then share one session row and silently swallow a real play.
        if (string.IsNullOrEmpty(key) || key.Length > SessionKeyMaxLength)
        {
            return null;
        }

        var session = await database.PlaybackSessions.FirstOrDefaultAsync(
            entry => entry.AppUserId == appUserId && entry.MediaItemId == mediaItemId && entry.SessionKey == key,
            cancellationToken);

        if (session is null)
        {
            session = new PlaybackSession
            {
                Id = Guid.NewGuid(),
                AppUserId = appUserId,
                MediaItemId = mediaItemId,
                SessionKey = key,
                StartedAt = now,
                // Set before the insert, not after: the cleanup below runs straight against the
                // database, so a row persisted with a default LastReportAt would be immediately
                // eligible for its own purge.
                LastReportAt = now,
            };
            database.PlaybackSessions.Add(session);

            // Insert eagerly, like GetOrCreateRowAsync: a session's first two reports can overlap
            // (Playing and Progress from one client), and without this both would reach the caller's
            // SaveChanges and one would trip the unique index with a 500.
            try
            {
                await database.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                database.Entry(session).State = EntityState.Detached;
                session = await database.PlaybackSessions.FirstAsync(
                    entry => entry.AppUserId == appUserId && entry.MediaItemId == mediaItemId && entry.SessionKey == key,
                    cancellationToken);
            }

            await PurgeStaleSessionsAsync(now, cancellationToken);
        }

        session.LastReportAt = now;
        if (observedBelowThreshold)
        {
            session.ObservedBelowThreshold = true;
        }

        return session;
    }

    // Sessions are only interesting while their playback is live, so they are dropped on age rather
    // than on an explicit end (a client that vanishes never sends one). Runs when a session is
    // created, which bounds the table without a background job.
    private async Task PurgeStaleSessionsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cutoff = now - SessionRetention;
        await database.PlaybackSessions
            .Where(entry => entry.LastReportAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }

    // `countsAsPlay` is decided by the caller and deliberately has no default: within a playback
    // session the flag alone cannot tell a first completion from a re-crossing after a rewind (the
    // rewind clears it), while for an explicit mark the flag is exactly the right test.
    private static void MarkWatched(UserItemData row, DateTimeOffset now, bool countsAsPlay)
    {
        if (countsAsPlay)
        {
            row.PlayCount++;
        }

        row.Played = true;
        row.PlaybackPositionTicks = 0;
        row.LastPlayedDate = now;
    }

    private async Task<UserItemData> GetOrCreateRowAsync(int appUserId, Guid mediaItemId, CancellationToken cancellationToken)
    {
        var row = await database.UserItemData
            .FirstOrDefaultAsync(data => data.AppUserId == appUserId && data.MediaItemId == mediaItemId, cancellationToken);
        if (row is not null)
        {
            return row;
        }

        // Insert eagerly so a concurrent report for the same (user, item) can't both reach the caller's
        // SaveChanges and trip the unique index. If we lose that race, fall back to the persisted row.
        row = new UserItemData { Id = Guid.NewGuid(), AppUserId = appUserId, MediaItemId = mediaItemId };
        database.UserItemData.Add(row);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            database.Entry(row).State = EntityState.Detached;
            row = await database.UserItemData
                .FirstAsync(data => data.AppUserId == appUserId && data.MediaItemId == mediaItemId, cancellationToken);
        }

        return row;
    }

    private async Task<List<Guid>> DescendantEpisodeIdsAsync(MediaItem folder, CancellationToken cancellationToken)
    {
        var query = database.MediaItems.AsNoTracking()
            .Where(episode => episode.Kind == MediaKind.Episode && episode.PublicId != null);
        query = folder.Kind == MediaKind.Series
            ? query.Where(episode => episode.SeriesId == folder.Id)
            : query.Where(episode => episode.SeasonId == folder.Id);
        return await query.Select(episode => episode.Id).ToListAsync(cancellationToken);
    }

    private async Task<MediaItem?> FindItemAsync(string publicId, CancellationToken cancellationToken) =>
        string.IsNullOrEmpty(publicId)
            ? null
            : await database.MediaItems.AsNoTracking().FirstOrDefaultAsync(item => item.PublicId == publicId, cancellationToken);

    private async Task<MediaItem?> FindItemByIdAsync(Guid id, CancellationToken cancellationToken) =>
        await database.MediaItems.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id && item.PublicId != null, cancellationToken);

    private async Task<Dictionary<Guid, long>> RuntimeTicksAsync(IReadOnlyList<Guid> itemIds, CancellationToken cancellationToken)
    {
        if (itemIds.Count == 0)
        {
            return [];
        }

        var durations = await database.MediaSources.AsNoTracking()
            .Where(source => itemIds.Contains(source.MediaItemId))
            .GroupBy(source => source.MediaItemId)
            .Select(group => new { Id = group.Key, Ticks = group.Max(source => source.DurationTicks) })
            .ToListAsync(cancellationToken);
        return durations.ToDictionary(entry => entry.Id, entry => entry.Ticks);
    }

    private async Task<long> RuntimeTicksAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var ticks = await database.MediaSources.AsNoTracking()
            .Where(source => source.MediaItemId == itemId)
            .Select(source => (long?)source.DurationTicks)
            .MaxAsync(cancellationToken) ?? 0;
        if (ticks > 0)
        {
            return ticks;
        }

        // Fall back to provider runtime when the file has not been probed for duration.
        var fromMeta = await database.MetadataRecords.AsNoTracking()
            .Where(record => record.MediaItemId == itemId && record.RuntimeTicks != null)
            .Select(record => record.RuntimeTicks)
            .FirstOrDefaultAsync(cancellationToken);
        return fromMeta ?? 0;
    }

    private static UserItemDataDto LeafDto(string key, UserItemData? row, long runtimeTicks)
    {
        if (row is null)
        {
            return new UserItemDataDto(Key: key);
        }

        double? percentage = !row.Played && row.PlaybackPositionTicks > 0 && runtimeTicks > 0
            ? Math.Clamp(100d * row.PlaybackPositionTicks / runtimeTicks, 0, 100)
            : null;

        return new UserItemDataDto(
            Key: key,
            PlaybackPositionTicks: row.PlaybackPositionTicks,
            PlayCount: row.PlayCount,
            IsFavorite: row.IsFavorite,
            Played: row.Played,
            PlayedPercentage: percentage,
            LastPlayedDate: row.LastPlayedDate);
    }

    private static UserItemDataDto FolderDto(
        MediaItem folder, IEnumerable<ChildEpisode> children, IReadOnlyDictionary<Guid, UserItemData> rowByItem)
    {
        var childIds = children.Select(child => child.Id).ToList();
        var total = childIds.Count;
        var playedCount = childIds.Count(id => rowByItem.TryGetValue(id, out var data) && data.Played);
        var unplayed = total - playedCount;

        // Max() over DateTimeOffset? ignores nulls and yields null for an empty/all-null sequence.
        DateTimeOffset? lastPlayed = childIds
            .Select(id => rowByItem.TryGetValue(id, out var data) ? data.LastPlayedDate : null)
            .Max();

        var own = rowByItem.GetValueOrDefault(folder.Id);
        return new UserItemDataDto(
            Key: folder.PublicId ?? folder.Id.ToString("N"),
            IsFavorite: own?.IsFavorite ?? false,
            Played: total > 0 && unplayed == 0,
            PlayedPercentage: total > 0 ? 100d * playedCount / total : null,
            LastPlayedDate: lastPlayed,
            UnplayedItemCount: total > 0 ? unplayed : null);
    }

    private readonly record struct ChildEpisode(Guid Id, Guid? SeriesId, Guid? SeasonId);
}
