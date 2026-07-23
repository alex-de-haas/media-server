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

    /// <summary>
    /// Secrets-store key holding one connection's tokens. Derived from the connection id when the
    /// connection is created, then persisted on the row — unlike the authorization key, which is
    /// recomputed on demand so cleanup never needs to read the row first.
    /// </summary>
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
            if (stored.Succeeded)
            {
                await oauth.RevokeAsync(stored.Value!.AccessToken, cancellationToken);
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
    internal async Task<WatchHistoryResult<TraktCredentials>> ReadCredentialsAsync(
        WatchHistoryProviderConnection connection, CancellationToken cancellationToken)
    {
        var stored = await credentials.GetAsync(connection.SecretKey, cancellationToken);
        if (stored is null)
        {
            await MarkRequiresReconnectAsync(connection, "The stored Trakt credentials are missing.", cancellationToken);
            return WatchHistoryResult<TraktCredentials>.Failed(
                WatchHistoryFailure.AuthenticationRequired, "The stored Trakt credentials are missing.");
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
            return WatchHistoryResult<TraktCredentials>.Failed(
                WatchHistoryFailure.ContractViolation, "The stored Trakt credentials could not be read.");
        }

        var fresh = await oauth.EnsureFreshAsync(parsed, cancellationToken);
        if (!fresh.Succeeded)
        {
            if (fresh.Failure == WatchHistoryFailure.AuthenticationRequired)
            {
                await MarkRequiresReconnectAsync(connection, fresh.Detail, cancellationToken);
            }

            // Otherwise the connection is left alone and the failure keeps its kind: a Trakt outage
            // during refresh must stay retryable, or a worker would give up and ask the user to
            // reconnect an account that was never disconnected.
            return WatchHistoryResult<TraktCredentials>.Failed(fresh.Failure!.Value, fresh.Detail, fresh.RetryAfter);
        }

        if (!ReferenceEquals(fresh.Value, parsed))
        {
            await StoreCredentialsAsync(connection, fresh.Value!, cancellationToken);
        }

        return WatchHistoryResult<TraktCredentials>.Success(fresh.Value!);
    }

    private async Task<WatchHistoryResult<WatchHistoryAuthorizationOutcome>> CompleteAsync(
        int appUserId, WatchHistoryProviderAuthorization attempt, string attemptSecretKey,
        TraktCredentials tokens, CancellationToken cancellationToken)
    {
        // The device code is spent the moment Trakt returns tokens: polling again earns a 409, which
        // reads as "denied". So the tokens must be persisted before anything else is attempted —
        // discarding them because a *later* call failed would tell a user who did approve that they
        // did not, and make them start over.
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

        connection.Status = WatchHistoryConnectionStatus.Connected;
        connection.CredentialExpiresAt = tokens.ExpiresAt;
        connection.LastError = null;

        await StoreCredentialsAsync(connection, tokens, cancellationToken);

        attempt.Status = WatchHistoryAuthorizationStatus.Approved;
        await DeleteSecretQuietlyAsync(attemptSecretKey, cancellationToken);
        database.WatchHistoryAuthorizations.Remove(attempt);
        await database.SaveChangesAsync(cancellationToken);

        // The display name is cosmetic, so it must not gate the connection. If Trakt is briefly
        // unavailable the account stays unnamed until something asks again — a connection that works
        // but is unlabelled beats one the user has to re-authorize.
        var account = await oauth.GetAccountAsync(tokens.AccessToken, cancellationToken);
        if (account.Succeeded)
        {
            connection.ProviderAccountId = account.Value!.Id;
            connection.ProviderAccountName = account.Value.Username;
            await database.SaveChangesAsync(cancellationToken);
        }
        else
        {
            logger.LogInformation("Connected to Trakt, but its account name could not be read yet.");
        }

        return Outcome(
            WatchHistoryAuthorizationState.Approved,
            connection.ProviderAccountId,
            connection.ProviderAccountName);
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
