using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace MediaServer.Api.Hosty;

/// <summary>
/// Revalidates a forwarded app identity token against Hosty Core:
/// <c>POST {HOSTY_CORE_ORIGIN}/api/auth/apps/revalidate</c> with the app service token as bearer.
/// Core identity JWTs are HS256 (symmetric), so the app cannot verify them locally — this
/// round-trip is the only trustworthy validation.
/// </summary>
public sealed class CoreIdentityValidator(
    IHttpClientFactory httpClientFactory,
    HostyOptions options,
    ILogger<CoreIdentityValidator> logger)
    : IHostyIdentityValidator
{
    public const string HttpClientName = "hosty-core";

    public async Task<HostySession?> ValidateAsync(string accessToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ServiceToken))
        {
            logger.LogWarning("HOSTY_APP_SERVICE_TOKEN is not set; cannot revalidate identity against Core.");
            return null;
        }

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/apps/revalidate")
        {
            Content = JsonContent.Create(new RevalidateRequest(accessToken)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ServiceToken);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Identity revalidation request to Core failed.");
            return null;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<RevalidateResponse>(cancellationToken);
            if (payload is null || !payload.Active)
            {
                return null;
            }

            // Defence in depth: the token's audience must be this app, even though Core also
            // enforces it via the calling service token.
            if (!string.Equals(payload.AppId, options.AppId, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "Revalidated token audience {Audience} does not match app {AppId}.",
                    payload.AppId,
                    options.AppId);
                return null;
            }

            return new HostySession(
                payload.AppId,
                payload.UserId,
                payload.Email,
                payload.DisplayName,
                payload.HostRole ?? string.Empty,
                payload.ExpiresAt);
        }
    }

    private sealed record RevalidateRequest(string AccessToken);

    private sealed record RevalidateResponse(
        bool Active,
        string AppId,
        string UserId,
        string? Email,
        string? DisplayName,
        string? HostRole,
        DateTimeOffset ExpiresAt);
}
