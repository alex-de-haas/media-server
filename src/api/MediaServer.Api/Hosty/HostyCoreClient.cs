using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using HostySdk.App;

namespace MediaServer.Api.Hosty;

/// <summary>Operator notification severity; serialized lowercase to match the Core contract.</summary>
public enum CoreNotificationLevel
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>Result of an on-demand Core backup. <see cref="Status"/> is <c>completed</c> or <c>empty</c>.</summary>
public sealed record CoreBackupResult(string Status, string? BackupId)
{
    public bool Completed => string.Equals(Status, "completed", StringComparison.Ordinal);
}

/// <summary>A user assigned to this app, as reported by Core's scoped directory.</summary>
public sealed record CoreDirectoryUser(string Id, string? DisplayName, string? Email, string HostRole);

/// <summary>
/// Raised when Core's secrets store could not be reached or refused a request. A *missing* secret is
/// not this — that is a null from <see cref="IHostyCoreClient.GetSecretAsync"/>.
/// </summary>
public sealed class CoreSecretsUnavailableException : Exception
{
    public CoreSecretsUnavailableException(string message)
        : base(message)
    {
    }

    public CoreSecretsUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Hosty Core's app secrets store: durable credentials kept outside this app's data directory, and so
/// outside Hosty's backups.
/// </summary>
/// <remarks>
/// Separate from <see cref="IHostyCoreClient"/> on purpose. Everything there folds failure into null
/// because a missed notification or backup is survivable; here, "no secret" and "Core unreachable"
/// mean opposite things to a caller holding a credential — the first says the user must reconnect,
/// the second says try again shortly — so failures throw rather than collapse into an absence.
/// </remarks>
public interface IHostyCoreSecrets
{
    /// <summary>
    /// Reads one of this app's stored secrets, or null when none is stored under that key.
    /// </summary>
    /// <remarks>
    /// Unlike the other calls here, secrets do **not** fold failure into null. "No secret" and "Core
    /// could not be reached" mean opposite things to a caller holding a credential: the first says
    /// the user must reconnect, the second says try again shortly. Collapsing them would make a brief
    /// Core outage look like a revoked account and send users to re-authorize for nothing.
    /// </remarks>
    /// <exception cref="CoreSecretsUnavailableException">Core was unreachable or refused the request.</exception>
    Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken);

    /// <summary>Stores or replaces one of this app's secrets.</summary>
    /// <exception cref="CoreSecretsUnavailableException">Core was unreachable or refused the request.</exception>
    Task SetSecretAsync(string key, string value, CancellationToken cancellationToken);

    /// <summary>Deletes one of this app's secrets. Deleting an absent key succeeds.</summary>
    /// <exception cref="CoreSecretsUnavailableException">Core was unreachable or refused the request.</exception>
    Task DeleteSecretAsync(string key, CancellationToken cancellationToken);

    /// <summary>Names of this app's stored secrets — names only, never values.</summary>
    /// <exception cref="CoreSecretsUnavailableException">Core was unreachable or refused the request.</exception>
    Task<IReadOnlyList<string>> ListSecretKeysAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Talks to Hosty Core's internal app APIs using the injected <c>HOSTY_APP_SERVICE_TOKEN</c> as bearer:
/// on-demand backups, operator notifications, and the scoped user directory. Contracts verified against
/// the sibling <c>docker-host</c> repo. All calls no-op (return null/false) when the app is not Core
/// managed (no service token), so standalone local runs stay functional.
/// </summary>
public interface IHostyCoreClient
{
    /// <summary>True only when Core has provisioned a service token (running under Core).</summary>
    bool IsEnabled { get; }

    /// <summary>Requests an on-demand snapshot before a risky local operation (e.g. EF migrations).</summary>
    Task<CoreBackupResult?> CreateBackupAsync(string? note, CancellationToken cancellationToken);


    /// <summary>
    /// Publishes a user-audience operator notification. <paramref name="target"/> defaults to
    /// <c>broadcast</c> (every assigned user); <paramref name="dedupeKey"/> suppresses repeats while an
    /// identical notification is still unread. Returns false when not Core managed or the call failed.
    /// </summary>
    Task<bool> PublishNotificationAsync(
        CoreNotificationLevel level,
        string title,
        string? body,
        string? link,
        string? dedupeKey,
        string target = HostyCoreClient.BroadcastTarget,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the enabled users currently assigned to this app, or null if the call failed.</summary>
    Task<IReadOnlyList<CoreDirectoryUser>?> ListDirectoryUsersAsync(CancellationToken cancellationToken);
}

public sealed class HostyCoreClient(
    IHttpClientFactory httpClientFactory,
    HostyOptions options,
    ILogger<HostyCoreClient> logger)
    : IHostyCoreClient, IHostyCoreSecrets
{
    public const string BroadcastTarget = "broadcast";

    // Reuses the Core HTTP client (BaseAddress = HOSTY_CORE_ORIGIN) registered for identity validation.
    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web);

    public bool IsEnabled => options.IsCoreManaged;

    public async Task<CoreBackupResult?> CreateBackupAsync(string? note, CancellationToken cancellationToken)
    {
        if (await TrySendAsync(
                HttpMethod.Post,
                $"/api/internal/apps/{options.AppId}/backups",
                new { note },
                "create backup",
                cancellationToken) is not { } response)
        {
            return null;
        }

        using (response)
        {
            // 201 completed, 200 empty (no data directory yet) — both mean "safe to proceed".
            var payload = await response.Content.ReadFromJsonAsync<BackupResponse>(ReadOptions, cancellationToken);
            return payload is null ? null : new CoreBackupResult(payload.Status ?? "completed", payload.BackupId);
        }
    }

