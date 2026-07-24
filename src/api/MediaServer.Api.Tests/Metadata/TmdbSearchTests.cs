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

    private static TmdbMetadataProvider Provider(HttpMessageHandler handler, IReadOnlyList<string>? languages = null) => new(
        new SingleClientFactory(handler),
        new MediaServerSettings { TmdbApiKey = Key, SupportedLanguages = languages ?? ["en-US"] },
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

    [Fact]
    public async Task SearchAsync_requests_localized_titles_for_a_cyrillic_query_and_scores_them()
    {
        const string body = """
            { "results": [
                { "id": 105, "title": "Назад в будущее", "original_title": "Back to the Future", "release_date": "1985-07-03" },
                { "id": 1, "title": "Оглядываясь в будущее", "original_title": "Looking Back to the Future", "release_date": "1985-01-01" }
            ] }
            """;
        var handler = new StubHandler(body);

        var candidates = await Provider(handler, ["en-US", "ru-RU"]).SearchAsync(
            new MediaQuery(MediaKind.Movie, "Назад в будущее", 1985), CancellationToken.None);

        // The Cyrillic query rides with the configured Cyrillic-script language, so the response titles
        // come back in the query's script and the re-score can see the match.
        Assert.Contains("language=ru-RU", handler.LastRequestUri!.Query);
        Assert.Equal("105", candidates.First().Reference.Id);
        Assert.True(candidates.First().Score >= TitleScoring.AutoMatchThreshold);
    }

    [Fact]
    public async Task SearchAsync_stays_languageless_for_latin_queries()
    {
        var handler = new StubHandler("""{ "results": [] }""");

        await Provider(handler, ["en-US", "ru-RU"]).SearchAsync(
            new MediaQuery(MediaKind.Movie, "Back to the Future", 1985), CancellationToken.None);

        Assert.DoesNotContain("language", handler.LastRequestUri!.Query);
    }

    [Fact]
    public async Task SearchAsync_stays_languageless_when_no_configured_language_matches_the_script()
    {
        var handler = new StubHandler("""{ "results": [] }""");

        await Provider(handler, ["en-US"]).SearchAsync(
            new MediaQuery(MediaKind.Movie, "Назад в будущее", null), CancellationToken.None);

        Assert.DoesNotContain("language", handler.LastRequestUri!.Query);
    }

    [Fact]
    public async Task SearchAsync_scores_on_the_original_title_when_it_matches_better()
    {
        // A film searched by its original (here Russian) name while the display title is the English one:
        // the score takes the best of the two, so the exact original-title match wins.
        const string body = """
            { "results": [
                { "id": 25237, "title": "Come and See", "original_title": "Иди и смотри", "release_date": "1985-10-17" }
            ] }
            """;
        var handler = new StubHandler(body);

        var candidates = await Provider(handler).SearchAsync(
            new MediaQuery(MediaKind.Movie, "Иди и смотри", 1985), CancellationToken.None);

        Assert.True(candidates.Single().Score >= TitleScoring.AutoMatchThreshold);
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
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
