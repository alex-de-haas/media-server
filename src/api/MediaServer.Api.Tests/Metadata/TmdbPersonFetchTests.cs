using System.Net;
using System.Text;
using MediaServer.Api.Configuration;
using MediaServer.Api.Metadata;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Metadata;

/// <summary>
/// The person-detail fetch (<see cref="TmdbMetadataProvider.FetchPersonAsync"/>) hits <c>/person/{id}</c>,
/// maps biography/profile/birth fields out of the payload, sends the api_key as a query credential, and
/// degrades to null on a provider error rather than throwing.
/// </summary>
public sealed class TmdbPersonFetchTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    private static TmdbMetadataProvider Provider(HttpMessageHandler handler) => new(
        new SingleClientFactory(handler),
        new MediaServerSettings { TmdbApiKey = Key },
        NullLogger<TmdbMetadataProvider>.Instance);

    [Fact]
    public async Task FetchPersonAsync_maps_biography_profile_and_birth_fields()
    {
        const string body = """
            {
              "id": 6193,
              "name": "Leonardo DiCaprio",
              "biography": "An American actor and film producer.",
              "profile_path": "/leo.jpg",
              "known_for_department": "Acting",
              "birthday": "1974-11-11",
              "deathday": null,
              "place_of_birth": "Los Angeles, California, USA"
            }
            """;

        var details = await Provider(new StubHandler(body)).FetchPersonAsync(
            new ProviderRef("tmdb", "6193"), "en-US", CancellationToken.None);

        Assert.NotNull(details);
        Assert.Equal("Leonardo DiCaprio", details!.Name);
        Assert.Equal("An American actor and film producer.", details.Biography);
        Assert.Equal("/leo.jpg", details.ProfilePath); // raw provider path; the caller derives the absolute url
        Assert.Equal("Acting", details.KnownForDepartment);
        Assert.Equal("1974-11-11", details.Birthday);
        Assert.Null(details.Deathday);                  // explicit null in the payload stays null
        Assert.Equal("Los Angeles, California, USA", details.PlaceOfBirth);
    }

    [Fact]
    public async Task FetchPersonAsync_treats_empty_strings_as_null()
    {
        const string body = """
            { "id": 1, "name": "No Bio", "biography": "", "profile_path": "", "place_of_birth": "" }
            """;

        var details = await Provider(new StubHandler(body)).FetchPersonAsync(
            new ProviderRef("tmdb", "1"), "en-US", CancellationToken.None);

        Assert.NotNull(details);
        Assert.Equal("No Bio", details!.Name);
        Assert.Null(details.Biography);
        Assert.Null(details.ProfilePath);
        Assert.Null(details.PlaceOfBirth);
    }

    [Fact]
    public async Task FetchPersonAsync_requests_the_person_endpoint_with_the_language_and_api_key()
    {
        var handler = new RecordingHandler();

        await Provider(handler).FetchPersonAsync(new ProviderRef("tmdb", "6193"), "en-US", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Contains("/person/6193", request);
        Assert.Contains("language=en-US", request);
        Assert.Contains($"api_key={Key}", request); // a v3 key rides as a query param (and is never logged)
    }

    [Fact]
    public async Task FetchPersonAsync_returns_null_on_a_provider_error()
    {
        var details = await Provider(new ErrorHandler(HttpStatusCode.NotFound)).FetchPersonAsync(
            new ProviderRef("tmdb", "999999"), "en-US", CancellationToken.None);

        Assert.Null(details);
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ErrorHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://api.themoviedb.org/"),
        };
    }
}
