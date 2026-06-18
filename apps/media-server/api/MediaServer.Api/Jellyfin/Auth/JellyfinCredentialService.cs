using System.Security.Cryptography;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Jellyfin.Auth;

public enum JellyfinAuthFailure
{
    InvalidCredentials,
    TemporarilyLocked,
    PermanentlyLocked,
    Revoked,
}

/// <summary>Raised when a Jellyfin PIN login is rejected; the reason drives the HTTP response.</summary>
public sealed class JellyfinAuthException(JellyfinAuthFailure reason)
    : Exception(reason.ToString())
{
    public JellyfinAuthFailure Reason { get; } = reason;
}

/// <summary>Validation failure when creating/regenerating a credential (e.g. an out-of-range PIN).</summary>
public sealed class JellyfinCredentialValidationException(string message) : Exception(message);

public sealed record JellyfinDeviceContext(string? Client, string? DeviceName, string? DeviceId, string? AppVersion);

/// <summary>The newly issued credential; <see cref="GeneratedPin"/> is non-null only when the server generated it.</summary>
public sealed record IssuedCredential(Guid Id, string Username, string? GeneratedPin);

/// <summary>A successful PIN login: the persisted token plus the raw token to hand back to the client once.</summary>
public sealed record AuthenticatedSession(JellyfinAccessToken Token, JellyfinCredential Credential, AppUser User, string RawToken);

/// <summary>A validated token resolved from a request, used by the authentication handler.</summary>
public sealed record ValidatedToken(JellyfinAccessToken Token, AppUser User);

/// <summary>
/// Manages Media Server-owned Jellyfin credentials and access tokens: creation/regeneration with an
/// argon2id PIN, PIN login with consecutive-failure lockout (temporary at 10 with a growing window,
/// permanent at 100), opaque-token issuance/validation, and logout/revocation. See
/// <c>docs/planning/security.md</c> and <c>docs/planning/jellyfin-compatibility.md</c>.
/// </summary>
public sealed class JellyfinCredentialService(MediaServerDbContext database, IPinHasher pinHasher, TimeProvider time)
{
    internal const int MinPinLength = 6;
    internal const int MaxPinLength = 8;
    internal const int TemporaryLockThreshold = 10;
    internal const int PermanentLockThreshold = 100;
    private static readonly TimeSpan MaxLockWindow = TimeSpan.FromHours(1);
    private static readonly TimeSpan SessionActivityWriteThreshold = TimeSpan.FromMinutes(5);

    // A well-formed dummy hash so a missing/revoked username still pays the argon2id verification
    // cost — otherwise response timing would distinguish unknown usernames (enumeration oracle).
    private static readonly Lazy<string> DummyPinHash = new(() => new Argon2idPinHasher().Hash("timing-equalizer"));

    public async Task<IssuedCredential> CreateOrRegenerateAsync(AppUser user, string? requestedPin, CancellationToken cancellationToken)
    {
        string pin;
        string? generated = null;
        if (string.IsNullOrEmpty(requestedPin))
        {
            pin = GeneratePin();
            generated = pin;
        }
        else
        {
            ValidatePin(requestedPin);
            pin = requestedPin;
        }

        var username = (string.IsNullOrWhiteSpace(user.Email) ? user.HostUserId : user.Email).Trim();
        if (string.IsNullOrEmpty(username))
        {
            throw new JellyfinCredentialValidationException("The user has no email to use as a Jellyfin username.");
        }

        var now = time.GetUtcNow();
        var credential = await database.JellyfinCredentials
            .FirstOrDefaultAsync(candidate => candidate.AppUserId == user.Id, cancellationToken);

        if (credential is null)
        {
            credential = new JellyfinCredential
            {
                Id = Guid.NewGuid(),
                AppUserId = user.Id,
                HostUserId = user.HostUserId,
                Username = username,
                PinHash = pinHasher.Hash(pin),
                CreatedAt = now,
            };
            database.JellyfinCredentials.Add(credential);
        }
        else
        {
            // Regenerating clears every lockout and invalidates all outstanding tokens.
            credential.HostUserId = user.HostUserId;
            credential.Username = username;
            credential.PinHash = pinHasher.Hash(pin);
            credential.FailedAttempts = 0;
            credential.LockedUntil = null;
            credential.PermanentlyLocked = false;
            credential.Revoked = false;
            credential.CreatedAt = now;
            credential.LastUsedAt = null;
            await RevokeTokensAsync(credential.Id, cancellationToken);
        }

        await database.SaveChangesAsync(cancellationToken);
        return new IssuedCredential(credential.Id, username, generated);
    }

