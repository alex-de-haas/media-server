using MediaServer.Api.Data;
using MediaServer.Api.Jellyfin.Auth;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Hosty;

/// <summary>Outcome of one directory reconcile pass (for logging/tests).</summary>
public sealed record DirectoryReconcileResult(bool Skipped, int UsersUpserted, int CredentialsRevoked)
{
    public static readonly DirectoryReconcileResult SkippedResult = new(true, 0, 0);
}

/// <summary>
/// Reconciles the internal <see cref="AppUser"/> table against Core's scoped user directory. Core has
/// no directory-change webhooks (verified against <c>docker-host</c>), so this polls: it upserts the
/// assigned, enabled users (mapping <c>host.admin</c> → <see cref="AppUserRole.Admin"/>) and revokes
/// the app-owned Jellyfin credential + tokens of any app user who is no longer assigned or has been
/// disabled — that user has vanished from the directory listing. See <c>docs/features/security.md</c>.
/// </summary>
public sealed class DirectoryReconcileService(
    MediaServerDbContext database,
    IHostyCoreClient core,
    JellyfinCredentialService credentials,
    ILogger<DirectoryReconcileService> logger)
{
    public async Task<DirectoryReconcileResult> ReconcileAsync(CancellationToken cancellationToken)
    {
        var directory = await core.ListDirectoryUsersAsync(cancellationToken);
        if (directory is null)
        {
            // Not Core managed, or a transient Core failure — never revoke on uncertainty.
            return DirectoryReconcileResult.SkippedResult;
        }

        var now = DateTimeOffset.UtcNow;
        var assignedHostIds = directory.Select(user => user.Id).ToHashSet(StringComparer.Ordinal);
        var appUsers = await database.AppUsers.ToListAsync(cancellationToken);
        var byHostId = appUsers.ToDictionary(user => user.HostUserId, StringComparer.Ordinal);

        var upserted = 0;
        foreach (var entry in directory)
        {
            var role = string.Equals(entry.HostRole, "host.admin", StringComparison.OrdinalIgnoreCase)
                ? AppUserRole.Admin
                : AppUserRole.User;

            if (byHostId.TryGetValue(entry.Id, out var existing))
            {
                if (existing.Email != entry.Email || existing.DisplayName != entry.DisplayName || existing.Role != role)
                {
                    existing.Email = entry.Email;
                    existing.DisplayName = entry.DisplayName;
                    existing.Role = role;
                    upserted++;
                }
            }
            else
            {
                database.AppUsers.Add(new AppUser
                {
                    HostUserId = entry.Id,
                    Email = entry.Email,
                    DisplayName = entry.DisplayName,
                    Role = role,
                    CreatedAt = now,
                    LastSeenAt = now,
                });
                upserted++;
            }
        }

        if (upserted > 0)
        {
            await database.SaveChangesAsync(cancellationToken);
        }

        // Revoke Jellyfin access for app users who fell out of the directory (unassigned or disabled).
        var revoked = 0;
        foreach (var user in appUsers.Where(user => !assignedHostIds.Contains(user.HostUserId)))
        {
            if (await credentials.RevokeCredentialAsync(user.Id, cancellationToken))
            {
                revoked++;
                logger.LogInformation("Revoked Jellyfin access for app user {AppUserId} (no longer assigned/enabled).", user.Id);
            }
        }

        if (upserted > 0 || revoked > 0)
        {
            logger.LogInformation("Directory reconcile: {Upserted} user(s) upserted, {Revoked} credential(s) revoked.", upserted, revoked);
        }

        return new DirectoryReconcileResult(false, upserted, revoked);
    }
}

/// <summary>Runs <see cref="DirectoryReconcileService.ReconcileAsync"/> shortly after startup and on a timer.</summary>
public sealed class DirectoryReconcileWorker(IServiceScopeFactory scopeFactory, ILogger<DirectoryReconcileWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
            await RunOnceAsync(stoppingToken);

            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<DirectoryReconcileService>();
            await service.ReconcileAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Directory reconcile pass failed.");
        }
    }
}
