namespace MediaServer.Api.Data;

/// <summary>
/// A Media Server-owned native-client credential (username + 6–8 digit PIN) bound to an internal
/// <see cref="AppUser"/>, used by Jellyfin clients such as Infuse that cannot perform the Hosty
/// app-code flow. The PIN is verified only at login (argon2id hash); brute-force protection lives on
/// this row as consecutive-failure counters plus temporary/permanent lockout. See
/// <c>docs/features/security.md</c> and <c>docs/features/jellyfin-compatibility.md</c>.
/// </summary>
public sealed class JellyfinCredential
{
    public Guid Id { get; set; }

    public int AppUserId { get; set; }

    /// <summary>Snapshot of the linked Hosty user id at creation, for re-checking Core assignment.</summary>
    public required string HostUserId { get; set; }

    /// <summary>Login name shown to the operator — the Hosty email for familiarity.</summary>
    public required string Username { get; set; }

    /// <summary>argon2id hash of the PIN; never stored or logged in plaintext.</summary>
    public required string PinHash { get; set; }

    /// <summary>Consecutive failed login attempts; reset to zero on a successful login.</summary>
    public int FailedAttempts { get; set; }

    /// <summary>Temporary lockout expiry (grows with the failure count); null when not locked.</summary>
    public DateTimeOffset? LockedUntil { get; set; }

    /// <summary>Set after 100 consecutive failures; cleared only by regenerating the credential.</summary>
    public bool PermanentlyLocked { get; set; }

    public bool Revoked { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    public AppUser? AppUser { get; set; }

    public ICollection<JellyfinAccessToken> Tokens { get; set; } = new List<JellyfinAccessToken>();
}
