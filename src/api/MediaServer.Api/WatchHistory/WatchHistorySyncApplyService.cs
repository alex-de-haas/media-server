using System.Globalization;
using System.Text.Json;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.WatchHistory;

/// <summary>Why one item was left alone during apply.</summary>
public enum WatchHistorySyncSkip
{
    /// <summary>The row changed after the preview captured it; its own outbound work runs instead.</summary>
    LocalStateChangedDuringSync,

    /// <summary>Several local rows share one provider identity, so acting on any is arbitrary.</summary>
    AmbiguousLocalIdentity,

    /// <summary>Cannot be described to the provider.</summary>
    UnidentifiedLocally,

    /// <summary>Its export failed, so the remote snapshot cannot be treated as authoritative for it.</summary>
    ExportFailed,
}

/// <summary>What one apply run did.</summary>
public sealed record WatchHistorySyncApplyResult(
    int Imported, int Exported, int Unchanged, IReadOnlyDictionary<WatchHistorySyncSkip, int> Skipped);

/// <summary>
/// Applies a previewed sync: exports what the provider is missing, then makes the provider's history
/// authoritative for the items it could safely act on.
/// </summary>
/// <remarks>
/// Export first, re-read, then project. The order matters: projecting from a snapshot taken before
/// the export would erase the very plays that were just sent. Anything the run could not act on
/// safely keeps all of its local state — watched flag, resume point, history and count — because a
/// half-applied item is worse than an unapplied one.
/// </remarks>
public sealed class WatchHistorySyncApplyService(
    MediaServerDbContext database,
    IWatchHistoryProviderRegistry registry,
    WatchHistoryIdentityMapper identities,
    TimeProvider time,
    ILogger<WatchHistorySyncApplyService> logger)
{
    public async Task<WatchHistoryResult<WatchHistorySyncApplyResult>> ApplyAsync(
        int appUserId, Guid runId, CancellationToken cancellationToken)
    {
        var run = await database.WatchHistorySyncRuns
            .FirstOrDefaultAsync(entry => entry.Id == runId && entry.AppUserId == appUserId, cancellationToken);

        if (run is null)
        {
            return Failed(WatchHistoryFailure.IdentityRejected, "No such sync run.");
        }

        if (run.Status != WatchHistorySyncStatus.Previewed)
        {
            return Failed(WatchHistoryFailure.IdentityRejected, $"This run is {run.Status}, not awaiting apply.");
        }

        if (time.GetUtcNow() >= run.ExpiresAt)
        {
            // The preview described a moment that has passed; applying it would act on a library the
            // user never saw.
            run.Status = WatchHistorySyncStatus.Abandoned;
            await database.SaveChangesAsync(cancellationToken);
            return Failed(WatchHistoryFailure.ContractViolation, "This preview has expired; take a new one.");
        }

        if (await HasUndeliverableWorkAsync(appUserId, cancellationToken))
        {
            // Applying over work that cannot be delivered would read a remote snapshot that will never
            // reflect those local changes, and then overwrite them with it.
            return Failed(
                WatchHistoryFailure.ContractViolation,
                "Outbound work is stuck; resolve it before syncing so local changes are not overwritten.");
        }

        var connection = await database.WatchHistoryConnections
            .FirstOrDefaultAsync(link => link.Id == run.ConnectionId, cancellationToken);
        var provider = connection is null ? null : registry.Find(connection.ProviderKey);
        if (provider is null)
        {
            return Failed(WatchHistoryFailure.Unsupported, "The provider for this run is unavailable.");
        }

        run.Status = WatchHistorySyncStatus.Applying;
        run.StartedAt = time.GetUtcNow();
        await database.SaveChangesAsync(cancellationToken);

        try
        {
            var result = await RunAsync(appUserId, run, provider, cancellationToken);
            run.Status = WatchHistorySyncStatus.Completed;
            run.CompletedAt = time.GetUtcNow();
            connection!.LastSyncAt = run.CompletedAt;
            await database.SaveChangesAsync(cancellationToken);
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            run.Status = WatchHistorySyncStatus.Failed;
            run.CompletedAt = time.GetUtcNow();
            run.LastError = exception.Message;
            await database.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    private async Task<WatchHistoryResult<WatchHistorySyncApplyResult>> RunAsync(
        int appUserId, WatchHistorySyncRun run, IWatchHistoryProvider provider, CancellationToken cancellationToken)
    {
        var scope = JsonSerializer.Deserialize<WatchHistorySyncScope>(run.Scope ?? "null") ?? WatchHistorySyncScope.Everything;
        var captured = JsonSerializer.Deserialize<Dictionary<string, int>>(run.CapturedRevisions ?? "{}") ?? [];

        var candidates = await LoadCandidatesAsync(scope, cancellationToken);
        var skipped = new Dictionary<WatchHistorySyncSkip, int>();
        var actionable = new List<(MediaItem Item, WatchHistoryIdentity Identity, string Key)>();

        foreach (var item in candidates)
        {
            var identity = await identities.MapAsync(item, cancellationToken);
            if (!identity.Resolved)
            {
                Count(skipped, WatchHistorySyncSkip.UnidentifiedLocally);
                continue;
            }

            actionable.Add((item, identity.Identity!, IdentityKey(identity.Identity!)));
        }

        // Two local rows for one provider identity: acting on either is arbitrary, and acting on both
        // can clear an edition's resume point.
        var ambiguous = actionable
            .GroupBy(entry => entry.Key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Select(entry => entry.Item.Id))
            .ToHashSet();

        foreach (var _ in ambiguous)
        {
            Count(skipped, WatchHistorySyncSkip.AmbiguousLocalIdentity);
        }

        actionable = [.. actionable.Where(entry => !ambiguous.Contains(entry.Item.Id))];

        var localHistory = await LoadHistoryAsync(appUserId, actionable.Select(entry => entry.Item.Id), cancellationToken);
        var rows = await LoadRowsAsync(appUserId, actionable.Select(entry => entry.Item.Id), cancellationToken);

        var remoteBefore = await provider.GetHistoryAsync(
            appUserId, [.. actionable.Select(entry => entry.Identity)], cancellationToken);
        if (!remoteBefore.Succeeded)
        {
            return Failed(remoteBefore.Failure!.Value, remoteBefore.Detail, remoteBefore.RetryAfter);
        }

        var (exported, exportFailures) = await ExportMissingAsync(
            appUserId, provider, actionable, localHistory, rows, Group(remoteBefore.Value!), skipped, cancellationToken);

        // Re-read after exporting: projecting from the earlier snapshot would erase the plays just sent.
        var remoteAfter = exported > 0
            ? await provider.GetHistoryAsync(appUserId, [.. actionable.Select(entry => entry.Identity)], cancellationToken)
            : remoteBefore;
        if (!remoteAfter.Succeeded)
        {
            return Failed(remoteAfter.Failure!.Value, remoteAfter.Detail, remoteAfter.RetryAfter);
        }

        var imported = 0;
        var unchanged = 0;
        var remoteByKey = Group(remoteAfter.Value!);

        foreach (var (item, identity, key) in actionable)
        {
            // An item whose export failed keeps everything: the remote snapshot cannot be
            // authoritative for a play that never made it across, and projecting from it would erase
            // exactly the history the export was meant to preserve.
            if (exportFailures.Contains(item.Id))
            {
                continue;
            }

            var row = rows.GetValueOrDefault(item.Id);

            // The row may have moved since the preview — a play recorded while this job was running.
            // Its own outbound work will carry it; overwriting it here would lose a real viewing.
            if (row is not null
                && captured.TryGetValue(item.Id.ToString("N"), out var capturedRevision)
                && row.StateRevision != capturedRevision)
            {
                Count(skipped, WatchHistorySyncSkip.LocalStateChangedDuringSync);
                continue;
            }

            if (Project(appUserId, item, identity, row, localHistory.GetValueOrDefault(item.Id) ?? [],
                    remoteByKey.GetValueOrDefault(key) ?? []))
            {
                imported++;
            }
            else
            {
                unchanged++;
            }
        }

        await database.SaveChangesAsync(cancellationToken);
        run.Counts = JsonSerializer.Serialize(new { imported, exported, unchanged, skipped });

        logger.LogInformation(
            "Sync {RunId} applied: {Imported} imported, {Exported} exported, {Unchanged} unchanged, {Skipped} skipped.",
            run.Id, imported, exported, unchanged, skipped.Values.Sum());

        return WatchHistoryResult<WatchHistorySyncApplyResult>.Success(
            new WatchHistorySyncApplyResult(imported, exported, unchanged, skipped));
    }

    /// <summary>Sends the plays the provider is missing, before its history is treated as authoritative.</summary>
    private async Task<(int Exported, HashSet<Guid> Failed)> ExportMissingAsync(
        int appUserId,
        IWatchHistoryProvider provider,
        IReadOnlyList<(MediaItem Item, WatchHistoryIdentity Identity, string Key)> actionable,
        IReadOnlyDictionary<Guid, List<PlaybackHistoryEntry>> localHistory,
        IReadOnlyDictionary<Guid, UserItemData> rows,
        IReadOnlyDictionary<string, List<WatchHistoryPlay>> remote,
        Dictionary<WatchHistorySyncSkip, int> skipped,
        CancellationToken cancellationToken)
    {
        var toSend = new List<(Guid ItemId, WatchHistoryPlay Play)>();

        foreach (var (item, identity, key) in actionable)
        {
            var row = rows.GetValueOrDefault(item.Id);

            // Unwatch is intentional current state. Re-uploading a retained count would undo it on the
            // provider's side, which is the opposite of what the user asked for.
            if (row is { Played: false })
            {
                continue;
            }

            var local = localHistory.GetValueOrDefault(item.Id) ?? [];
            var remotePlays = remote.GetValueOrDefault(key) ?? [];

            foreach (var play in local.Where(entry => entry.WatchedAt is not null))
            {
                // Matched by exact instant: re-posting one the provider already has would be a second
                // viewing, since providers do not deduplicate.
                if (!remotePlays.Any(existing => existing.WatchedAt is { } at && SameInstant(at, play.WatchedAt!.Value)))
                {
                    toSend.Add((item.Id, new WatchHistoryPlay(identity, play.WatchedAt)));
                }
            }

            // At most one timeless mark, and only when the provider holds nothing at all — a local
            // count of five cannot become five "unknown" entries, because their times are unknowable.
            var hasTimelessLocally = local.Any(entry => entry.WatchedAt is null) || (row?.Played == true && local.Count == 0);
            if (hasTimelessLocally && remotePlays.Count == 0 && !toSend.Any(pending => pending.ItemId == item.Id))
            {
                toSend.Add((item.Id, new WatchHistoryPlay(identity, null)));
            }
        }

        if (toSend.Count == 0)
        {
            return (0, []);
        }

        var added = await provider.AddPlaysAsync(appUserId, [.. toSend.Select(entry => entry.Play)], cancellationToken);
        if (added.Succeeded)
        {
            return (toSend.Count, []);
        }

        // The export failed, so the remote snapshot is not authoritative for these items: projecting
        // from it would erase the local plays that never made it across.
        var failed = toSend.Select(entry => entry.ItemId).ToHashSet();
        foreach (var _ in failed)
        {
            Count(skipped, WatchHistorySyncSkip.ExportFailed);
        }

        logger.LogWarning("Sync export failed ({Detail}); those items keep their local state.", added.Detail);
        return (0, failed);
    }

    /// <summary>
    /// Replaces one item's local history from the provider's snapshot and recomputes its aggregates.
    /// </summary>
    /// <returns>True when anything changed.</returns>
    private bool Project(
        int appUserId,
        MediaItem item,
        WatchHistoryIdentity identity,
        UserItemData? row,
        List<PlaybackHistoryEntry> local,
        List<WatchHistoryPlay> remote)
    {
        // A pre-migration row the provider knows nothing about keeps its count: recomputing it to zero
        // would silently discard the only record that those viewings happened.
        if (remote.Count == 0 && local.Count == 0 && row is { PlayCount: > 0 })
        {
            return false;
        }

        var snapshot = JsonSerializer.Serialize(identity);
        var rebuilt = new List<PlaybackHistoryEntry>();
        var now = time.GetUtcNow();

        foreach (var play in remote.Where(play => play.WatchedAt is not null))
        {
            rebuilt.Add(new PlaybackHistoryEntry
            {
                Id = Guid.NewGuid(),
                AppUserId = appUserId,
                MediaItemId = item.Id,
                CreatedAt = now,
                WatchedAt = play.WatchedAt,
                Origin = PlaybackHistoryOrigin.ProviderSync,
                IdentitySnapshot = snapshot,
                ProviderKey = null,
                ProviderHistoryId = play.RemoteId,
                // Imported, not created here: this app may not delete it remotely.
                ProviderEntryOwned = false,
                LinkStatus = play.RemoteId is null ? PlaybackHistoryLinkStatus.None : PlaybackHistoryLinkStatus.Resolved,
            });
        }

        // Any number of remote "unknown" entries collapse to one locally: their individual times are
        // unknowable, so more than one carries no information.
        if (remote.Any(play => play.WatchedAt is null))
        {
            rebuilt.Add(new PlaybackHistoryEntry
            {
                Id = Guid.NewGuid(),
                AppUserId = appUserId,
                MediaItemId = item.Id,
                CreatedAt = now,
                WatchedAt = null,
                Origin = PlaybackHistoryOrigin.ProviderSync,
                IdentitySnapshot = snapshot,
                ProviderEntryOwned = false,
                LinkStatus = PlaybackHistoryLinkStatus.None,
            });
        }

        database.PlaybackHistoryEntries.RemoveRange(local);
        database.PlaybackHistoryEntries.AddRange(rebuilt);

        row ??= AddRow(appUserId, item.Id);
        var watched = rebuilt.Count > 0;
        if (row.Played != watched)
        {
            row.WatchedStateChangedAt = now;
        }

        row.Played = watched;
        row.PlayCount = rebuilt.Count;
        row.LastWatchedAt = rebuilt.Max(entry => entry.WatchedAt);

        // A watched item has nowhere to resume from. An item the provider does not know keeps its
        // resume point — it is still genuinely useful to the user.
        if (watched)
        {
            row.PlaybackPositionTicks = 0;
        }

        // LastPlayedDate, favorites, catalogs, metadata and source ordering are deliberately untouched:
        // imported history would otherwise reshuffle Continue Watching and Next Up.
        return true;
    }

    private UserItemData AddRow(int appUserId, Guid mediaItemId)
    {
        var row = new UserItemData { Id = Guid.NewGuid(), AppUserId = appUserId, MediaItemId = mediaItemId };
        database.UserItemData.Add(row);
        return row;
    }

    private async Task<bool> HasUndeliverableWorkAsync(int appUserId, CancellationToken cancellationToken) =>
        await database.WatchHistoryOutboxEvents.AsNoTracking().AnyAsync(
            queued => queued.AppUserId == appUserId && queued.Status == WatchHistoryOutboxStatus.Terminal,
            cancellationToken);

    private async Task<List<MediaItem>> LoadCandidatesAsync(WatchHistorySyncScope scope, CancellationToken cancellationToken)
    {
        var kinds = scope.Kinds.Count == 0
            ? new[] { MediaKind.Movie, MediaKind.Episode }
            : [.. scope.Kinds.Select(kind => kind == WatchHistoryMediaKind.Movie ? MediaKind.Movie : MediaKind.Episode)];

        var query = database.MediaItems.Where(item => kinds.Contains(item.Kind));
        if (scope.CatalogIds.Count > 0)
        {
            query = query.Where(item => scope.CatalogIds.Contains(item.CatalogId));
        }

        return await query.ToListAsync(cancellationToken);
    }

    private const int IdChunkSize = 500;

    private async Task<Dictionary<Guid, List<PlaybackHistoryEntry>>> LoadHistoryAsync(
        int appUserId, IEnumerable<Guid> itemIds, CancellationToken cancellationToken)
    {
        var byItem = new Dictionary<Guid, List<PlaybackHistoryEntry>>();
        foreach (var chunk in itemIds.Chunk(IdChunkSize))
        {
            foreach (var entry in await database.PlaybackHistoryEntries
                .Where(entry => entry.AppUserId == appUserId && chunk.Contains(entry.MediaItemId))
                .ToListAsync(cancellationToken))
            {
                if (!byItem.TryGetValue(entry.MediaItemId, out var list))
                {
                    byItem[entry.MediaItemId] = list = [];
                }

                list.Add(entry);
            }
        }

        return byItem;
    }

    private async Task<Dictionary<Guid, UserItemData>> LoadRowsAsync(
        int appUserId, IEnumerable<Guid> itemIds, CancellationToken cancellationToken)
    {
        var rows = new Dictionary<Guid, UserItemData>();
        foreach (var chunk in itemIds.Chunk(IdChunkSize))
        {
            foreach (var row in await database.UserItemData
                .Where(row => row.AppUserId == appUserId && chunk.Contains(row.MediaItemId))
                .ToListAsync(cancellationToken))
            {
                rows[row.MediaItemId] = row;
            }
        }

        return rows;
    }

    private static Dictionary<string, List<WatchHistoryPlay>> Group(IReadOnlyList<WatchHistoryPlay> plays) =>
        plays.GroupBy(play => IdentityKey(play.Identity), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

    private static void Count(Dictionary<WatchHistorySyncSkip, int> skipped, WatchHistorySyncSkip reason) =>
        skipped[reason] = skipped.GetValueOrDefault(reason) + 1;

    private static bool SameInstant(DateTimeOffset left, DateTimeOffset right) =>
        Math.Abs((left - right).TotalSeconds) < 1;

    private static string IdentityKey(WatchHistoryIdentity identity) => string.Join(
        ':',
        identity.Kind,
        identity.TmdbId?.ToString(CultureInfo.InvariantCulture) ?? identity.ImdbId ?? "?",
        identity.SeasonNumber?.ToString(CultureInfo.InvariantCulture) ?? "-",
        identity.EpisodeNumber?.ToString(CultureInfo.InvariantCulture) ?? "-");

    private static WatchHistoryResult<WatchHistorySyncApplyResult> Failed(
        WatchHistoryFailure failure, string? detail, TimeSpan? retryAfter = null) =>
        WatchHistoryResult<WatchHistorySyncApplyResult>.Failed(failure, detail, retryAfter);
}
