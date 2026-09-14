using System.Text.RegularExpressions;

namespace MediaServer.Api.Hosty;

/// <summary>Removes obsolete app credentials left in Core by older app versions.</summary>
public sealed partial class LegacyCredentialCleanup(
    IHostyCoreSecrets secrets,
    ILogger<LegacyCredentialCleanup> logger)
{
    /// <summary>Returns true once cleanup succeeds; unavailable Core storage is retried later.</summary>
    public async Task<bool> TryCleanupAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var key in await secrets.ListSecretKeysAsync(cancellationToken))
            {
                if (ObsoleteKey().IsMatch(key))
                {
                    await secrets.DeleteSecretAsync(key, cancellationToken);
                }
            }

            return true;
        }
        catch (CoreSecretsUnavailableException)
        {
            // Neither keys nor credential values belong in logs. Local playback can continue while
            // Core is unavailable; deletion is idempotent, including after a partial cleanup.
            logger.LogWarning("Obsolete app credentials could not be cleaned up; retrying later.");
            return false;
        }
    }

    // Persisted key formats are an upgrade contract. Do not broaden this to arbitrary app secrets.
    [GeneratedRegex(@"\Atrakt\.(?:connection\.[0-9a-f]{32}\.tokens|authorization\.[0-9a-f]{32}\.device)\z", RegexOptions.CultureInvariant)]
    private static partial Regex ObsoleteKey();
}

/// <summary>Retries upgrade credential cleanup independently of local history and playback.</summary>
public sealed class LegacyCredentialCleanupWorker(
    LegacyCredentialCleanup cleanup,
    IHostyCoreClient core,
    TimeProvider time,
    ILogger<LegacyCredentialCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!core.IsEnabled)
        {
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (await cleanup.TryCleanupAsync(stoppingToken))
                    {
                        return;
                    }
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    // Exception messages can contain response data or credential keys. Log only
                    // the failure type, and let local playback continue while cleanup retries.
                    logger.LogWarning(
                        "Obsolete app credential cleanup failed unexpectedly ({ExceptionType}); retrying later.",
                        exception.GetType().Name);
                }

                await Task.Delay(TimeSpan.FromMinutes(5), time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown, including cancellation during a Core call or the retry delay.
        }
    }
}
