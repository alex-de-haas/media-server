using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using MediaServer.Api.Watchlist;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.Watchlist;

/// <summary>
/// "When does it come out" for a title nobody is tracking.
/// </summary>
/// <remarks>
/// The calendar answers for tracked titles because that is where the dates are stored, but the question
/// is usually asked about something not on the list — and answering it is what prompts someone to add
/// it. Nothing here persists: tracking creates a row, a question does not.
/// </remarks>
public sealed class ReleasePreviewTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly FakeScheduleProvider _provider = new();
    private readonly WatchlistService _service;

    public ReleasePreviewTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(
            new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();
        _service = new WatchlistService(
            _database,
            new MediaServerSettings { WatchRegion = "US" },
            new RecordingSyncQueue(),
            new WatchlistLibraryLinker(_database, TimeProvider.System),
            TimeProvider.System,
            _provider);
    }

    [Fact]
    public async Task A_movie_answers_with_its_dates_and_tracks_nothing()
    {
        _provider.Movies["27205"] = new MovieReleaseSchedule(
            "Inception", 2010, null, "Released",
            [new TypedReleaseDate("US", ReleaseType.Theatrical, 3, new DateOnly(2010, 7, 16), null)]);

        var preview = await _service.PreviewScheduleAsync("tmdb", "27205", MediaKind.Movie, default);

        Assert.NotNull(preview);
        Assert.Equal("Inception", preview.Title);
        Assert.Equal(new DateOnly(2010, 7, 16), Assert.Single(preview.Dates).Date);

        // The point of a preview: asking must not put the title on anyone's list.
        Assert.Empty(await _database.WatchlistEntries.ToListAsync());
        Assert.Empty(await _database.TrackedTitles.ToListAsync());
    }

    [Fact]
    public async Task A_series_answers_with_the_next_episode()
    {
        // "When is the next episode" is the series form of the same question, and it is a different
        // field — a series has no release date of its own to report.
        _provider.Series["1396"] = new SeriesReleaseSchedule(
            "Breaking Bad", 2008, null, "Ended",
            NextEpisode: new EpisodeAirDate(6, 1, new DateOnly(2027, 3, 1), "Return"),
            LastEpisode: new EpisodeAirDate(5, 16, new DateOnly(2013, 9, 29), "Felina"),
            Seasons: [1, 2, 3, 4, 5]);

        var preview = await _service.PreviewScheduleAsync("tmdb", "1396", MediaKind.Series, default);

        Assert.NotNull(preview);
        Assert.Equal(new DateOnly(2027, 3, 1), preview.NextEpisode?.AirDate);
        Assert.Empty(preview.Dates);
    }

    [Fact]
    public async Task A_title_the_provider_will_not_answer_for_is_refused_rather_than_reported_as_undated()
    {
        // An empty schedule and no schedule are different answers. Reporting "no dates" for a title the
        // provider never answered about would tell the operator the film has no release date, which is a
        // statement about the film rather than about the request.
        Assert.Null(await _service.PreviewScheduleAsync("tmdb", "404", MediaKind.Movie, default));

        // Beside a title that genuinely has none, which is an answer and comes back as one.
        _provider.Movies["7"] = new MovieReleaseSchedule("Undated", null, null, "Planned", []);
        var undated = await _service.PreviewScheduleAsync("tmdb", "7", MediaKind.Movie, default);
        Assert.NotNull(undated);
        Assert.Empty(undated.Dates);
    }

    [Fact]
    public async Task Only_the_provider_this_instance_uses_is_asked()
    {
        // A provider key this server does not speak must not silently be treated as the one it does, or
        // an id from somewhere else would be looked up against TMDb's id space and answer about a
        // different film entirely.
        _provider.Movies["27205"] = new MovieReleaseSchedule("Inception", 2010, null, "Released", []);

        Assert.Null(await _service.PreviewScheduleAsync("imdb", "27205", MediaKind.Movie, default));
        Assert.Empty(_provider.Calls);
    }

    [Fact]
    public async Task An_episode_has_no_schedule_of_its_own_to_ask_about()
    {
        Assert.Null(await _service.PreviewScheduleAsync("tmdb", "1396", MediaKind.Episode, default));
        Assert.Empty(_provider.Calls);
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }
}
