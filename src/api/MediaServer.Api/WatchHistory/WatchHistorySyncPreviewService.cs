using System.Text.Json;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.WatchHistory;

/// <summary>What the user picked to compare. An empty scope means everything they can reach.</summary>
/// <param name="CatalogIds">Catalogs to include; empty means all.</param>
/// <param name="Kinds">Media kinds to include; empty means both.</param>
public sealed record WatchHistorySyncScope(
    IReadOnlyList<Guid> CatalogIds, IReadOnlyList<WatchHistoryMediaKind> Kinds)
{
    public static WatchHistorySyncScope Everything => new([], []);
}

/// <summary>How one local item compares with the provider.</summary>
public enum WatchHistorySyncClassification
{
    /// <summary>Watched on both sides. Nothing to do.</summary>
    InSync,

    /// <summary>The provider has history this library does not. Apply would import it.</summary>
    RemoteOnly,

    /// <summary>Watched here, unknown to the provider. Apply would export it.</summary>
    LocalOnly,

    /// <summary>
    /// Currently unwatched here but carrying a historical play count — a pre-migration row, or one
    /// the user explicitly unwatched. Apply exports nothing for it: unwatch is intentional.
    /// </summary>
    LocalUnwatchedWithHistory,

    /// <summary>Cannot be described to the provider; reported rather than guessed at.</summary>
    UnidentifiedLocally,

    /// <summary>Several local rows share one provider identity, so applying to any is arbitrary.</summary>
    AmbiguousLocalIdentity,
}

/// <summary>One line of the preview, kept small enough to persist a bounded sample.</summary>
public sealed record WatchHistorySyncEntry(
    Guid MediaItemId,
    string Title,
    WatchHistorySyncClassification Classification,
    int LocalPlayCount,
    int RemotePlayCount);

/// <summary>The read-only comparison the user approves before anything is written.</summary>
public sealed record WatchHistorySyncPreview(
    Guid RunId,
    WatchHistorySyncScope Scope,
    IReadOnlyDictionary<WatchHistorySyncClassification, int> Counts,
    IReadOnlyList<WatchHistorySyncEntry> Sample,
    int RemoteItemsNotInLibrary,
    bool HasPendingOutboundWork,
    bool HasTerminalOutboundWork,
    bool AggregateCountsMayCollapse);

