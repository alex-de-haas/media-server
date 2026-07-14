using System.Net.Http.Headers;
using System.Text.Json;
using MediaServer.Api.Configuration;

namespace MediaServer.Api.Metadata;

/// <summary>
/// Shared TMDb GET plumbing for every TMDb-backed provider: v3-key-vs-v4-token credential detection,
/// 401 diagnostics that never log the credential, and non-2xx → null. Extracted from
/// <see cref="TmdbMetadataProvider"/> so the release-schedule provider reuses the exact same behavior.
/// </summary>
internal static class TmdbRequest
{
    public static async Task<JsonDocument?> GetAsync(
        IHttpClientFactory httpClientFactory,
        MediaServerSettings settings,
        ILogger logger,
        string pathWithQuery,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.TmdbApiKey))
        {
            throw new InvalidOperationException("TMDB_API_KEY is not configured.");
        }

        var key = settings.TmdbApiKey;
        // TMDb accepts either a v3 API key (sent as the api_key query parameter) or a v4 API Read Access
        // Token — a JWT — sent as a Bearer header. Detect which the operator configured so pasting either
        // works; a v4 token sent as api_key would be rejected with 401 (and vice-versa).
        var useBearer = key.Contains('.');

        var client = httpClientFactory.CreateClient(TmdbMetadataProvider.HttpClientName);
        var requestUri = useBearer
            ? $"3/{pathWithQuery}"
            : $"3/{pathWithQuery}{(pathWithQuery.Contains('?') ? '&' : '?')}api_key={key}";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        if (useBearer)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // 401 almost always means a mis-configured credential — surface that at warning level so it
            // is diagnosable, while never logging the api_key query value or the token itself.
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                logger.LogWarning(
                    "TMDb rejected the configured TMDB_API_KEY (HTTP 401). Provide a valid v3 API key or v4 API Read Access Token.");
            }
            else
            {
                logger.LogDebug("TMDb request {Path} returned {StatusCode}.", pathWithQuery, (int)response.StatusCode);
            }

            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }
}
