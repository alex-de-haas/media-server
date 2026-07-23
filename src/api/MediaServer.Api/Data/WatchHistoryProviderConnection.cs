namespace MediaServer.Api.Data;

/// <summary>
/// One user's link to one watched-history provider. Credentials are **not** here: the row holds only
/// the Hosty Core secrets-store key under which they live, because the secrets store sits outside the
/// backed-up data directory and this database does not.
/// </summary>
/// <remarks>
/// Unique on <c>(AppUserId, ProviderKey)</c>. The schema allows a user a row per provider, while the
/// first-version service permits only one active connection — the limit is a policy, not a shape, so
/// lifting it later needs no migration. See <c>docs/planning/trakt-watched-state-sync.md</c>.
/// </remarks>
public sealed class WatchHistoryProviderConnection
{
    public Guid Id { get; set; }

    public int AppUserId { get; set; }

    /// <summary>Stable provider key (e.g. <c>trakt</c>), matching the registry.</summary>
    public string ProviderKey { get; set; } = string.Empty;

    /// <summary>The provider's own account id, for display and for detecting a switched account.</summary>
    public string? ProviderAccountId { get; set; }

    /// <summary>Username or display name shown in Settings.</summary>
    public string? ProviderAccountName { get; set; }

    /// <summary>
    /// Key into the Core secrets store holding this connection's credentials — never the credentials.
    /// Deleted from the store before this row is removed, so nothing outlives its owner.
    /// </summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>When the stored access credential expires, when the provider tells us.</summary>
    public DateTimeOffset? CredentialExpiresAt { get; set; }

    public WatchHistoryConnectionStatus Status { get; set; }

    public DateTimeOffset ConnectedAt { get; set; }

    /// <summary>Last time outbound work reached the provider successfully.</summary>
    public DateTimeOffset? LastDeliveryAt { get; set; }

    /// <summary>Last time the user ran an explicit sync.</summary>
    public DateTimeOffset? LastSyncAt { get; set; }

    /// <summary>Sanitized, bounded reason for the current status. Never contains credentials.</summary>
    public string? LastError { get; set; }

    public AppUser? AppUser { get; set; }
}

/// <summary>Health of a provider connection, as Settings shows it.</summary>
public enum WatchHistoryConnectionStatus
{
    /// <summary>Usable.</summary>
    Connected,

    /// <summary>
    /// Credentials are gone or unusable and cannot be refreshed — the secret is missing from the
    /// store, was rejected, or the account was disconnected on the provider's side. The user must
    /// reconnect; pending outbound work is kept rather than discarded.
    /// </summary>
    RequiresReconnect,
}
