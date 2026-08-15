using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Metadata;

/// <summary>
/// TMDb uses separate id spaces for movies and tv, so a single numeric id can resolve to unrelated
/// titles in each (the real collision: tv 95480 = "Slow Horses", movie 95480 = "Flesh, TX"). Enrich
/// must pick the endpoint from the matched item's kind, never by probing — probing movie-first would
/// fetch the wrong title and poster for any tv id that also exists as a movie.
/// </summary>
public sealed class TmdbEndpointSelectionTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";
    private const string CollidingId = "95480";

    private static TmdbMetadataProvider Provider(RecordingHandler handler) => new(
        new SingleClientFactory(handler),
        new MediaServerSettings { TmdbApiKey = Key },
        NullLogger<TmdbMetadataProvider>.Instance);

    [Fact]
    public async Task FetchAsync_for_a_series_hits_the_tv_endpoint_and_never_movie()
    {
        var handler = new RecordingHandler();

        await Provider(handler).FetchAsync(
            new ProviderRef("tmdb", CollidingId), MediaKind.Series, ["en-US"], CancellationToken.None);

        Assert.Contains(handler.Requests, uri => uri.Contains($"/tv/{CollidingId}"));
        Assert.DoesNotContain(handler.Requests, uri => uri.Contains($"/movie/{CollidingId}"));
    }

    [Fact]
    public async Task GetImagesAsync_for_a_series_hits_the_tv_images_endpoint_and_never_movie()
    {
        var handler = new RecordingHandler();

        await Provider(handler).GetImagesAsync(
            new ProviderRef("tmdb", CollidingId), MediaKind.Series, ["en-US"], CancellationToken.None);

        Assert.Contains(handler.Requests, uri => uri.Contains($"/tv/{CollidingId}/images"));
        Assert.DoesNotContain(handler.Requests, uri => uri.Contains($"/movie/{CollidingId}"));
    }

    [Fact]
    public async Task GetImagesAsync_asks_for_english_artwork_even_when_it_is_not_a_configured_language()
    {
        var handler = new RecordingHandler();
        var provider = new TmdbMetadataProvider(
            new SingleClientFactory(handler),
            new MediaServerSettings { TmdbApiKey = Key, SupportedLanguages = ["ru-RU"] },
            NullLogger<TmdbMetadataProvider>.Instance);

        await provider.GetImagesAsync(
            new ProviderRef("tmdb", CollidingId), MediaKind.Movie, ["ru-RU"], CancellationToken.None);

        // Without English there is nothing between the Russian art and the textless art for the display
        // ranking to fall back to, and a poster with no title on it is the failure being fixed.
        var request = Assert.Single(handler.Requests, uri => uri.Contains("/images"));
        Assert.Contains("include_image_language=ru,en,null", request);
    }

    [Fact]
    public async Task GetImagesAsync_does_not_ask_for_english_twice()
    {
        var handler = new RecordingHandler();

        await Provider(handler).GetImagesAsync(
            new ProviderRef("tmdb", CollidingId), MediaKind.Movie, ["en-US", "en-GB"], CancellationToken.None);

        var request = Assert.Single(handler.Requests, uri => uri.Contains("/images"));
        Assert.Contains("include_image_language=en,null", request);
    }

    [Fact]
    public async Task FetchAsync_for_a_movie_hits_the_movie_endpoint_and_never_tv()
    {
        var handler = new RecordingHandler();

        await Provider(handler).FetchAsync(
            new ProviderRef("tmdb", CollidingId), MediaKind.Movie, ["en-US"], CancellationToken.None);

        Assert.Contains(handler.Requests, uri => uri.Contains($"/movie/{CollidingId}"));
        Assert.DoesNotContain(handler.Requests, uri => uri.Contains($"/tv/{CollidingId}"));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
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
