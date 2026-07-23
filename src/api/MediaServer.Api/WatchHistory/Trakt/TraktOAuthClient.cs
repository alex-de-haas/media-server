using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaServer.Api.Configuration;

namespace MediaServer.Api.WatchHistory.Trakt;

/// <summary>Credentials for one connected account, as stored in the credential store.</summary>
/// <param name="AccessToken">Bearer token for API calls.</param>
/// <param name="RefreshToken">Exchanged for a new pair before the access token expires.</param>
/// <param name="ExpiresAt">When <paramref name="AccessToken"/> stops working.</param>
public sealed record TraktCredentials(
    [property: JsonPropertyName("accessToken")] string AccessToken,
    [property: JsonPropertyName("refreshToken")] string RefreshToken,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);

/// <summary>What Trakt returns when a device authorization begins.</summary>
internal sealed record TraktDeviceCode(string DeviceCode, string UserCode, string VerificationUrl, int ExpiresInSeconds, int IntervalSeconds);

/// <summary>The identity of the account that just authorized.</summary>
internal sealed record TraktAccount(string Id, string Username);

/// <summary>One round of the device-token poll: where the attempt stands, and the tokens on approval.</summary>
internal sealed record TraktDevicePoll(
    WatchHistoryAuthorizationState State, TraktCredentials? Credentials, TimeSpan? RetryAfter);

