using System.Net;
using System.Text;
using MediaServer.Api.Configuration;
using MediaServer.Api.Tests.Jellyfin;
using MediaServer.Api.WatchHistory;
using MediaServer.Api.WatchHistory.Trakt;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.WatchHistory;

/// <summary>
/// Trakt's per-work paths take a Trakt id, slug, or IMDb id — never a TMDb id, which they answer with
/// an empty array rather than an error. That silence made every ownership read-back come back empty,
/// so no remote id was ever captured and an unwatch had nothing it was allowed to remove.
/// </summary>
public sealed class TraktWorkIdResolverTests : IDisposable
{
    private readonly TestTimeProvider _time = new(DateTimeOffset.Parse("2026-07-25T12:00:00Z"));
    private readonly StubHandler _handler = new();

    private sealed class StubHandler : HttpMessageHandler
    {
        public Queue<(HttpStatusCode Status, string Body)> Responses { get; } = new();

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            var (status, body) = Responses.Count > 0 ? Responses.Dequeue() : (HttpStatusCode.OK, "[]");
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.trakt.tv/") };
    }

    [Fact]
    public async Task AnImdbIdIsUsedDirectlyAndCostsNoLookup()
    {
        // Trakt accepts an IMDb id on these paths, so spending a search request would be waste.
        var resolved = await Resolver().ResolveAsync(
            new WatchHistoryIdentity { Kind = WatchHistoryMediaKind.Movie, TmdbId = 687163, ImdbId = "tt12042730" },
            "token",
            CancellationToken.None);

        Assert.Equal("tt12042730", resolved);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task ATmdbOnlyIdentityIsTranslatedThroughSearch()
    {
        _handler.Responses.Enqueue((HttpStatusCode.OK, """[ { "movie": { "ids": { "trakt": 531178 } } } ]"""));

        var resolved = await Resolver().ResolveAsync(Movie(687163), "token", CancellationToken.None);

        Assert.Equal("531178", resolved);
        Assert.Contains("search/tmdb/687163?type=movie", Assert.Single(_handler.Requests), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEpisodeIdentityResolvesThroughItsShow()
    {
        // Episodes are addressed by their show on the history paths, so the search type must match.
        _handler.Responses.Enqueue((HttpStatusCode.OK, """[ { "show": { "ids": { "trakt": 1425 } } } ]"""));

        var resolved = await Resolver().ResolveAsync(
            new WatchHistoryIdentity
            {
                Kind = WatchHistoryMediaKind.Episode, TmdbId = 1434, SeasonNumber = 1, EpisodeNumber = 2,
            },
            "token",
            CancellationToken.None);

        Assert.Equal("1425", resolved);
        Assert.Contains("type=show", Assert.Single(_handler.Requests), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheMappingIsResolvedOnceAndReused()
    {
        // The mapping between two catalogues' ids for one work never changes, and the read path runs
        // per item — re-resolving would double every sync's request count.
        _handler.Responses.Enqueue((HttpStatusCode.OK, """[ { "movie": { "ids": { "trakt": 531178 } } } ]"""));
        var resolver = Resolver();

        await resolver.ResolveAsync(Movie(687163), "token", CancellationToken.None);
        await resolver.ResolveAsync(Movie(687163), "token", CancellationToken.None);

        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task ATitleTraktDoesNotKnowIsRememberedAsUnknown()
    {
        _handler.Responses.Enqueue((HttpStatusCode.OK, "[]"));
        var resolver = Resolver();

        Assert.Null(await resolver.ResolveAsync(Movie(1), "token", CancellationToken.None));
        Assert.Null(await resolver.ResolveAsync(Movie(1), "token", CancellationToken.None));

        // A real negative, so asking again is pointless.
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task AnOutageIsNotRememberedAsUnknown()
    {
        // Caching a transient failure would blank this title for the whole process lifetime.
        _handler.Responses.Enqueue((HttpStatusCode.ServiceUnavailable, ""));
        _handler.Responses.Enqueue((HttpStatusCode.OK, """[ { "movie": { "ids": { "trakt": 531178 } } } ]"""));
        var resolver = Resolver();

        Assert.Null(await resolver.ResolveAsync(Movie(687163), "token", CancellationToken.None));
        Assert.Equal("531178", await resolver.ResolveAsync(Movie(687163), "token", CancellationToken.None));
    }

    [Fact]
    public async Task AnIdentityWithNeitherIdResolvesToNothingWithoutAsking()
    {
        var resolved = await Resolver().ResolveAsync(
            new WatchHistoryIdentity { Kind = WatchHistoryMediaKind.Movie }, "token", CancellationToken.None);

        Assert.Null(resolved);
        Assert.Empty(_handler.Requests);
    }

    private static WatchHistoryIdentity Movie(int tmdbId) =>
        new() { Kind = WatchHistoryMediaKind.Movie, TmdbId = tmdbId };

    private TraktWorkIdResolver Resolver()
    {
        var settings = new MediaServerSettings { TraktClientId = "cid", TraktClientSecret = "secret" };
        var oauth = new TraktOAuthClient(
            new StubFactory(_handler), settings, _time, NullLogger<TraktOAuthClient>.Instance);
        return new TraktWorkIdResolver(oauth, new TraktWorkIdCache(), NullLogger<TraktWorkIdResolver>.Instance);
    }

    public void Dispose() => _handler.Dispose();
}
