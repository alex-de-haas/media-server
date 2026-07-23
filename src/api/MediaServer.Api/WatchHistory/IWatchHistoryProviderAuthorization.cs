namespace MediaServer.Api.WatchHistory;

/// <summary>What to show a user so they can approve the connection on the provider's side.</summary>
/// <param name="UserCode">Short code the user types. Safe to display.</param>
/// <param name="VerificationUrl">Where they type it. Safe to display.</param>
/// <param name="ExpiresAt">When the attempt stops being usable.</param>
/// <param name="PollInterval">Minimum gap between polls, as the provider asked.</param>
public sealed record WatchHistoryAuthorizationPrompt(
    string UserCode, string VerificationUrl, DateTimeOffset ExpiresAt, TimeSpan PollInterval);

/// <summary>Where an authorization attempt stands.</summary>
public enum WatchHistoryAuthorizationState
{
    /// <summary>The user has not acted yet. Keep polling at the provider's interval.</summary>
    Pending,

    /// <summary>Approved; credentials were stored.</summary>
    Approved,

    /// <summary>The user declined.</summary>
    Denied,

    /// <summary>The attempt aged out before approval.</summary>
    Expired,

    /// <summary>We polled too fast; back off before the next attempt.</summary>
    SlowDown,
}

/// <summary>Result of one poll.</summary>
/// <param name="State">Where the attempt stands.</param>
/// <param name="AccountId">Provider account id, once approved.</param>
/// <param name="AccountName">Display identity, once approved.</param>
/// <param name="RetryAfter">Extra delay to apply, when the provider asked us to slow down.</param>
public sealed record WatchHistoryAuthorizationOutcome(
    WatchHistoryAuthorizationState State,
    string? AccountId = null,
    string? AccountName = null,
    TimeSpan? RetryAfter = null);

/// <summary>
/// Connecting and disconnecting one user's account. Separate from <see cref="IWatchHistoryProvider"/>
/// so device-code OAuth is this provider's business rather than a requirement on every future one: a
/// provider authenticated by an operator-entered API key implements this trivially, or not at all.
/// </summary>
/// <remarks>
/// Implementations act for the calling user only. There is no overload taking an arbitrary app-user id
/// — an administrator configures the instance's provider application but cannot connect an account on
/// someone else's behalf, nor read their credentials.
/// </remarks>
public interface IWatchHistoryProviderAuthorization
{
    string ProviderKey { get; }

    /// <summary>True when the operator has configured everything this provider needs to be usable.</summary>
    bool IsConfigured { get; }

    /// <summary>Begins an attempt and returns what to show the user.</summary>
    Task<WatchHistoryResult<WatchHistoryAuthorizationPrompt>> StartAsync(int appUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Polls the pending attempt. On approval the adapter stores credentials itself — they never pass
    /// through the core, and never appear in this result.
    /// </summary>
    Task<WatchHistoryResult<WatchHistoryAuthorizationOutcome>> PollAsync(int appUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Revokes remotely on a best-effort basis, then drops the stored credentials. Local playback data
    /// is never touched. A failed revocation must still delete locally: leaving a credential behind
    /// after the user asked to disconnect is the worse outcome.
    /// </summary>
    Task DisconnectAsync(int appUserId, CancellationToken cancellationToken);
}