/// <summary>
/// Builds the read-only preview that precedes an explicit sync.
/// </summary>
/// <remarks>
/// Sync is the only inbound path and the only one that can overwrite local aggregates and resume
/// points, so nothing here writes: it reads both sides, classifies, and records what it saw. The
/// captured state revisions are what let Apply refuse to act on a row that changed in between.
/// </remarks>
public sealed class WatchHistorySyncPreviewService(
    MediaServerDbContext database,
    IWatchHistoryProviderRegistry registry,
    WatchHistoryIdentityMapper identities,
    TimeProvider time,
    ILogger<WatchHistorySyncPreviewService> logger)
{
    /// <summary>Shown to the user; the full classification lives in the counts.</summary>
    private const int SampleSize = 50;

    /// <summary>A preview describes a moment; applying a stale one would act on a library that moved on.</summary>
    internal static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(30);

    public async Task<WatchHistoryResult<WatchHistorySyncPreview>> BuildAsync(
        int appUserId, WatchHistorySyncScope scope, CancellationToken cancellationToken)
    {
        var connection = await database.WatchHistoryConnections
            .FirstOrDefaultAsync(link => link.AppUserId == appUserId, cancellationToken);

        if (connection is null)
        {
            return WatchHistoryResult<WatchHistorySyncPreview>.Failed(
                WatchHistoryFailure.AuthenticationRequired, "No provider is connected.");
        }

        var provider = registry.Find(connection.ProviderKey);
        if (provider is null)
        {
            return WatchHistoryResult<WatchHistorySyncPreview>.Failed(
                WatchHistoryFailure.Unsupported, $"No adapter is registered for '{connection.ProviderKey}'.");
        }

        if (!provider.Capabilities.FullHistoryReads)
        {
            // Without per-play history there is nothing to reconcile against; an aggregate "watched"
            // flag cannot tell an imported play from a local one.
            return WatchHistoryResult<WatchHistorySyncPreview>.Failed(
                WatchHistoryFailure.Unsupported, "This provider cannot report its history.");
        }

        var candidates = await LoadCandidatesAsync(scope, cancellationToken);
        var localHistory = await LoadLocalHistoryAsync(appUserId, candidates.Select(item => item.Id), cancellationToken);
        var localRows = await LoadRowsAsync(appUserId, candidates.Select(item => item.Id), cancellationToken);

        // Identity first: an item we cannot describe is reported, not sent.
        var mapped = new List<(MediaItem Item, WatchHistoryIdentityResult Identity)>(candidates.Count);
        foreach (var item in candidates)
        {
            mapped.Add((item, await identities.MapAsync(item, cancellationToken)));
        }

        var resolvable = mapped.Where(pair => pair.Identity.Resolved).ToList();
        var remote = await provider.GetHistoryAsync(
            appUserId, [.. resolvable.Select(pair => pair.Identity.Identity!)], cancellationToken);

        if (!remote.Succeeded)
        {
            return WatchHistoryResult<WatchHistorySyncPreview>.Failed(remote.Failure!.Value, remote.Detail, remote.RetryAfter);
        }

        var remoteByIdentity = remote.Value!
            .GroupBy(play => IdentityKey(play.Identity), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        var duplicateIdentities = resolvable
            .GroupBy(pair => IdentityKey(pair.Identity.Identity!), StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);

        var entries = new List<WatchHistorySyncEntry>(mapped.Count);
        var matchedRemoteKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (item, identity) in mapped)
        {
            var row = localRows.GetValueOrDefault(item.Id);
            var localPlays = localHistory.GetValueOrDefault(item.Id, []).Count;

            if (!identity.Resolved)
            {
                entries.Add(new WatchHistorySyncEntry(
                    item.Id, item.Title, WatchHistorySyncClassification.UnidentifiedLocally, localPlays, 0));
                continue;
            }

            var key = IdentityKey(identity.Identity!);
            matchedRemoteKeys.Add(key);
            var remotePlays = remoteByIdentity.GetValueOrDefault(key, []).Count;

            var classification = duplicateIdentities.Contains(key)
                ? WatchHistorySyncClassification.AmbiguousLocalIdentity
                : Classify(row, localPlays, remotePlays);

            entries.Add(new WatchHistorySyncEntry(item.Id, item.Title, classification, localPlays, remotePlays));
        }

        var outbound = await database.WatchHistoryOutboxEvents.AsNoTracking()
            .Where(queued => queued.AppUserId == appUserId
                && (queued.Status == WatchHistoryOutboxStatus.Pending
                    || queued.Status == WatchHistoryOutboxStatus.Leased
                    || queued.Status == WatchHistoryOutboxStatus.Terminal))
            .Select(queued => queued.Status)
            .ToListAsync(cancellationToken);

        var counts = entries
            .GroupBy(entry => entry.Classification)
            .ToDictionary(group => group.Key, group => group.Count());

        var run = new WatchHistorySyncRun
        {
            Id = Guid.NewGuid(),
            AppUserId = appUserId,
            ConnectionId = connection.Id,
            Scope = JsonSerializer.Serialize(scope),
            Status = WatchHistorySyncStatus.Previewed,
            Counts = JsonSerializer.Serialize(counts),
            Issues = JsonSerializer.Serialize(entries.Where(IsIssue).Take(SampleSize)),
            // Captured now so Apply can refuse to act on a row that changed in between; without this
            // a play recorded during a long sync would be overwritten by a snapshot read before it.
            CapturedRevisions = JsonSerializer.Serialize(
                localRows.ToDictionary(pair => pair.Key.ToString("N"), pair => pair.Value.StateRevision)),
            HasPendingOutboundWork = outbound.Any(status => status != WatchHistoryOutboxStatus.Terminal),
            CreatedAt = time.GetUtcNow(),
            ExpiresAt = time.GetUtcNow().Add(PreviewLifetime),
        };
        database.WatchHistorySyncRuns.Add(run);
        await database.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Sync preview {RunId}: {Total} local items compared against {RemotePlays} remote plays.",
            run.Id, entries.Count, remote.Value!.Count);

        return WatchHistoryResult<WatchHistorySyncPreview>.Success(new WatchHistorySyncPreview(
            run.Id,
            scope,
            counts,
            [.. entries.Where(IsIssue).Take(SampleSize)],
            // Remote history for identities that are not in the selected library at all. Apply never
            // creates library items, so these are reported and skipped.
            remoteByIdentity.Keys.Count(key => !matchedRemoteKeys.Contains(key)),
            run.HasPendingOutboundWork,
            outbound.Contains(WatchHistoryOutboxStatus.Terminal),
            // A pre-migration row with several plays but no per-play rows can only export one
            // timeless mark, so its count collapses. The user is warned before, not after.
            entries.Any(entry => entry.Classification == WatchHistorySyncClassification.LocalUnwatchedWithHistory
                || (entry.LocalPlayCount == 0 && entry.RemotePlayCount == 0))));
    }

    private static bool IsIssue(WatchHistorySyncEntry entry) =>
        entry.Classification is not WatchHistorySyncClassification.InSync;

    private static WatchHistorySyncClassification Classify(UserItemData? row, int localPlays, int remotePlays)
    {
        if (row is { Played: false, PlayCount: > 0 })
        {
            // Deliberately not exported: unwatch is a statement about current state, and re-uploading
            // would undo the user's action on the provider's side.
            return WatchHistorySyncClassification.LocalUnwatchedWithHistory;
        }

        var watchedHere = row?.Played == true || localPlays > 0;
        return (watchedHere, remotePlays > 0) switch
        {
            (true, true) => WatchHistorySyncClassification.InSync,
            (true, false) => WatchHistorySyncClassification.LocalOnly,
            (false, true) => WatchHistorySyncClassification.RemoteOnly,
            _ => WatchHistorySyncClassification.InSync,
        };
    }

    private async Task<List<MediaItem>> LoadCandidatesAsync(WatchHistorySyncScope scope, CancellationToken cancellationToken)
    {
        var kinds = scope.Kinds.Count == 0
            ? new[] { MediaKind.Movie, MediaKind.Episode }
            : [.. scope.Kinds.Select(kind => kind == WatchHistoryMediaKind.Movie ? MediaKind.Movie : MediaKind.Episode)];

        var query = database.MediaItems.AsNoTracking().Where(item => kinds.Contains(item.Kind));
        if (scope.CatalogIds.Count > 0)
        {
            query = query.Where(item => scope.CatalogIds.Contains(item.CatalogId));
        }

        return await query.ToListAsync(cancellationToken);
    }

    private async Task<Dictionary<Guid, List<PlaybackHistoryEntry>>> LoadLocalHistoryAsync(
        int appUserId, IEnumerable<Guid> itemIds, CancellationToken cancellationToken)
    {
        var ids = itemIds.ToList();
        var entries = await database.PlaybackHistoryEntries.AsNoTracking()
            .Where(entry => entry.AppUserId == appUserId && ids.Contains(entry.MediaItemId))
            .ToListAsync(cancellationToken);

        return entries.GroupBy(entry => entry.MediaItemId).ToDictionary(group => group.Key, group => group.ToList());
    }

    private async Task<Dictionary<Guid, UserItemData>> LoadRowsAsync(
        int appUserId, IEnumerable<Guid> itemIds, CancellationToken cancellationToken)
    {
        var ids = itemIds.ToList();
        return await database.UserItemData.AsNoTracking()
            .Where(row => row.AppUserId == appUserId && ids.Contains(row.MediaItemId))
            .ToDictionaryAsync(row => row.MediaItemId, cancellationToken);
    }

    /// <summary>
    /// Stable key for the identity a provider addresses — the same one for two local editions of a
    /// film, which is exactly how duplicates are detected.
    /// </summary>
    private static string IdentityKey(WatchHistoryIdentity identity) => string.Join(
        ':',
        identity.Kind,
        identity.TmdbId?.ToString() ?? identity.ImdbId ?? "?",
        identity.SeasonNumber?.ToString() ?? "-",
        identity.EpisodeNumber?.ToString() ?? "-");
}
