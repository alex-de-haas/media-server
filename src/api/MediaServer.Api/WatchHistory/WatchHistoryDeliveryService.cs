using System.Text.Json;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.WatchHistory;

/// <summary>Outcome of one delivery pass, for logging and tests.</summary>
public sealed record WatchHistoryDeliveryResult(int Delivered, int Retried, int Failed);

/// <summary>
/// Delivers queued outbound work to the connected provider.
/// </summary>
/// <remarks>
/// The queue exists so a slow, rate-limited or unreachable provider never blocks playback or a
/// watched toggle. That means everything here runs after the fact and must be safe to run twice: a
/// crash mid-delivery has to leave the next pass able to finish the job rather than duplicate it.
/// </remarks>
public sealed class WatchHistoryDeliveryService(
    MediaServerDbContext database,
    IWatchHistoryProviderRegistry registry,
    TimeProvider time,
    ILogger<WatchHistoryDeliveryService> logger)
{
    /// <summary>How long a claimed event stays claimed before another pass may take it.</summary>
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    /// <summary>Give up after this many attempts and surface the issue instead of retrying forever.</summary>
    internal const int MaxAttempts = 8;

    /// <summary>Bounded so one pass cannot monopolise a provider's rate limit.</summary>
    private const int BatchSize = 20;

    public async Task<WatchHistoryDeliveryResult> DeliverAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var due = await database.WatchHistoryOutboxEvents
            .Where(item => (item.Status == WatchHistoryOutboxStatus.Pending
                    || (item.Status == WatchHistoryOutboxStatus.Leased && item.LeaseUntil < now))
                && (item.NextAttemptAt == null || item.NextAttemptAt <= now))
            .OrderBy(item => item.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var delivered = 0;
        var retried = 0;
        var failed = 0;

        foreach (var item in due)
        {
            // Claim first: a lease that expires lets a stalled pass be picked up again, while a live
            // one keeps two passes from sending the same thing twice.
            item.Status = WatchHistoryOutboxStatus.Leased;
            item.LeaseUntil = now.Add(LeaseDuration);
            item.Attempts++;
            await database.SaveChangesAsync(cancellationToken);

            var outcome = await DeliverOneAsync(item, cancellationToken);
            switch (outcome.Failure)
            {
                case null:
                    item.Status = WatchHistoryOutboxStatus.Completed;
                    item.CompletedAt = time.GetUtcNow();
                    item.LeaseUntil = null;
                    item.LastError = null;
                    delivered++;
                    break;

                case { } failure when IsRetryable(failure) && item.Attempts < MaxAttempts:
                    item.Status = WatchHistoryOutboxStatus.Pending;
                    item.LeaseUntil = null;
                    item.NextAttemptAt = time.GetUtcNow().Add(outcome.RetryAfter ?? BackoffFor(item.Attempts));
                    item.LastError = outcome.Detail;
                    retried++;
                    break;

                default:
                    // Terminal: retrying an identity the provider rejected only burns rate limit, and
                    // an expired credential needs the user, not another attempt.
                    item.Status = WatchHistoryOutboxStatus.Terminal;
                    item.LeaseUntil = null;
                    item.LastError = outcome.Detail;
                    failed++;
                    break;
            }

            await database.SaveChangesAsync(cancellationToken);
        }

        if (delivered > 0 || retried > 0 || failed > 0)
        {
            logger.LogInformation(
                "Watch-history delivery: {Delivered} delivered, {Retried} to retry, {Failed} terminal.",
                delivered, retried, failed);
        }

        return new WatchHistoryDeliveryResult(delivered, retried, failed);
    }

    private async Task<(WatchHistoryFailure? Failure, string? Detail, TimeSpan? RetryAfter)> DeliverOneAsync(
        WatchHistoryOutboxEvent item, CancellationToken cancellationToken)
    {
        var connection = await database.WatchHistoryConnections.AsNoTracking()
            .FirstOrDefaultAsync(link => link.Id == item.ConnectionId, cancellationToken);

        if (connection is null)
        {
            return (WatchHistoryFailure.ContractViolation, "The connection this work belonged to is gone.", null);
        }

        var provider = registry.Find(connection.ProviderKey);
        if (provider is null)
        {
            return (WatchHistoryFailure.Unsupported, $"No adapter is registered for '{connection.ProviderKey}'.", null);
        }

        var identity = Deserialize(item.IdentitySnapshot);
        if (identity is null)
        {
            // The snapshot is frozen at enqueue time precisely so this cannot depend on the library's
            // current state; an unreadable one is a bug, not something to retry.
            return (WatchHistoryFailure.ContractViolation, "The queued identity snapshot could not be read.", null);
        }

        return item.Operation switch
        {
            WatchHistoryOutboxOperation.AddExactWatch =>
                await AddExactAsync(provider, item, identity, cancellationToken),
            WatchHistoryOutboxOperation.EnsureTimelessWatched =>
                await EnsureTimelessAsync(provider, item, identity, cancellationToken),
            WatchHistoryOutboxOperation.RemoveOwnedTimelessEntries =>
                await RemoveOwnedAsync(provider, item, cancellationToken),
            _ => (WatchHistoryFailure.Unsupported, $"Unknown operation {item.Operation}.", null),
        };
    }

    private async Task<(WatchHistoryFailure?, string?, TimeSpan?)> AddExactAsync(
        IWatchHistoryProvider provider, WatchHistoryOutboxEvent item, WatchHistoryIdentity identity, CancellationToken cancellationToken)
    {
        if (!provider.Capabilities.ExactTimestampWrites)
        {
            return (WatchHistoryFailure.Unsupported, "This provider cannot record an exact time.", null);
        }

        if (item.OccurredAt is not { } occurredAt)
        {
            // Sending this as a timeless write would quietly turn a proven completion into "watched,
            // time unknown". A queued exact watch without its time is a bug upstream, not a retry.
            return (WatchHistoryFailure.ContractViolation, "An exact watch was queued without a time.", null);
        }

        // On a retry the previous attempt may have reached the provider before the process died.
        // Providers do not deduplicate history by item and timestamp, so a blind re-post becomes a
        // second viewing on the user's profile. Only retries pay for this read — a first attempt
        // cannot be a duplicate of itself.
        if (item.Attempts > 1 && provider.Capabilities.FullHistoryReads)
        {
            var existing = await provider.GetHistoryAsync(item.AppUserId, [identity], cancellationToken);
            if (!existing.Succeeded)
            {
                return (existing.Failure, existing.Detail, existing.RetryAfter);
            }

            if (existing.Value!.Any(play => play.WatchedAt is { } watched && SameInstant(watched, occurredAt)))
            {
                logger.LogDebug("An earlier attempt already recorded this exact play; not sending it again.");
                return (null, null, null);
            }
        }

        var added = await provider.AddPlaysAsync(
            item.AppUserId, [new WatchHistoryPlay(identity, occurredAt)], cancellationToken);

        return added.Succeeded
            ? (null, null, null)
            : (added.Failure, added.Detail, added.RetryAfter);
    }

    // Providers round stored timestamps, so compare at second precision rather than tick equality.
    private static bool SameInstant(DateTimeOffset left, DateTimeOffset right) =>
        Math.Abs((left - right).TotalSeconds) < 1;

    /// <summary>
    /// Adds one timeless mark, but only if the provider holds nothing for the item — and records
    /// which entry we created so it can be removed later.
    /// </summary>
    /// <remarks>
    /// Read before, write, read after. The provider's add response reports counts rather than the ids
    /// it created, so the new entry is identified by the difference between the two reads. The
    /// "before" set is persisted on the event, so a crash between the write and the second read can
    /// still resolve ownership on a later pass instead of leaving a mark this app can never prove is
    /// its own — and therefore can never safely delete.
    /// </remarks>
    private async Task<(WatchHistoryFailure?, string?, TimeSpan?)> EnsureTimelessAsync(
        IWatchHistoryProvider provider, WatchHistoryOutboxEvent item, WatchHistoryIdentity identity, CancellationToken cancellationToken)
    {
        if (!provider.Capabilities.TimelessWrites)
        {
            return (WatchHistoryFailure.Unsupported, "This provider cannot record a play without a time.", null);
        }

        if (!provider.Capabilities.FullHistoryReads)
        {
            // Without a history read there is no way to tell whether a mark is even needed, nor which
            // entry we created — and ownership is what permits removing it later.
            return (WatchHistoryFailure.Unsupported, "This provider cannot report its history.", null);
        }

        var current = await provider.GetHistoryAsync(item.AppUserId, [identity], cancellationToken);
        if (!current.Succeeded)
        {
            return (current.Failure, current.Detail, current.RetryAfter);
        }

        var before = ParseRemoteIds(item.RemoteIdSnapshot);
        if (before is null)
        {
            // First attempt.
            if (current.Value!.Count > 0)
            {
                // The provider already knows this was watched. Adding a second mark would put an extra
                // viewing on the user's profile for a toggle.
                return (null, null, null);
            }

            item.RemoteIdSnapshot = JsonSerializer.Serialize(Array.Empty<string>());
            await database.SaveChangesAsync(cancellationToken);

            var added = await provider.AddPlaysAsync(
                item.AppUserId, [new WatchHistoryPlay(identity, WatchedAt: null)], cancellationToken);
            if (!added.Succeeded)
            {
                return (added.Failure, added.Detail, added.RetryAfter);
            }

            var after = await provider.GetHistoryAsync(item.AppUserId, [identity], cancellationToken);
            if (!after.Succeeded)
            {
                return (after.Failure, after.Detail, after.RetryAfter);
            }

            return await ResolveOwnershipAsync(item, new HashSet<string>(StringComparer.Ordinal), after.Value!, cancellationToken);
        }

        // A retry. The snapshot is written *before* the add, so its presence cannot prove the add
        // landed — only the difference can. Deliberately not re-adding: if the earlier attempt did
        // land, a second mark is a duplicate viewing on the user's profile, whereas a mark that never
        // landed is recovered by the next explicit sync. The duplicate is the harm worth avoiding.
        return await ResolveOwnershipAsync(item, before, current.Value!, cancellationToken);
    }

    private async Task<(WatchHistoryFailure?, string?, TimeSpan?)> ResolveOwnershipAsync(
        WatchHistoryOutboxEvent item,
        IReadOnlySet<string> before,
        IReadOnlyList<WatchHistoryPlay> after,
        CancellationToken cancellationToken)
    {
        var created = after
            .Where(play => play.RemoteId is not null && !before.Contains(play.RemoteId))
            .Select(play => play.RemoteId!)
            .ToList();

        if (created.Count == 0 && item.Attempts < MaxAttempts)
        {
            // Nothing new yet: either the provider has not surfaced the entry, or the add never
            // landed. Read again shortly rather than deciding now; the attempt ceiling ends this and
            // leaves ownership unresolved rather than guessed.
            return (WatchHistoryFailure.Transient, "The created entry is not visible yet.", null);
        }

        await LinkOwnedEntryAsync(item, created, cancellationToken);
        return (null, null, null);
    }

    private async Task LinkOwnedEntryAsync(
        WatchHistoryOutboxEvent item, IReadOnlyList<string> created, CancellationToken cancellationToken)
    {
        if (item.HistoryEntryId is not { } entryId)
        {
            return;
        }

        var entry = await database.PlaybackHistoryEntries.FirstOrDefaultAsync(
            row => row.Id == entryId, cancellationToken);
        if (entry is null)
        {
            // The user undid the mark before delivery finished. Nothing to own it, so the remote entry
            // stays — better a stale mark than deleting one that might not be ours.
            return;
        }

        if (created.Count == 1)
        {
            entry.ProviderKey = ProviderKeyOf(item);
            entry.ProviderHistoryId = created[0];
            entry.ProviderEntryOwned = true;
            entry.LinkStatus = PlaybackHistoryLinkStatus.Resolved;
        }
        else
        {
            // Zero means the provider had not surfaced it yet; more than one means a concurrent write
            // muddied the difference. Either way the id is not knowable, and guessing would license
            // deleting an entry this app did not create.
            entry.LinkStatus = PlaybackHistoryLinkStatus.Unresolved;
            logger.LogInformation(
                "Could not identify the created remote entry uniquely ({Count} candidates); leaving it unowned.",
                created.Count);
        }
    }

    /// <summary>
    /// Removes only the entries this app created and resolved.
    /// </summary>
    /// <remarks>
    /// Entries another client wrote, and every exact play, are left alone. The provider's whole-item
    /// removal is never used — it would take those with it.
    /// </remarks>
    private async Task<(WatchHistoryFailure?, string?, TimeSpan?)> RemoveOwnedAsync(
        IWatchHistoryProvider provider, WatchHistoryOutboxEvent item, CancellationToken cancellationToken)
    {
        if (!provider.Capabilities.IndividualEntryRemoval)
        {
            // A provider that can only remove everything for an item must not be asked to, so this
            // surfaces as an issue rather than silently doing something broader.
            return (WatchHistoryFailure.Unsupported, "This provider cannot remove a single history entry.", null);
        }

        // Read from the event, not from the entries: the recorder deleted those in the same
        // transaction as the unwatch, so by the time this runs there is nothing local left to read.
        // The ids were captured onto the event before that deletion for exactly this reason.
        var owned = ParseRemoteIds(item.RemoteIdSnapshot)?.ToList() ?? [];

        if (owned.Count == 0)
        {
            // Nothing this app can prove it owns. Completing rather than failing is deliberate: there
            // is no work to do, and retrying would never find any.
            return (null, null, null);
        }

        var removed = await provider.RemoveEntriesAsync(item.AppUserId, owned, cancellationToken);
        return removed.Succeeded ? (null, null, null) : (removed.Failure, removed.Detail, removed.RetryAfter);
    }

    private string? ProviderKeyOf(WatchHistoryOutboxEvent item) =>
        database.WatchHistoryConnections.AsNoTracking()
            .Where(link => link.Id == item.ConnectionId)
            .Select(link => link.ProviderKey)
            .FirstOrDefault();

    private static bool IsRetryable(WatchHistoryFailure failure) =>
        failure is WatchHistoryFailure.Transient or WatchHistoryFailure.RateLimited;

    // Exponential with a ceiling: a provider that is down for an hour should not be retried every
    // few seconds, and one that recovers quickly should not wait an hour.
    private static TimeSpan BackoffFor(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(Math.Pow(2, Math.Min(attempts, 10)) * 5, 3600));

    private static WatchHistoryIdentity? Deserialize(string? snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WatchHistoryIdentity>(snapshot);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static HashSet<string>? ParseRemoteIds(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json) is { } ids
                ? [.. ids]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>
/// Runs <see cref="WatchHistoryDeliveryService"/> on a short interval.
/// </summary>
/// <remarks>
/// Frequent enough that a watched toggle reaches the provider while the user still has the app open,
/// slow enough not to poll a provider that has nothing queued.
/// </remarks>
public sealed class WatchHistoryDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<WatchHistoryDeliveryWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var delivery = scope.ServiceProvider.GetRequiredService<WatchHistoryDeliveryService>();
                await delivery.DeliverAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A failed pass must never take the worker down: the queue is durable and the next
                // pass picks up where this one stopped.
                logger.LogWarning(exception, "The watch-history delivery pass failed.");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
