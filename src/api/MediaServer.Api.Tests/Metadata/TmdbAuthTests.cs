using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Metadata;

/// <summary>
/// TMDb accepts a v3 API key (api_key query) or a v4 API Read Access Token (Bearer header). Operators
/// paste whichever they have, so the provider must pick the right mechanism — sending a v4 JWT as the
/// api_key query is rejected with 401 (the bug that returned empty search results).
/// </summary>
public sealed class TmdbAuthTests
{
    private const string V4Token = "eyJhbGciOiJIUzI1NiJ9.payload.signature";
    private const string V3Key = "0123456789abcdef0123456789abcdef";

    private static TmdbMetadataProvider Provider(string key, RecordingHandler handler) => new(
        new SingleClientFactory(handler),
        new MediaServerSettings { TmdbApiKey = key },
        NullLogger<TmdbMetadataProvider>.Instance);

    [Fact]
    public async Task A_v4_token_is_sent_as_a_bearer_header_not_an_api_key_query()
    {
        var handler = new RecordingHandler();
        await Provider(V4Token, handler).SearchAsync(new MediaQuery(MediaKind.Movie, "Project Hail Mary", 2026), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal($"Bearer {V4Token}", request.Authorization);
        Assert.DoesNotContain("api_key=", request.Uri);
    }

    [Fact]
    public async Task A_v3_key_is_sent_as_the_api_key_query_not_a_bearer_header()
    {
        var handler = new RecordingHandler();
        await Provider(V3Key, handler).SearchAsync(new MediaQuery(MediaKind.Movie, "Project Hail Mary", 2026), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Null(request.Authorization);
        Assert.Contains($"api_key={V3Key}", request.Uri);
    }

    private sealed record Captured(string Uri, string? Authorization);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Captured> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Captured(request.RequestUri!.ToString(), request.Headers.Authorization?.ToString()));
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"results\":[]}", System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://api.themoviedb.org/"),
        };
    }
}
