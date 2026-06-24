using System.Net;
using System.Text;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Metadata;

public sealed class TmdbSearchTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    private static TmdbMetadataProvider Provider(HttpMessageHandler handler) => new(
        new SingleClientFactory(handler),
        new MediaServerSettings { TmdbApiKey = Key },
        NullLogger<TmdbMetadataProvider>.Instance);

    [Fact]
    public async Task SearchAsync_maps_poster_path_to_a_thumbnail_url()
    {
        const string body = """
            { "results": [
                { "id": 218, "title": "The Terminator", "release_date": "1984-10-26", "poster_path": "/abc.jpg" },
                { "id": 296, "title": "Terminator 2", "release_date": "1991-07-03", "poster_path": null }
            ] }
            """;

        var candidates = await Provider(new StubHandler(body)).SearchAsync(
            new MediaQuery(MediaKind.Movie, "Terminator", null), CancellationToken.None);

        var withPoster = candidates.Single(candidate => candidate.Reference.Id == "218");
        Assert.Equal("https://image.tmdb.org/t/p/w154/abc.jpg", withPoster.PosterUrl);

        // A null poster_path stays null rather than producing a broken base-only URL.
        var withoutPoster = candidates.Single(candidate => candidate.Reference.Id == "296");
        Assert.Null(withoutPoster.PosterUrl);
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://api.themoviedb.org/"),
        };
    }
}
