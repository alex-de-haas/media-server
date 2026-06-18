namespace MediaServer.Api.Data;

/// <summary>
/// An opaque, revocable access token issued to a Jellyfin client after PIN login. The raw token
/// (≥128 bits of entropy) is returned once and stored only as a hash at rest; the row id doubles as
/// the Jellyfin session id. Tokens are validated locally on every request — Core is consulted only at
/// issuance and session validation. See <c>docs/planning/security.md</c>.
/// </summary>
public sealed class JellyfinAccessToken
{
    public Guid Id { get; set; }

    public Guid CredentialId { get; set; }

    public int AppUserId { get; set; }

    /// <summary>SHA-256 hash (hex) of the opaque token; the plaintext is never persisted.</summary>
    public required string TokenHash { get; set; }

    public string? Client { get; set; }

    public string? DeviceName { get; set; }

    public string? DeviceId { get; set; }

    public string? AppVersion { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    public bool Revoked { get; set; }

    public JellyfinCredential? Credential { get; set; }

    public AppUser? AppUser { get; set; }
}
