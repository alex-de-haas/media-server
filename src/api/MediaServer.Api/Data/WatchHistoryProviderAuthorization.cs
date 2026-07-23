namespace MediaServer.Api.Data;

/// <summary>
/// One short-lived attempt to connect an account, while the user approves it on the provider's site.
/// </summary>
/// <remarks>
/// The device code is a credential, so it lives in the Core secrets store, not here. Its key is
/// **derived** from this row's id (<c>{providerKey}.authorization.{id}.device</c>) rather than stored,
/// so cleanup never has to read the row first: a reconciliation pass can list secret names, match the
/// pattern, and delete the ones with no live row. Without that, a denied or abandoned attempt would
/// strand its device code in Core with nothing left in SQLite pointing at it.
/// </remarks>
public sealed class WatchHistoryProviderAuthorization
{
    public Guid Id { get; set; }

    public int AppUserId { get; set; }

    public string ProviderKey { get; set; } = string.Empty;

    /// <summary>Short code the user types on the provider's site. Safe to display and to log.</summary>
    public string UserCode { get; set; } = string.Empty;

    /// <summary>Where the user types it. Safe to display.</summary>
    public string VerificationUrl { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Minimum gap between polls, as the provider asked. Polling faster earns a slow-down.</summary>
    public int PollIntervalSeconds { get; set; }

    /// <summary>Earliest time the next poll may run; pushed out when the provider says slow down.</summary>
    public DateTimeOffset NextPollAt { get; set; }

    public WatchHistoryAuthorizationStatus Status { get; set; }

    public AppUser? AppUser { get; set; }

    /// <summary>
    /// The secrets-store key for this attempt's device code. Derived, never persisted, so the two can
    /// never disagree.
    /// </summary>
    public static string SecretKeyFor(string providerKey, Guid authorizationId) =>
        $"{providerKey.ToLowerInvariant()}.authorization.{authorizationId:N}.device";
}

/// <summary>Where an authorization attempt stands. Terminal states are cleaned up with their secret.</summary>
public enum WatchHistoryAuthorizationStatus
{
    Pending,
    Approved,
    Denied,
    Expired,
}
