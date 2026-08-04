using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Native;

/// <summary>
/// Keeps the change log bounded. A client offline longer than the retention window is told to
/// re-snapshot rather than being silently handed an incomplete feed — see
/// <see cref="NativeSyncService"/>.
/// </summary>
public sealed class ChangeLogPruner(IServiceProvider services, ILogger<ChangeLogPruner> logger)
    : BackgroundService
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
                var removed = await PruneAsync(database, DateTimeOffset.UtcNow, stoppingToken);
                if (removed > 0)
                {
                    logger.LogInformation("Pruned {Count} change-log entries older than {Days} days.",
                        removed, Retention.TotalDays);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Pruning is hygiene: a failed pass must not take the host down, and the next one
                // simply removes more.
                logger.LogWarning(exception, "Change-log pruning failed; retrying at the next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// Deletes entries older than the retention window, <b>except the newest row</b>, which is always
    /// kept. That one row is what lets sync tell "nothing has ever changed" apart from "everything you
    /// missed has been pruned" — without it, an emptied log would silently look like the former to a
    /// client that is actually in the latter situation.
    /// </summary>
    internal static async Task<int> PruneAsync(
        MediaServerDbContext database, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var newest = await database.ChangeLog.AsNoTracking()
            .MaxAsync(entry => (long?)entry.Sequence, cancellationToken);
        if (newest is not { } keep)
        {
            return 0;
        }

        var cutoff = now - Retention;
        return await database.ChangeLog
            .Where(entry => entry.OccurredAt < cutoff && entry.Sequence != keep)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
