using System.Text.Json;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.WatchHistory.Trakt;

/// <summary>
/// Connects and disconnects one user's Trakt account through the device flow.
/// </summary>
/// <remarks>
/// Acts for the calling user only — there is no overload taking someone else's id, so an
/// administrator who configures the instance's Trakt application still cannot connect an account on
/// another user's behalf or read their tokens.
/// </remarks>
public sealed class TraktAuthorizationService(
    MediaServerDbContext database,
    TraktOAuthClient oauth,
    IWatchHistoryCredentialStore credentials,
    MediaServerSettings settings,
    TimeProvider time,
    ILogger<TraktAuthorizationService> logger)
    : IWatchHistoryProviderAuthorization
{
    public const string ProviderKeyValue = "trakt";

    public string ProviderKey => ProviderKeyValue;

    public bool IsConfigured => settings.IsTraktConfigured;

    /// <summary>Secrets-store key holding one connection's tokens. Derived, never stored.</summary>
    internal static string ConnectionSecretKey(Guid connectionId) => $"{ProviderKeyValue}.connection.{connectionId:N}.tokens";

    public async Task<WatchHistoryResult<WatchHistoryAuthorizationPrompt>> StartAsync(int appUserId, CancellationToken cancellationToken)
    {
        var started = await oauth.StartDeviceAuthorizationAsync(cancellationToken);
        if (!started.Succeeded)
        {
            return WatchHistoryResult<WatchHistoryAuthorizationPrompt>.Failed(started.Failure!.Value, started.Detail, started.RetryAfter);
        }

        var device = started.Value!;
        var now = time.GetUtcNow();

        // Starting again replaces any attempt in flight: the user is looking at the new code, and the
        // old device code is dead to them. Drop its secret before the row, so a crash between the two
        // leaves an orphan the reconciliation can find rather than a row pointing at nothing.
        var existing = await database.WatchHistoryAuthorizations
            .FirstOrDefaultAsync(entry => entry.AppUserId == appUserId && entry.ProviderKey == ProviderKeyValue, cancellationToken);
        if (existing is not null)
        {
            await DeleteSecretQuietlyAsync(WatchHistoryProviderAuthorization.SecretKeyFor(ProviderKeyValue, existing.Id), cancellationToken);
            database.WatchHistoryAuthorizations.Remove(existing);
            await database.SaveChangesAsync(cancellationToken);
        }

        var attempt = new WatchHistoryProviderAuthorization
        {
            Id = Guid.NewGuid(),
            AppUserId = appUserId,
            ProviderKey = ProviderKeyValue,
            UserCode = device.UserCode,
            VerificationUrl = device.VerificationUrl,
            CreatedAt = now,
            ExpiresAt = now.AddSeconds(device.ExpiresInSeconds),
            PollIntervalSeconds = device.IntervalSeconds,
            NextPollAt = now.AddSeconds(device.IntervalSeconds),
            Status = WatchHistoryAuthorizationStatus.Pending,
        };

        // Secret first, row second: a row without its device code is unusable, while a secret without
        // a row is merely garbage the reconciliation collects.
        await credentials.SetAsync(
            WatchHistoryProviderAuthorization.SecretKeyFor(ProviderKeyValue, attempt.Id), device.DeviceCode, cancellationToken);
        database.WatchHistoryAuthorizations.Add(attempt);
        await database.SaveChangesAsync(cancellationToken);

        return WatchHistoryResult<WatchHistoryAuthorizationPrompt>.Success(new WatchHistoryAuthorizationPrompt(
            device.UserCode, device.VerificationUrl, attempt.ExpiresAt, TimeSpan.FromSeconds(device.IntervalSeconds)));
    }

    public async Task<WatchHistoryResult<WatchHistoryAuthorizationOutcome>> PollAsync(int appUserId, CancellationToken cancellationToken)
    {
        var attempt = await database.WatchHistoryAuthorizations
            .FirstOrDefaultAsync(entry => entry.AppUserId == appUserId && entry.ProviderKey == ProviderKeyValue, cancellationToken);
        if (attempt is null)
        {
            return WatchHistoryResult<WatchHistoryAuthorizationOutcome>.Failed(
                WatchHistoryFailure.IdentityRejected, "There is no Trakt authorization in progress.");
        }

        var now = time.GetUtcNow();
        var secretKey = WatchHistoryProviderAuthorization.SecretKeyFor(ProviderKeyValue, attempt.Id);

        if (now >= attempt.ExpiresAt)
        {
            await FinishAttemptAsync(attempt, secretKey, cancellationToken);
            return Outcome(WatchHistoryAuthorizationState.Expired);
        }

        if (now < attempt.NextPollAt)
        {
            // Respect the interval we were given rather than asking Trakt to tell us off.
            return Outcome(WatchHistoryAuthorizationState.Pending, retryAfter: attempt.NextPollAt - now);
        }

        var deviceCode = await credentials.GetAsync(secretKey, cancellationToken);
        if (deviceCode is null)
        {
            // The secret is gone (restored database, rebuilt host); the attempt cannot be completed.
            await FinishAttemptAsync(attempt, secretKey, cancellationToken);
            return Outcome(WatchHistoryAuthorizationState.Expired);
        }

        var (state, tokens, retryAfter) = await oauth.PollDeviceTokenAsync(deviceCode, cancellationToken);

        switch (state)
        {
            case WatchHistoryAuthorizationState.Approved when tokens is not null:
                var connected = await CompleteAsync(appUserId, attempt, secretKey, tokens, cancellationToken);
                return connected;

            case WatchHistoryAuthorizationState.Denied:
            case WatchHistoryAuthorizationState.Expired:
                await FinishAttemptAsync(attempt, secretKey, cancellationToken);
                return Outcome(state);

            case WatchHistoryAuthorizationState.SlowDown:
                var backoff = retryAfter ?? TimeSpan.FromSeconds(attempt.PollIntervalSeconds);
                attempt.PollIntervalSeconds = (int)Math.Ceiling(backoff.TotalSeconds);
                attempt.NextPollAt = now.Add(backoff);
                await database.SaveChangesAsync(cancellationToken);
                return Outcome(WatchHistoryAuthorizationState.SlowDown, retryAfter: backoff);

            default:
                attempt.NextPollAt = now.AddSeconds(attempt.PollIntervalSeconds);
                await database.SaveChangesAsync(cancellationToken);
                return Outcome(WatchHistoryAuthorizationState.Pending, retryAfter: TimeSpan.FromSeconds(attempt.PollIntervalSeconds));
        }
    }

    public async Task DisconnectAsync(int appUserId, CancellationToken cancellationToken)
    {
        var connection = await database.WatchHistoryConnections
            .FirstOrDefaultAsync(entry => entry.AppUserId == appUserId && entry.ProviderKey == ProviderKeyValue, cancellationToken);

        if (connection is not null)
        {
            // Revoke first, best effort. Whether Trakt answers or not, the local credential goes: the
            // user asked to disconnect, and leaving a working token behind is the worse failure.
            var stored = await ReadCredentialsAsync(connection, cancellationToken);
            if (stored is not null)
            {
                await oauth.RevokeAsync(stored.AccessToken, cancellationToken);
            }

            await DeleteSecretQuietlyAsync(connection.SecretKey, cancellationToken);
            database.WatchHistoryConnections.Remove(connection);
        }

        var attempt = await database.WatchHistoryAuthorizations
            .FirstOrDefaultAsync(entry => entry.AppUserId == appUserId && entry.ProviderKey == ProviderKeyValue, cancellationToken);
        if (attempt is not null)
        {
            await DeleteSecretQuietlyAsync(WatchHistoryProviderAuthorization.SecretKeyFor(ProviderKeyValue, attempt.Id), cancellationToken);
            database.WatchHistoryAuthorizations.Remove(attempt);
        }

        // Local playback state is never touched: disconnecting a provider is not forgetting what the
        // user watched.
        await database.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Reads a connection's credentials, refreshing them when they are near expiry and persisting what
    /// comes back — Trakt rotates the refresh token on every exchange, so not storing it would strand
    /// the connection at the next refresh.
    /// </summary>
    internal async Task<TraktCredentials?> ReadCredentialsAsync(
        WatchHistoryProviderConnection connection, CancellationToken cancellationToken)
    {
        var stored = await credentials.GetAsync(connection.SecretKey, cancellationToken);
        if (stored is null)
        {
            await MarkRequiresReconnectAsync(connection, "The stored Trakt credentials are missing.", cancellationToken);
            return null;
        }

        TraktCredentials? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<TraktCredentials>(stored);
        }
        catch (JsonException)
        {
            parsed = null;
        }

        if (parsed is null)
        {
            await MarkRequiresReconnectAsync(connection, "The stored Trakt credentials could not be read.", cancellationToken);
            return null;
        }

        var fresh = await oauth.EnsureFreshAsync(parsed, cancellationToken);
        if (!fresh.Succeeded)
        {
            if (fresh.Failure == WatchHistoryFailure.AuthenticationRequired)
            {
                await MarkRequiresReconnectAsync(connection, fresh.Detail, cancellationToken);
                return null;
            }

            // A transient refresh failure leaves the connection alone; the worker will try again.
            return null;
        }

        if (!ReferenceEquals(fresh.Value, parsed))
        {
            await StoreCredentialsAsync(connection, fresh.Value!, cancellationToken);
        }

        return fresh.Value;
    }

    private async Task<WatchHistoryResult<WatchHistoryAuthorizationOutcome>> CompleteAsync(
        int appUserId, WatchHistoryProviderAuthorization attempt, string attemptSecretKey,
        TraktCredentials tokens, CancellationToken cancellationToken)
    {
        var account = await oauth.GetAccountAsync(tokens.AccessToken, cancellationToken);
        if (!account.Succeeded)
        {
            // We hold usable tokens but cannot name the account. Keep the attempt open rather than
            // storing a connection we cannot describe; the next poll retries.
            return WatchHistoryResult<WatchHistoryAuthorizationOutcome>.Failed(
                account.Failure!.Value, account.Detail, account.RetryAfter);
        }

        var now = time.GetUtcNow();
        var connection = await database.WatchHistoryConnections
            .FirstOrDefaultAsync(entry => entry.AppUserId == appUserId && entry.ProviderKey == ProviderKeyValue, cancellationToken);

        if (connection is null)
        {
            connection = new WatchHistoryProviderConnection
            {
                Id = Guid.NewGuid(),
                AppUserId = appUserId,
                ProviderKey = ProviderKeyValue,
                ConnectedAt = now,
            };
            connection.SecretKey = ConnectionSecretKey(connection.Id);
            database.WatchHistoryConnections.Add(connection);
        }

        connection.ProviderAccountId = account.Value!.Id;
        connection.ProviderAccountName = account.Value.Username;
        connection.Status = WatchHistoryConnectionStatus.Connected;
        connection.CredentialExpiresAt = tokens.ExpiresAt;
        connection.LastError = null;

        await StoreCredentialsAsync(connection, tokens, cancellationToken);

        attempt.Status = WatchHistoryAuthorizationStatus.Approved;
        await DeleteSecretQuietlyAsync(attemptSecretKey, cancellationToken);
        database.WatchHistoryAuthorizations.Remove(attempt);
        await database.SaveChangesAsync(cancellationToken);

        return Outcome(WatchHistoryAuthorizationState.Approved, account.Value.Id, account.Value.Username);
    }

    private async Task StoreCredentialsAsync(
        WatchHistoryProviderConnection connection, TraktCredentials tokens, CancellationToken cancellationToken)
    {
        await credentials.SetAsync(connection.SecretKey, JsonSerializer.Serialize(tokens), cancellationToken);
        connection.CredentialExpiresAt = tokens.ExpiresAt;
    }

    private async Task MarkRequiresReconnectAsync(
        WatchHistoryProviderConnection connection, string? detail, CancellationToken cancellationToken)
    {
        connection.Status = WatchHistoryConnectionStatus.RequiresReconnect;
        connection.LastError = detail;
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task FinishAttemptAsync(
        WatchHistoryProviderAuthorization attempt, string secretKey, CancellationToken cancellationToken)
    {
        await DeleteSecretQuietlyAsync(secretKey, cancellationToken);
        database.WatchHistoryAuthorizations.Remove(attempt);
        await database.SaveChangesAsync(cancellationToken);
    }

    // A failure to delete must not block the caller: the row still goes, and the orphaned secret is
    // what the reconciliation pass exists to collect.
    private async Task DeleteSecretQuietlyAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await credentials.DeleteAsync(key, cancellationToken);
        }
        catch (WatchHistoryCredentialStoreException exception)
        {
            logger.LogWarning(exception, "Could not delete a Trakt secret; it will be reconciled later.");
        }
    }

    private static WatchHistoryResult<WatchHistoryAuthorizationOutcome> Outcome(
        WatchHistoryAuthorizationState state, string? accountId = null, string? accountName = null, TimeSpan? retryAfter = null) =>
        WatchHistoryResult<WatchHistoryAuthorizationOutcome>.Success(
            new WatchHistoryAuthorizationOutcome(state, accountId, accountName, retryAfter));
}
