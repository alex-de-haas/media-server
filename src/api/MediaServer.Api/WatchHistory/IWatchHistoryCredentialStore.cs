namespace MediaServer.Api.WatchHistory;

/// <summary>
/// Where a provider's credentials live. Deliberately a seam rather than a direct call into the Hosty
/// secrets client: the adapter's business is "keep this connection's tokens somewhere durable and out
/// of our database", not which store implements that.
/// </summary>
/// <remarks>
/// The store must sit outside the app data directory, because Hosty backups are a copy of that
/// directory and this app's SQLite file is inside it — plaintext refresh tokens in a backup archive
/// would be live account access. A missing value is an expected, meaningful answer: it means the user
/// has to reconnect, which is exactly what a restored database or a rebuilt host produces.
/// </remarks>
public interface IWatchHistoryCredentialStore
{
    /// <summary>Reads a stored credential, or null when there is none.</summary>
    Task<string?> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>Stores or replaces a credential.</summary>
    Task SetAsync(string key, string value, CancellationToken cancellationToken);

    /// <summary>Removes a credential. Removing an absent one succeeds.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Names of every credential this app has stored — names only, never values. Reconciliation uses
    /// it to find secrets whose owning row is gone, which a crash between the two deletions can leave
    /// behind.
    /// </summary>
    Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Raised when the credential store cannot be reached or refuses a request. A *missing* credential is
/// not an error — <see cref="IWatchHistoryCredentialStore.GetAsync"/> returns null for that.
/// </summary>
public sealed class WatchHistoryCredentialStoreException : Exception
{
    public WatchHistoryCredentialStoreException(string message)
        : base(message)
    {
    }

    public WatchHistoryCredentialStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
