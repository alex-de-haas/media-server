using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Metadata;

/// <summary>
/// Tier 1 detail enrichment: the single detail call carries append_to_response (no extra round-trips),
/// and certification is mapped into <c>OfficialRating</c> from release_dates (movies) / content_ratings
/// (tv), keyed by the region implied by the requested language with a US fallback.
/// </summary>
public sealed class TmdbDetailMappingTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    private static TmdbMetadataProvider Provider(CannedHandler handler) => new(
        new SingleClientFactory(handler),
        new MediaServerSettings { TmdbApiKey = Key },
        NullLogger<TmdbMetadataProvider>.Instance);

    [Fact]
    public async Task FetchAsync_appends_credits_external_ids_videos_release_dates_and_keywords_for_a_movie()
    {
        var handler = new CannedHandler();

        await Provider(handler).FetchAsync(new ProviderRef("tmdb", "27205"), MediaKind.Movie, ["en-US"], CancellationToken.None);

        Assert.Contains(handler.Requests, uri =>
            uri.Contains("append_to_response=") &&
            uri.Contains("credits") && uri.Contains("external_ids") && uri.Contains("videos") &&
            uri.Contains("release_dates") && uri.Contains("keywords"));
    }

    [Fact]
    public async Task FetchAsync_appends_content_ratings_for_a_series()
    {
        var handler = new CannedHandler();

        await Provider(handler).FetchAsync(new ProviderRef("tmdb", "1396"), MediaKind.Series, ["en-US"], CancellationToken.None);

        Assert.Contains(handler.Requests, uri => uri.Contains("append_to_response=") && uri.Contains("content_ratings"));
    }

    [Fact]
    public async Task FetchAsync_maps_the_region_certification_to_official_rating()
    {
        var handler = new CannedHandler();

        var records = await Provider(handler).FetchAsync(new ProviderRef("tmdb", "27205"), MediaKind.Movie, ["en-US"], CancellationToken.None);

        Assert.Equal("PG-13", Assert.Single(records).OfficialRating); // US (from en-US), not the GB "12A"
    }

    [Fact]
    public async Task FetchAsync_falls_back_to_us_certification_for_an_unlisted_region()
    {
        var handler = new CannedHandler();

        var records = await Provider(handler).FetchAsync(new ProviderRef("tmdb", "27205"), MediaKind.Movie, ["ru-RU"], CancellationToken.None);

        Assert.Equal("PG-13", Assert.Single(records).OfficialRating); // no RU entry → fall back to US
    }

    [Fact]
    public async Task FetchAsync_maps_a_series_content_rating_to_official_rating()
    {
        var handler = new CannedHandler();

        var records = await Provider(handler).FetchAsync(new ProviderRef("tmdb", "1396"), MediaKind.Series, ["en-US"], CancellationToken.None);

        Assert.Equal("TV-MA", Assert.Single(records).OfficialRating);
    }

    private sealed class CannedHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            Requests.Add(uri);

            var json = uri.Contains("/tv/")
                ? """
                  {
                    "id": 1396, "name": "Breaking Bad", "vote_average": 8.9,
                    "content_ratings": { "results": [ { "iso_3166_1": "US", "rating": "TV-MA" } ] }
                  }
                  """
                : """
                  {
                    "id": 27205, "title": "Inception", "vote_average": 8.4,
                    "release_dates": { "results": [
                      { "iso_3166_1": "GB", "release_dates": [ { "certification": "12A" } ] },
                      { "iso_3166_1": "US", "release_dates": [ { "certification": "PG-13" } ] }
                    ] }
                  }
                  """;

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
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
