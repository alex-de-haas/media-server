namespace MediaServer.Api.WatchHistory;

/// <summary>
/// One play of one item, as providers model it. <see cref="WatchedAt"/> is null for a "watched, time
/// unknown" mark — the only thing that can be said about a manual toggle or a legacy aggregate row.
/// </summary>
/// <param name="Identity">Which movie or episode.</param>
/// <param name="WatchedAt">Server UTC of a proven completion, or null for a timeless mark.</param>
/// <param name="RemoteId">The provider's own id for this entry, once known.</param>
public sealed record WatchHistoryPlay(WatchHistoryIdentity Identity, DateTimeOffset? WatchedAt, string? RemoteId = null);

/// <summary>What an adapter can actually do. The core asks before it acts.</summary>
/// <remarks>
/// Capabilities exist so the core never has to guess and never has to degrade silently. An operation
/// the provider cannot support becomes a typed issue the user can see, rather than a fabricated
/// timestamp or — worse — a broader destructive call standing in for a precise one.
/// </remarks>
public sealed record WatchHistoryCapabilities
{
    /// <summary>Can record a play at an exact timestamp.</summary>
    public required bool ExactTimestampWrites { get; init; }

    /// <summary>Can record a play whose time is unknown.</summary>
    public required bool TimelessWrites { get; init; }

    /// <summary>
    /// Can report which items are watched without per-play detail. Nothing consumes this yet; the
    /// sync preview that will use it lands in a later slice. It stays here because it is a real
    /// question about a provider, which an adapter author can answer today.
    /// </summary>
    public required bool AggregateWatchedReads { get; init; }

    /// <summary>
    /// Can report every individual play, which is what <see cref="IWatchHistoryProvider.GetHistoryAsync"/>
    /// returns. Paging is the adapter's business: the contract hands back the whole list for the
    /// identities asked about, so a provider that paginates does so internally.
    /// </summary>
    public required bool FullHistoryReads { get; init; }

    /// <summary>
    /// Can delete one identified history entry, leaving others intact — what
    /// <see cref="IWatchHistoryProvider.RemoveEntriesAsync"/> needs. A provider that can only remove
    /// everything for an item declares false here rather than having the core substitute the broader
    /// call, which would take other clients' plays with it.
    /// </summary>
    public required bool IndividualEntryRemoval { get; init; }
}

/// <summary>Why a provider operation did not succeed, in terms the core can act on.</summary>
public enum WatchHistoryFailure
{
    /// <summary>Transport, timeout, or 5xx — worth retrying later.</summary>
    Transient,

    /// <summary>Provider asked us to slow down; honour <see cref="WatchHistoryResult.RetryAfter"/>.</summary>
    RateLimited,

    /// <summary>Credentials are missing, expired beyond refresh, or revoked. Needs the user to reconnect.</summary>
    AuthenticationRequired,

    /// <summary>The provider cannot express what was asked (see <see cref="WatchHistoryCapabilities"/>).</summary>
    Unsupported,

    /// <summary>The identity was rejected or is unknown to the provider — retrying will not help.</summary>
    IdentityRejected,

    /// <summary>The provider answered, but not in a way we can use. Terminal until someone looks.</summary>
    ContractViolation,
}

/// <summary>
/// Outcome of a provider call: a value, or a typed failure. Expected failures are results rather than
/// exceptions so a worker can classify and schedule them without catching by type; exceptions stay for
/// the genuinely unexpected.
/// </summary>
public sealed record WatchHistoryResult<T>
{
    private WatchHistoryResult()
    {
    }

    public T? Value { get; private init; }

    public WatchHistoryFailure? Failure { get; private init; }

    /// <summary>Sanitized, loggable detail. Never contains credentials, tokens, or media titles.</summary>
    public string? Detail { get; private init; }

    /// <summary>Provider-requested delay before retrying, when it supplied one.</summary>
    public TimeSpan? RetryAfter { get; private init; }

    public bool Succeeded => Failure is null;

    /// <summary>True when trying again later could plausibly succeed without user action.</summary>
    public bool IsRetryable => Failure is WatchHistoryFailure.Transient or WatchHistoryFailure.RateLimited;

    public static WatchHistoryResult<T> Success(T value) => new() { Value = value };

    public static WatchHistoryResult<T> Failed(WatchHistoryFailure failure, string? detail = null, TimeSpan? retryAfter = null) =>
        new() { Failure = failure, Detail = detail, RetryAfter = retryAfter };
}

/// <summary>
/// A watched-history backend. Trakt is the first; nothing in this contract is Trakt-shaped.
/// </summary>
/// <remarks>
/// Everything provider-specific — DTOs, OAuth details, credentials, timestamp sentinels, rate-limit
/// headers, transport errors, remote id formats — stays behind this interface. Authorization is a
/// separate collaborator (<see cref="IWatchHistoryProviderAuthorization"/>) so a future provider that
/// authenticates with an API key is not forced through a device-code flow.
/// </remarks>
public interface IWatchHistoryProvider
{
    /// <summary>Stable key used in storage, settings and API routes (e.g. <c>trakt</c>).</summary>
    string Key { get; }

    /// <summary>Name for the operator's eyes.</summary>
    string DisplayName { get; }

    WatchHistoryCapabilities Capabilities { get; }

    /// <summary>Every play the provider holds for these identities. Requires <see cref="WatchHistoryCapabilities.FullHistoryReads"/>.</summary>
    Task<WatchHistoryResult<IReadOnlyList<WatchHistoryPlay>>> GetHistoryAsync(
        int appUserId, IReadOnlyCollection<WatchHistoryIdentity> identities, CancellationToken cancellationToken);

    /// <summary>Adds plays. A null <c>WatchedAt</c> requires <see cref="WatchHistoryCapabilities.TimelessWrites"/>.</summary>
    Task<WatchHistoryResult<IReadOnlyList<WatchHistoryPlay>>> AddPlaysAsync(
        int appUserId, IReadOnlyCollection<WatchHistoryPlay> plays, CancellationToken cancellationToken);

    /// <summary>
    /// Removes entries by their provider ids. Callers pass only ids this app created and stored;
    /// the adapter must remove exactly those and never fall back to a whole-item removal.
    /// </summary>
    Task<WatchHistoryResult<int>> RemoveEntriesAsync(
        int appUserId, IReadOnlyCollection<string> remoteIds, CancellationToken cancellationToken);
}
