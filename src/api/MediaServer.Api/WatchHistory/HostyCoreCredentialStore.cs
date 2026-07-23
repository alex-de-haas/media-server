using MediaServer.Api.Hosty;

namespace MediaServer.Api.WatchHistory;

/// <summary>
/// Keeps provider credentials in Hosty Core's app secrets store.
/// </summary>
/// <remarks>
/// Core holds them outside this app's data directory, which is what makes them safe: Hosty backups
/// copy that directory, and this app's SQLite file is in it, so a token stored locally would ride
/// along into every archive. It also means a restored database can reference a connection whose
/// secret is still current — better than restoring a stale token, since Trakt rotates the refresh
/// token on every exchange.
/// </remarks>
public sealed class HostyCoreCredentialStore(IHostyCoreSecrets core) : IWatchHistoryCredentialStore
{
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            return await core.GetSecretAsync(key, cancellationToken);
        }
        catch (CoreSecretsUnavailableException exception)
        {
            // Translated rather than swallowed: a caller must not read "Core is down" as "the user has
            // no credentials", which would flip a working connection to RequiresReconnect.
            throw new WatchHistoryCredentialStoreException(exception.Message, exception);
        }
    }

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        try
        {
            await core.SetSecretAsync(key, value, cancellationToken);
        }
        catch (CoreSecretsUnavailableException exception)
        {
            throw new WatchHistoryCredentialStoreException(exception.Message, exception);
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await core.DeleteSecretAsync(key, cancellationToken);
        }
        catch (CoreSecretsUnavailableException exception)
        {
            throw new WatchHistoryCredentialStoreException(exception.Message, exception);
        }
    }

    public async Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await core.ListSecretKeysAsync(cancellationToken);
        }
        catch (CoreSecretsUnavailableException exception)
        {
            throw new WatchHistoryCredentialStoreException(exception.Message, exception);
        }
    }
}