    public async Task<bool> PublishNotificationAsync(
        CoreNotificationLevel level,
        string title,
        string? body,
        string? link,
        string? dedupeKey,
        string target = BroadcastTarget,
        CancellationToken cancellationToken = default)
    {
        var request = new
        {
            target,
            audience = "user", // apps may not target host-admin; Core forces this anyway.
            level = level.ToString().ToLowerInvariant(),
            title,
            body,
            link,
            dedupeKey,
        };

        if (await TrySendAsync(
                HttpMethod.Post,
                $"/api/internal/apps/{options.AppId}/notifications",
                request,
                "publish notification",
                cancellationToken) is not { } response)
        {
            return false;
        }

        response.Dispose();
        return true;
    }

    public async Task<IReadOnlyList<CoreDirectoryUser>?> ListDirectoryUsersAsync(CancellationToken cancellationToken)
    {
        if (await TrySendAsync(
                HttpMethod.Get,
                $"/api/internal/apps/{options.AppId}/directory/users",
                payload: null,
                "list directory users",
                cancellationToken) is not { } response)
        {
            return null;
        }

        using (response)
        {
            var payload = await response.Content.ReadFromJsonAsync<DirectoryResponse>(ReadOptions, cancellationToken);
            return payload?.Users ?? [];
        }
    }

    public async Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken)
    {
        using var response = await SendSecretsAsync(HttpMethod.Get, SecretPath(key), payload: null, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureSecretsSuccess(response, "read");
        var payload = await response.Content.ReadFromJsonAsync<SecretValueResponse>(ReadOptions, cancellationToken);
        return payload?.Value;
    }

    public async Task SetSecretAsync(string key, string value, CancellationToken cancellationToken)
    {
        using var response = await SendSecretsAsync(HttpMethod.Put, SecretPath(key), new { value }, cancellationToken);
        EnsureSecretsSuccess(response, "write");
    }

    public async Task DeleteSecretAsync(string key, CancellationToken cancellationToken)
    {
        using var response = await SendSecretsAsync(HttpMethod.Delete, SecretPath(key), payload: null, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return;
        }

        EnsureSecretsSuccess(response, "delete");
    }

    public async Task<IReadOnlyList<string>> ListSecretKeysAsync(CancellationToken cancellationToken)
    {
        using var response = await SendSecretsAsync(
            HttpMethod.Get, $"/api/internal/apps/{options.AppId}/secrets", payload: null, cancellationToken);
        EnsureSecretsSuccess(response, "list");
        var payload = await response.Content.ReadFromJsonAsync<SecretKeysResponse>(ReadOptions, cancellationToken);
        return payload?.Keys ?? [];
    }

    private string SecretPath(string key) =>
        $"/api/internal/apps/{options.AppId}/secrets/{Uri.EscapeDataString(key)}";

    // Deliberately does not reuse TrySendAsync: that folds every failure into null, which is right for
    // fire-and-forget calls and wrong here, where "absent" and "unreachable" lead to opposite actions.
    private async Task<HttpResponseMessage> SendSecretsAsync(
        HttpMethod method, string path, object? payload, CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            throw new CoreSecretsUnavailableException(
                "The app secrets store is only available when running under Hosty Core.");
        }

        var client = httpClientFactory.CreateClient(CoreIdentityValidator.HttpClientName);
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ServiceToken);
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }

        try
        {
            return await client.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new CoreSecretsUnavailableException("Hosty Core could not be reached for the app secrets store.", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CoreSecretsUnavailableException("The app secrets request to Hosty Core timed out.", exception);
        }
    }

    private static void EnsureSecretsSuccess(HttpResponseMessage response, string operation)
    {
        if (!response.IsSuccessStatusCode)
        {
            // The status is safe to surface; Core's secret error bodies carry codes and limits, never
            // the stored value.
            throw new CoreSecretsUnavailableException(
                $"Hosty Core returned HTTP {(int)response.StatusCode} for an app secrets {operation}.");
        }
    }

    private sealed record SecretValueResponse(string? Value);

    private sealed record SecretKeysResponse(IReadOnlyList<string>? Keys);

    /// <summary>
    /// Sends an authenticated request to Core, returning the response only on success. Returns null
    /// (after disposing any response) when the app is not Core managed, the network fails, or Core
    /// returns a non-2xx — callers treat that as "skip", never as a fatal error.
    /// </summary>
    private async Task<HttpResponseMessage?> TrySendAsync(
        HttpMethod method, string path, object? payload, string action, CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            return null; // Standalone local run: no service token, nothing to call.
        }

        var client = httpClientFactory.CreateClient(CoreIdentityValidator.HttpClientName);
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ServiceToken);
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        // Treat any failure as "skip" (network error, HttpClient timeout — which surfaces as a
        // TaskCanceledException — etc.) so a Core hiccup never crashes the caller (e.g. startup migration).
        // Genuine caller-requested cancellation still propagates.
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Core call to {Action} failed.", action);
            return null;
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        logger.LogWarning("Core call to {Action} returned {StatusCode}.", action, (int)response.StatusCode);
        response.Dispose();
        return null;
    }

    private sealed record BackupResponse(string? Status, string? BackupId);

    private sealed record DirectoryResponse(IReadOnlyList<CoreDirectoryUser> Users);
}