/// <summary>
/// Trakt's OAuth device flow and token lifecycle. Everything Trakt-shaped about authentication lives
/// here: endpoint paths, the <c>trakt-api-version</c>/<c>trakt-api-key</c> headers, and the status
/// codes the device flow overloads to mean "keep waiting", "slow down", "denied" and "expired".
/// </summary>
public sealed class TraktOAuthClient(
    IHttpClientFactory httpClientFactory,
    MediaServerSettings settings,
    TimeProvider time,
    ILogger<TraktOAuthClient> logger)
{
    public const string HttpClientName = "trakt";

    /// <summary>Refresh this long before expiry, so an in-flight delivery is not cut off mid-request.</summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(10);

    internal async Task<WatchHistoryResult<TraktDeviceCode>> StartDeviceAuthorizationAsync(CancellationToken cancellationToken)
    {
        if (!settings.IsTraktConfigured)
        {
            return WatchHistoryResult<TraktDeviceCode>.Failed(
                WatchHistoryFailure.Unsupported, "Trakt is not configured for this instance.");
        }

        using var content = JsonContent.Create(new { client_id = settings.TraktClientId });
        var response = await SendAsync(HttpMethod.Post, "oauth/device/code", content, accessToken: null, cancellationToken);
        if (!response.Succeeded)
        {
            return WatchHistoryResult<TraktDeviceCode>.Failed(response.Failure!.Value, response.Detail, response.RetryAfter);
        }

        using var document = response.Value!;
        var root = document.RootElement;
        var deviceCode = ReadString(root, "device_code");
        var userCode = ReadString(root, "user_code");
        var verificationUrl = ReadString(root, "verification_url");
        if (deviceCode is null || userCode is null || verificationUrl is null)
        {
            return WatchHistoryResult<TraktDeviceCode>.Failed(
                WatchHistoryFailure.ContractViolation, "Trakt returned a device code without the fields needed to display it.");
        }

        return WatchHistoryResult<TraktDeviceCode>.Success(new TraktDeviceCode(
            deviceCode,
            userCode,
            verificationUrl,
            ReadInt(root, "expires_in") ?? 600,
            // Polling faster than Trakt asks earns a 429; default conservatively when it says nothing.
            ReadInt(root, "interval") ?? 5));
    }

    /// <summary>
    /// Exchanges a device code for credentials. The device flow overloads HTTP status codes rather
    /// than returning an error body, so each one is mapped to a state the caller can act on. A
    /// rejection of this instance's application credentials is a failure, not a state: no amount of
    /// waiting fixes a bad client id or secret, so reporting it as Pending would spin forever.
    /// </summary>
    internal async Task<WatchHistoryResult<TraktDevicePoll>> PollDeviceTokenAsync(
        string deviceCode, CancellationToken cancellationToken)
    {
        using var content = JsonContent.Create(new
        {
            code = deviceCode,
            client_id = settings.TraktClientId,
            client_secret = settings.TraktClientSecret,
        });

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "oauth/device/token") { Content = content };
        AddApiHeaders(request, accessToken: null);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // A transport failure is not a decision by the user; keep the attempt pending.
            logger.LogDebug("Polling the Trakt device token failed transiently.");
            return Poll(WatchHistoryAuthorizationState.Pending);
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    var credentials = await ReadCredentialsAsync(response, cancellationToken);
                    return credentials is null
                        ? Poll(WatchHistoryAuthorizationState.Pending)
                        : WatchHistoryResult<TraktDevicePoll>.Success(
                            new TraktDevicePoll(WatchHistoryAuthorizationState.Approved, credentials, null));

                // Trakt's documented device-flow vocabulary.
                case HttpStatusCode.BadRequest:
                    return Poll(WatchHistoryAuthorizationState.Pending);
                case HttpStatusCode.NotFound:
                    return Poll(WatchHistoryAuthorizationState.Denied);
                case HttpStatusCode.Conflict:
                    // Already used: someone completed this code. Treat as approved-elsewhere, which is
                    // terminal for this attempt.
                    return Poll(WatchHistoryAuthorizationState.Denied);
                case HttpStatusCode.Gone:
                    return Poll(WatchHistoryAuthorizationState.Expired);
                case (HttpStatusCode)418:
                    return Poll(WatchHistoryAuthorizationState.Denied);
                case HttpStatusCode.TooManyRequests:
                    return WatchHistoryResult<TraktDevicePoll>.Success(new TraktDevicePoll(
                        WatchHistoryAuthorizationState.SlowDown, null, RetryAfterOf(response)));

                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                    // Trakt rejected this instance's application credentials, not the user's approval.
                    // Waiting cannot fix a bad client id or secret, so this must surface as an error
                    // rather than be filed under "keep polling".
                    return WatchHistoryResult<TraktDevicePoll>.Failed(
                        WatchHistoryFailure.Unsupported,
                        $"Trakt rejected this instance's application credentials (HTTP {(int)response.StatusCode}). "
                        + "Check the Trakt client ID and client secret in the app settings.");

                default:
                    logger.LogDebug("Trakt device token poll returned {StatusCode}.", (int)response.StatusCode);
                    return Poll(WatchHistoryAuthorizationState.Pending);
            }
        }

        static WatchHistoryResult<TraktDevicePoll> Poll(WatchHistoryAuthorizationState state) =>
            WatchHistoryResult<TraktDevicePoll>.Success(new TraktDevicePoll(state, null, null));
    }

    /// <summary>
    /// Returns credentials good for the next call, refreshing when they are close to expiry. Trakt
    /// rotates the refresh token on every exchange, so the caller must persist what comes back.
    /// </summary>
    internal async Task<WatchHistoryResult<TraktCredentials>> EnsureFreshAsync(
        TraktCredentials credentials, CancellationToken cancellationToken)
    {
        if (credentials.ExpiresAt - time.GetUtcNow() > RefreshMargin)
        {
            return WatchHistoryResult<TraktCredentials>.Success(credentials);
        }

        using var content = JsonContent.Create(new
        {
            refresh_token = credentials.RefreshToken,
            client_id = settings.TraktClientId,
            client_secret = settings.TraktClientSecret,
            redirect_uri = "urn:ietf:wg:oauth:2.0:oob",
            grant_type = "refresh_token",
        });

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "oauth/token") { Content = content };
        AddApiHeaders(request, accessToken: null);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return WatchHistoryResult<TraktCredentials>.Failed(
                WatchHistoryFailure.Transient, "The Trakt token refresh could not be reached.");
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest or HttpStatusCode.Forbidden)
            {
                // The refresh token is spent or revoked; no amount of retrying brings it back.
                return WatchHistoryResult<TraktCredentials>.Failed(
                    WatchHistoryFailure.AuthenticationRequired, "Trakt rejected the stored refresh token.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return WatchHistoryResult<TraktCredentials>.Failed(
                    WatchHistoryFailure.Transient, $"Trakt token refresh returned HTTP {(int)response.StatusCode}.", RetryAfterOf(response));
            }

            var refreshed = await ReadCredentialsAsync(response, cancellationToken);
            return refreshed is null
                ? WatchHistoryResult<TraktCredentials>.Failed(WatchHistoryFailure.ContractViolation, "Trakt returned an unusable token response.")
                : WatchHistoryResult<TraktCredentials>.Success(refreshed);
        }
    }

    internal async Task<WatchHistoryResult<TraktAccount>> GetAccountAsync(string accessToken, CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Get, "users/settings", content: null, accessToken, cancellationToken);
        if (!response.Succeeded)
        {
            return WatchHistoryResult<TraktAccount>.Failed(response.Failure!.Value, response.Detail, response.RetryAfter);
        }

        using var document = response.Value!;
        if (!document.RootElement.TryGetProperty("user", out var user))
        {
            return WatchHistoryResult<TraktAccount>.Failed(WatchHistoryFailure.ContractViolation, "Trakt returned no user in its settings response.");
        }

        var username = ReadString(user, "username") ?? ReadString(user, "name");
        var id = user.TryGetProperty("ids", out var ids) ? ReadString(ids, "slug") : null;
        return username is null
            ? WatchHistoryResult<TraktAccount>.Failed(WatchHistoryFailure.ContractViolation, "Trakt returned a user without a username.")
            : WatchHistoryResult<TraktAccount>.Success(new TraktAccount(id ?? username, username));
    }

    /// <summary>Best-effort revocation. A failure here must not stop the caller deleting locally.</summary>
    internal async Task RevokeAsync(string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            using var content = JsonContent.Create(new
            {
                token = accessToken,
                client_id = settings.TraktClientId,
                client_secret = settings.TraktClientSecret,
            });
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, "oauth/revoke") { Content = content };
            AddApiHeaders(request, accessToken: null);
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("Trakt token revocation returned {StatusCode}.", (int)response.StatusCode);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Leaving a token alive at Trakt is better than refusing to disconnect: the user asked to
            // be disconnected, and they can revoke it from Trakt's own settings.
            logger.LogDebug(exception, "Trakt token revocation failed; disconnecting locally regardless.");
        }
    }

    /// <summary>Shared request path: headers, transport-failure classification, and status mapping.</summary>
    internal async Task<WatchHistoryResult<JsonDocument>> SendAsync(
        HttpMethod method, string path, HttpContent? content, string? accessToken, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(method, path) { Content = content };
        AddApiHeaders(request, accessToken);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return WatchHistoryResult<JsonDocument>.Failed(WatchHistoryFailure.Transient, "Trakt could not be reached.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WatchHistoryResult<JsonDocument>.Failed(WatchHistoryFailure.Transient, "The Trakt request timed out.");
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return WatchHistoryResult<JsonDocument>.Failed(
                    WatchHistoryFailure.AuthenticationRequired, $"Trakt rejected the stored credentials (HTTP {(int)response.StatusCode}).");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return WatchHistoryResult<JsonDocument>.Failed(
                    WatchHistoryFailure.RateLimited, "Trakt rate limit reached.", RetryAfterOf(response));
            }

            if (!response.IsSuccessStatusCode)
            {
                var failure = (int)response.StatusCode >= 500 ? WatchHistoryFailure.Transient : WatchHistoryFailure.ContractViolation;
                return WatchHistoryResult<JsonDocument>.Failed(failure, $"Trakt returned HTTP {(int)response.StatusCode}.");
            }

            // 204 and an empty body are legitimate for the mutation endpoints. Decide that from the
            // status and content length rather than the stream: HTTP content streams are usually not
            // seekable, so probing Length would misread a real 204 and report a contract violation.
            if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
            {
                return WatchHistoryResult<JsonDocument>.Success(JsonDocument.Parse("null"));
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                return WatchHistoryResult<JsonDocument>.Success(await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken));
            }
            catch (JsonException)
            {
                // Also covers a body-less 200 with no content-length header: nothing to parse.
                return WatchHistoryResult<JsonDocument>.Failed(
                    WatchHistoryFailure.ContractViolation, "Trakt returned a body that could not be parsed.");
            }
        }
    }

    private void AddApiHeaders(HttpRequestMessage request, string? accessToken)
    {
        // Trakt sits behind Cloudflare, which answers 403 to any request without a User-Agent — and
        // HttpClient sends none by default. Verified live: the same key and headers pass with any UA
        // and fail without one. Set per-request, beside the other Trakt headers, so no future call
        // path can miss it.
        request.Headers.UserAgent.ParseAdd("HaasMediaServer/1.0 (+https://github.com/alex-de-haas/media-server)");
        request.Headers.Add("trakt-api-version", "2");
        if (!string.IsNullOrWhiteSpace(settings.TraktClientId))
        {
            request.Headers.Add("trakt-api-key", settings.TraktClientId);
        }

        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
    }

    private async Task<TraktCredentials?> ReadCredentialsAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var access = ReadString(root, "access_token");
            var refresh = ReadString(root, "refresh_token");
            if (access is null || refresh is null)
            {
                return null;
            }

            // Trust the returned lifetime, but treat a missing or nonsensical one as "refresh soon"
            // rather than "never expires" — the latter would leave a dead token in place forever.
            var expiresIn = ReadInt(root, "expires_in") is { } seconds and > 0 ? seconds : 3600;
            return new TraktCredentials(access, refresh, time.GetUtcNow().AddSeconds(expiresIn));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Delta
        ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed)
            ? parsed
            : null;
}