    public async Task<AuthenticatedSession> AuthenticateAsync(
        string username, string pin, JellyfinDeviceContext device, CancellationToken cancellationToken)
    {
        var normalized = (username ?? string.Empty).Trim();
        var credential = await database.JellyfinCredentials
            .FirstOrDefaultAsync(candidate => candidate.Username == normalized, cancellationToken);

        // Do not reveal whether the username exists; run a dummy verification first so the timing
        // of a missing/revoked username matches that of a wrong PIN.
        if (credential is null || credential.Revoked)
        {
            pinHasher.Verify(pin ?? string.Empty, DummyPinHash.Value);
            throw new JellyfinAuthException(credential is null ? JellyfinAuthFailure.InvalidCredentials : JellyfinAuthFailure.Revoked);
        }

        if (credential.PermanentlyLocked)
        {
            throw new JellyfinAuthException(JellyfinAuthFailure.PermanentlyLocked);
        }

        var now = time.GetUtcNow();
        if (credential.LockedUntil is { } until && until > now)
        {
            throw new JellyfinAuthException(JellyfinAuthFailure.TemporarilyLocked);
        }

        if (string.IsNullOrEmpty(pin) || !pinHasher.Verify(pin, credential.PinHash))
        {
            RegisterFailure(credential, now);
            await database.SaveChangesAsync(cancellationToken);
            throw new JellyfinAuthException(
                credential.PermanentlyLocked ? JellyfinAuthFailure.PermanentlyLocked : JellyfinAuthFailure.InvalidCredentials);
        }

        var user = await database.AppUsers.FirstOrDefaultAsync(candidate => candidate.Id == credential.AppUserId, cancellationToken)
            ?? throw new JellyfinAuthException(JellyfinAuthFailure.Revoked);

        // A successful login clears the consecutive-failure counter and any temporary lock.
        credential.FailedAttempts = 0;
        credential.LockedUntil = null;
        credential.LastUsedAt = now;

        var raw = AccessTokens.Generate();
        var token = new JellyfinAccessToken
        {
            Id = Guid.NewGuid(),
            CredentialId = credential.Id,
            AppUserId = credential.AppUserId,
            TokenHash = AccessTokens.Hash(raw),
            Client = device.Client,
            DeviceName = device.DeviceName,
            DeviceId = device.DeviceId,
            AppVersion = device.AppVersion,
            CreatedAt = now,
            LastSeenAt = now,
        };
        database.JellyfinAccessTokens.Add(token);
        await database.SaveChangesAsync(cancellationToken);

        return new AuthenticatedSession(token, credential, user, raw);
    }

    public async Task<ValidatedToken?> ValidateTokenAsync(string rawToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return null;
        }

        var hash = AccessTokens.Hash(rawToken.Trim());
        var token = await database.JellyfinAccessTokens
            .Include(entity => entity.Credential)
            .Include(entity => entity.AppUser)
            .FirstOrDefaultAsync(entity => entity.TokenHash == hash && !entity.Revoked, cancellationToken);

        if (token is null || token.Credential is null || token.Credential.Revoked || token.Credential.PermanentlyLocked || token.AppUser is null)
        {
            return null;
        }

        // Record session activity, but throttle the write so it does not run on every request.
        var now = time.GetUtcNow();
        if (now - token.LastSeenAt > SessionActivityWriteThreshold)
        {
            token.LastSeenAt = now;
            await database.SaveChangesAsync(cancellationToken);
        }

        return new ValidatedToken(token, token.AppUser);
    }

    /// <summary>Revokes a single session token (Jellyfin <c>POST /Sessions/Logout</c>).</summary>
    public async Task LogoutAsync(Guid tokenId, CancellationToken cancellationToken)
    {
        var token = await database.JellyfinAccessTokens.FirstOrDefaultAsync(entity => entity.Id == tokenId, cancellationToken);
        if (token is null || token.Revoked)
        {
            return;
        }

        token.Revoked = true;
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<JellyfinCredential?> GetCredentialAsync(int appUserId, CancellationToken cancellationToken) =>
        await database.JellyfinCredentials.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.AppUserId == appUserId, cancellationToken);

    /// <summary>Revokes the credential and all its tokens (operator/user action).</summary>
    public async Task<bool> RevokeCredentialAsync(int appUserId, CancellationToken cancellationToken)
    {
        var credential = await database.JellyfinCredentials
            .FirstOrDefaultAsync(candidate => candidate.AppUserId == appUserId, cancellationToken);
        if (credential is null)
        {
            return false;
        }

        credential.Revoked = true;
        await RevokeTokensAsync(credential.Id, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    private void RegisterFailure(JellyfinCredential credential, DateTimeOffset now)
    {
        credential.FailedAttempts++;

        if (credential.FailedAttempts >= PermanentLockThreshold)
        {
            credential.PermanentlyLocked = true;
            credential.LockedUntil = null;
        }
        else if (credential.FailedAttempts >= TemporaryLockThreshold)
        {
            credential.LockedUntil = now + LockWindow(credential.FailedAttempts);
        }
    }

    /// <summary>Temporary-lock window grows with each block of failures: 1m, 2m, 4m, … capped at 1h.</summary>
    private static TimeSpan LockWindow(int failedAttempts)
    {
        var level = failedAttempts / TemporaryLockThreshold;
        var seconds = Math.Min(MaxLockWindow.TotalSeconds, 60d * Math.Pow(2, level - 1));
        return TimeSpan.FromSeconds(seconds);
    }

    private async Task RevokeTokensAsync(Guid credentialId, CancellationToken cancellationToken)
    {
        var tokens = await database.JellyfinAccessTokens
            .Where(token => token.CredentialId == credentialId && !token.Revoked)
            .ToListAsync(cancellationToken);
        foreach (var token in tokens)
        {
            token.Revoked = true;
        }
    }

    private static void ValidatePin(string pin)
    {
        if (pin.Length < MinPinLength || pin.Length > MaxPinLength || !pin.All(char.IsAsciiDigit))
        {
            throw new JellyfinCredentialValidationException($"PIN must be {MinPinLength}–{MaxPinLength} digits.");
        }
    }

    private static string GeneratePin()
    {
        // Uniform 6-digit numeric PIN.
        return RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
    }
}
