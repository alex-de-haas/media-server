using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.WatchHistory;

/// <summary>Outcome of one cleanup pass (for logging and tests).</summary>
public sealed record AuthorizationCleanupResult(int AttemptsRemoved, int OrphanedSecretsRemoved);

/// <summary>
/// Removes authorization attempts that will never complete, and the credentials they left behind.
/// </summary>
/// <remarks>
/// Two leaks make this necessary, and neither is exotic. A user who opens the connect dialog and
/// closes the tab never polls again, so nothing else would ever remove that attempt or its device
/// code. And because an attempt's secret and its row are deleted in two steps, a crash between them
/// strands a secret with nothing pointing at it — which is why the store exposes key *names*: the
/// derivable key format lets a sweep match them against live rows without ever reading a value.
/// </remarks>
public sealed class WatchHistoryAuthorizationCleanupService(
    MediaServerDbContext database,
    IWatchHistoryCredentialStore credentials,
    TimeProvider time,
    ILogger<WatchHistoryAuthorizationCleanupService> logger)
{
    /// <summary>Matches any authorization secret, for any provider: <c>{provider}.authorization.{id}.device</c>.</summary>
    private const string AuthorizationSegment = ".authorization.";

    public async Task<AuthorizationCleanupResult> CleanupAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();

        var expired = await database.WatchHistoryAuthorizations
            .Where(attempt => attempt.ExpiresAt <= now)
            .ToListAsync(cancellationToken);

        foreach (var attempt in expired)
        {
            // Secret first, row second — the same order as everywhere else, so an interruption leaves
            // a collectable orphan rather than a row referencing a secret that is already gone.
            await DeleteQuietlyAsync(
                WatchHistoryProviderAuthorization.SecretKeyFor(attempt.ProviderKey, attempt.Id), cancellationToken);
        }

        if (expired.Count > 0)
        {
            database.WatchHistoryAuthorizations.RemoveRange(expired);
            await database.SaveChangesAsync(cancellationToken);
        }

        var orphans = await RemoveOrphanedSecretsAsync(cancellationToken);
        if (expired.Count > 0 || orphans > 0)
        {
            logger.LogInformation(
                "Cleaned up {Attempts} expired authorization attempt(s) and {Orphans} orphaned secret(s).",
                expired.Count,
                orphans);
        }

        return new AuthorizationCleanupResult(expired.Count, orphans);
    }

    private async Task<int> RemoveOrphanedSecretsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> keys;
        try
        {
            keys = await credentials.ListKeysAsync(cancellationToken);
        }
        catch (WatchHistoryCredentialStoreException exception)
        {
            // Never delete on uncertainty: if the store cannot be listed, we cannot tell an orphan
            // from a live credential, and guessing wrong logs a user out of their provider.
            logger.LogDebug(exception, "Skipping orphaned-secret cleanup; the credential store is unavailable.");
            return 0;
        }

        var authorizationKeys = keys.Where(key => key.Contains(AuthorizationSegment, StringComparison.Ordinal)).ToList();
        if (authorizationKeys.Count == 0)
        {
            return 0;
        }

        var live = await database.WatchHistoryAuthorizations
            .Select(attempt => new { attempt.ProviderKey, attempt.Id })
            .ToListAsync(cancellationToken);
        var liveKeys = live
            .Select(attempt => WatchHistoryProviderAuthorization.SecretKeyFor(attempt.ProviderKey, attempt.Id))
            .ToHashSet(StringComparer.Ordinal);

        var removed = 0;
        foreach (var key in authorizationKeys.Where(key => !liveKeys.Contains(key)))
        {
            await DeleteQuietlyAsync(key, cancellationToken);
            removed++;
        }

        return removed;
    }

    private async Task DeleteQuietlyAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await credentials.DeleteAsync(key, cancellationToken);
        }
        catch (WatchHistoryCredentialStoreException exception)
        {
            // The next pass will try again; a failure here must not stop the rows being removed.
            logger.LogDebug(exception, "Could not delete an authorization secret during cleanup.");
        }
    }
}

/// <summary>
/// Runs <see cref="WatchHistoryAuthorizationCleanupService"/> periodically. Attempts live for minutes,
/// so an hourly sweep is frequent enough to keep the leak bounded without polling Core's secrets store
/// for no reason.
/// </summary>
public sealed class WatchHistoryAuthorizationCleanupWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<WatchHistoryAuthorizationCleanupWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var cleanup = scope.ServiceProvider.GetRequiredService<WatchHistoryAuthorizationCleanupService>();
                await cleanup.CleanupAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Cleanup is housekeeping: a failed pass must never take the worker down with it.
                logger.LogWarning(exception, "The watch-history authorization cleanup pass failed.");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
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
