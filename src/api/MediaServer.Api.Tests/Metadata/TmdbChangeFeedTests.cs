using System.Net;
using System.Text;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Metadata;

/// <summary>
/// Reading TMDb's change lists. The one thing this must never do is report a short answer as a complete
/// one: the caller advances its sync marker on the strength of it, and would step over whatever went
/// unread for good.
/// </summary>
public sealed class TmdbChangeFeedTests
{
    private static readonly DateTimeOffset Noon = DateTimeOffset.Parse("2026-08-31T12:00:00Z");

    [Fact]
    public async Task It_walks_the_window_a_day_at_a_time()
    {
        // TMDb takes dates, and its paging grows with the span asked for. A day is the unit it answers
        // in, so a fortnight's catch-up is fourteen bounded queries rather than one enormous one.
        var handler = new StubHandler();
        handler.Answer("2026-08-29", [1]);
        handler.Answer("2026-08-30", [2]);
        handler.Answer("2026-08-31", [3, 1]);

        var changed = await Feed(handler).GetChangedAsync(
            MediaKind.Movie, Noon.AddDays(-2), Noon, CancellationToken.None);

        Assert.Equal(["1", "2", "3"], changed!.OrderBy(id => id));
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, path => Assert.Contains("movie/changes", path));
    }

    [Fact]
    public async Task It_reads_every_page_of_a_day()
    {
        var handler = new StubHandler();
        handler.Answer("2026-08-31", [1, 2], page: 1, totalPages: 2);
        handler.Answer("2026-08-31", [3], page: 2, totalPages: 2);

        var changed = await Feed(handler).GetChangedAsync(MediaKind.Movie, Noon, Noon, CancellationToken.None);

        Assert.Equal(["1", "2", "3"], changed!.OrderBy(id => id));
    }

    [Fact]
    public async Task A_failed_request_is_no_answer_rather_than_a_short_one()
    {
        var handler = new StubHandler();
        handler.Answer("2026-08-30", [1]);
        handler.Fail("2026-08-31");

        var changed = await Feed(handler).GetChangedAsync(
            MediaKind.Movie, Noon.AddDays(-1), Noon, CancellationToken.None);

        // Not `["1"]`: the caller would advance its marker past the day that was never read.
        Assert.Null(changed);
    }

    [Fact]
    public async Task A_series_query_asks_the_tv_list()
    {
        var handler = new StubHandler();
        handler.Answer("2026-08-31", [1399]);

        await Feed(handler).GetChangedAsync(MediaKind.Series, Noon, Noon, CancellationToken.None);

        Assert.Contains("tv/changes", handler.Requests.Single());
    }

    [Fact]
    public async Task A_kind_the_provider_has_no_list_for_asks_nothing()
    {
        var handler = new StubHandler();

        var changed = await Feed(handler).GetChangedAsync(MediaKind.Episode, Noon, Noon, CancellationToken.None);

        Assert.Empty(changed!);
        Assert.Empty(handler.Requests);
    }

    private static TmdbChangeFeed Feed(StubHandler handler) => new(
        new StubFactory(handler),
        new MediaServerSettings { TmdbApiKey = "v3-key" },
        NullLogger<TmdbChangeFeed>.Instance);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _bodies = new(StringComparer.Ordinal);
        private readonly HashSet<string> _failures = [];

        public List<string> Requests { get; } = [];

        public void Answer(string date, int[] ids, int page = 1, int totalPages = 1) =>
            _bodies[$"{date}:{page}"] =
                $$"""
                  {"page":{{page}},"total_pages":{{totalPages}},"total_results":{{ids.Length}},
                   "results":[{{string.Join(",", ids.Select(id => $"{{\"id\":{id},\"adult\":false}}"))}}]}
                  """;

        public void Fail(string date) => _failures.Add(date);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri!.PathAndQuery;
            Requests.Add(query);

            var date = Between(query, "start_date=", "&");
            var page = Between(query, "page=", "&");

            if (_failures.Contains(date))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }

            var body = _bodies.GetValueOrDefault($"{date}:{page}")
                ?? """{"page":1,"total_pages":1,"total_results":0,"results":[]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }

        private static string Between(string value, string after, string before)
        {
            var start = value.IndexOf(after, StringComparison.Ordinal);
            if (start < 0)
            {
                return string.Empty;
            }

            start += after.Length;
            var end = value.IndexOf(before, start, StringComparison.Ordinal);
            return end < 0 ? value[start..] : value[start..end];
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler) { BaseAddress = new Uri("https://api.themoviedb.org/") };
    }
}
