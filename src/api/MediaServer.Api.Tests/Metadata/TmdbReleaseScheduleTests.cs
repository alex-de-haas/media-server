using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Metadata;

/// <summary>
/// TMDb→app release-type bucketing and per-region parsing for release tracking: Theatrical merges
/// limited (2) + wide (3) to the earliest, Premiere (1) stays separate, Digital (4) maps, and
/// Physical (5) / TV (6) are dropped. Regions are the watch-region axis (independent of
/// SUPPORTED_LANGUAGES); series carry the title-level next/last episode plus opt-in season enumeration.
/// </summary>
public sealed class TmdbReleaseScheduleTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    private static TmdbReleaseScheduleProvider Provider(CannedHandler handler) => new(
        new SingleClientFactory(handler),
        new MediaServerSettings { TmdbApiKey = Key },
        NullLogger<TmdbReleaseScheduleProvider>.Instance);

    private const string MovieJson = """
        {
          "id": 27205, "title": "Inception", "status": "Released",
          "release_date": "2010-07-15", "poster_path": "/poster.jpg",
          "release_dates": { "results": [
            {
              "iso_3166_1": "US",
              "release_dates": [
                { "type": 1, "release_date": "2010-07-08T00:00:00.000Z", "note": "World premiere" },
                { "type": 2, "release_date": "2010-07-13T00:00:00.000Z", "note": "IMAX" },
                { "type": 3, "release_date": "2010-07-16T00:00:00.000Z" },
                { "type": 4, "release_date": "2010-11-15T00:00:00.000Z" },
                { "type": 5, "release_date": "2010-12-07T00:00:00.000Z" },
                { "type": 6, "release_date": "2012-01-01T00:00:00.000Z" }
              ]
            },
            {
              "iso_3166_1": "RU",
              "release_dates": [
                { "type": 3, "release_date": "2010-07-22T00:00:00.000Z" }
              ]
            }
          ] }
        }
        """;

    private const string SeriesJson = """
        {
          "id": 1396, "name": "Breaking Bad", "status": "Returning Series",
          "first_air_date": "2008-01-20", "poster_path": "/bb.jpg",
          "seasons": [
            { "season_number": 0 }, { "season_number": 1 }, { "season_number": 2 }
          ],
          "next_episode_to_air": { "season_number": 2, "episode_number": 5, "air_date": "2009-04-05", "name": "Breakage" },
          "last_episode_to_air": { "season_number": 2, "episode_number": 4, "air_date": "2009-03-29", "name": "Down" }
        }
        """;

    private const string SeasonJson = """
        {
          "episodes": [
            { "season_number": 2, "episode_number": 1, "air_date": "2009-03-08", "name": "Seven Thirty-Seven" },
            { "season_number": 2, "episode_number": 2, "air_date": "2009-03-15", "name": "Grilled" },
            { "season_number": 2, "episode_number": 3, "name": "Undated" }
          ]
        }
        """;

    [Fact]
    public async Task Movie_theatrical_merges_limited_and_wide_to_the_earliest()
    {
        var schedule = await Provider(new CannedHandler(MovieJson)).GetMovieScheduleAsync("27205", ["US"], CancellationToken.None);

        var theatrical = Assert.Single(schedule!.Dates, date => date.Type == ReleaseType.Theatrical);
        Assert.Equal(new DateOnly(2010, 7, 13), theatrical.Date); // limited (2) is earlier than wide (3)
        Assert.Equal(2, theatrical.RawType); // the winning raw code is kept
        Assert.Equal("IMAX", theatrical.Note);
    }

    [Fact]
    public async Task Movie_premiere_stays_separate_and_digital_maps()
    {
        var schedule = await Provider(new CannedHandler(MovieJson)).GetMovieScheduleAsync("27205", ["US"], CancellationToken.None);

        var premiere = Assert.Single(schedule!.Dates, date => date.Type == ReleaseType.Premiere);
        Assert.Equal(new DateOnly(2010, 7, 8), premiere.Date);

        var digital = Assert.Single(schedule.Dates, date => date.Type == ReleaseType.Digital);
        Assert.Equal(new DateOnly(2010, 11, 15), digital.Date);
    }

    [Fact]
    public async Task Movie_physical_and_tv_are_dropped()
    {
        var schedule = await Provider(new CannedHandler(MovieJson)).GetMovieScheduleAsync("27205", ["US"], CancellationToken.None);

        Assert.Equal(3, schedule!.Dates.Count); // Premiere + Theatrical + Digital, never 5/6
        Assert.DoesNotContain(schedule.Dates, date => date.RawType is 5 or 6);
    }

    [Fact]
    public async Task Movie_parses_only_the_requested_regions()
    {
        var us = await Provider(new CannedHandler(MovieJson)).GetMovieScheduleAsync("27205", ["US"], CancellationToken.None);
        Assert.All(us!.Dates, date => Assert.Equal("US", date.Region));

        // A second watch region (e.g. a per-entry RegionOverride) adds that region's rows.
        var both = await Provider(new CannedHandler(MovieJson)).GetMovieScheduleAsync("27205", ["US", "RU"], CancellationToken.None);
        var ru = Assert.Single(both!.Dates, date => date.Region == "RU");
        Assert.Equal(ReleaseType.Theatrical, ru.Type);
        Assert.Equal(new DateOnly(2010, 7, 22), ru.Date);

        // A region TMDb has no dates for simply yields nothing (never another region's dates).
        var unlisted = await Provider(new CannedHandler(MovieJson)).GetMovieScheduleAsync("27205", ["DE"], CancellationToken.None);
        Assert.Empty(unlisted!.Dates);
    }

    [Fact]
    public async Task Movie_maps_the_display_snapshot()
    {
        var schedule = await Provider(new CannedHandler(MovieJson)).GetMovieScheduleAsync("27205", ["US"], CancellationToken.None);

        Assert.Equal("Inception", schedule!.Title);
        Assert.Equal(2010, schedule.Year);
        Assert.Equal("Released", schedule.Status);
        Assert.Equal("https://image.tmdb.org/t/p/w154/poster.jpg", schedule.PosterUrl);
    }

    [Fact]
    public async Task Movie_requests_release_dates_in_one_call()
    {
        var handler = new CannedHandler(MovieJson);
        await Provider(handler).GetMovieScheduleAsync("27205", ["US"], CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Contains("movie/27205", request);
        Assert.Contains("append_to_response=release_dates", request);
    }

    [Fact]
    public async Task Series_maps_title_level_next_and_last_episode_and_seasons()
    {
        var schedule = await Provider(new CannedHandler(SeriesJson)).GetSeriesScheduleAsync("1396", CancellationToken.None);

        Assert.Equal("Breaking Bad", schedule!.Title);
        Assert.Equal("Returning Series", schedule.Status);
        Assert.Equal([0, 1, 2], schedule.Seasons);

        Assert.Equal(2, schedule.NextEpisode!.Season);
        Assert.Equal(5, schedule.NextEpisode.Episode);
        Assert.Equal(new DateOnly(2009, 4, 5), schedule.NextEpisode.AirDate);
        Assert.Equal("Breakage", schedule.NextEpisode.Name);
        Assert.Equal(4, schedule.LastEpisode!.Episode);
    }

    [Fact]
    public async Task Season_enumeration_skips_undated_episodes()
    {
        var handler = new CannedHandler(SeasonJson);
        var episodes = await Provider(handler).GetSeasonEpisodesAsync("1396", 2, CancellationToken.None);

        Assert.Contains("tv/1396/season/2", Assert.Single(handler.Requests));
        Assert.Equal(2, episodes.Count); // the undated episode is not a release event yet
        Assert.Equal(1, episodes[0].Episode);
        Assert.Equal(new DateOnly(2009, 3, 15), episodes[1].AirDate);
    }

    [Fact]
    public async Task Unknown_title_returns_null()
    {
        var handler = new CannedHandler(json: null); // 404
        Assert.Null(await Provider(handler).GetMovieScheduleAsync("1", ["US"], CancellationToken.None));
        Assert.Null(await Provider(handler).GetSeriesScheduleAsync("1", CancellationToken.None));
        Assert.Empty(await Provider(handler).GetSeasonEpisodesAsync("1", 1, CancellationToken.None));
    }

    private sealed class CannedHandler(string? json) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(json is null
                ? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK)
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
