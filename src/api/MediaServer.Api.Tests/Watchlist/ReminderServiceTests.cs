using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Watchlist;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.Watchlist;

/// <summary>
/// Reminder creation and its resolved-state response: kind↔type validation, remind-implies-track (entry
/// created, episode tracking auto-enabled with the FutureEpisodes default), the three states
/// (scheduled / alreadyReleased / pending), series resolution from the title-level last/next episode, the
/// one-reminder-per-(user, title, type) upsert, and per-user scoping.
/// </summary>
public sealed class ReminderServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 7, 14);

    private readonly SqliteConnection _connection = WatchlistTestData.OpenDatabase();
    private readonly FixedTimeProvider _clock = new(Now);
    private readonly RecordingSyncQueue _queue = new();

    public ReminderServiceTests()
    {
        using var database = WatchlistTestData.NewContext(_connection);
        database.Database.Migrate();
    }

    [Fact]
    public async Task Create_rejects_invalid_kind_type_combinations()
    {
        int userId;
        Guid movieId;
        Guid seriesId;
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var user = WatchlistTestData.SeedUser(database);
            userId = user.Id;
            movieId = WatchlistTestData.SeedTitle(database, MediaKind.Movie, "27205", "Inception").Id;
            seriesId = WatchlistTestData.SeedTitle(database, MediaKind.Series, "1396", "Slow Horses").Id;
        }

        // A movie has no episode airings; a series has only episode airings.
        var movieEpisode = await CreateAsync(userId, new CreateReminderRequest(movieId, null, null, ReleaseType.EpisodeAir, 0, null, null, null, null));
        Assert.NotNull(movieEpisode.Error);

        var seriesDigital = await CreateAsync(userId, new CreateReminderRequest(seriesId, null, null, ReleaseType.Digital, 0, null, null, null, null));
        Assert.NotNull(seriesDigital.Error);

        // Movie types are accepted.
        var movieDigital = await CreateAsync(userId, new CreateReminderRequest(movieId, null, null, ReleaseType.Digital, 0, null, null, null, null));
        Assert.Null(movieDigital.Error);
    }

    [Fact]
    public async Task Remind_implies_track_and_auto_enables_episode_tracking()
    {
        int userId;
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            userId = WatchlistTestData.SeedUser(database).Id;
        }

        // No entry, not even a TrackedTitle: created from a provider ref in one action.
        var result = await CreateAsync(userId, new CreateReminderRequest(
            null, new ProviderRefBody("tmdb", "1396"), MediaKind.Series, ReleaseType.EpisodeAir, 1, "20:00", "Slow Horses", 2022, null));

        Assert.Null(result.Error);
        Assert.True(result.Created);
        Assert.Equal(ReminderResolutionDto.Pending, result.Resolution!.State); // nothing synced yet

        using var fresh = WatchlistTestData.NewContext(_connection);
        var entry = await fresh.WatchlistEntries.SingleAsync();
        Assert.Equal(userId, entry.AppUserId);
        Assert.Equal(SeriesMonitorScope.FutureEpisodes, entry.MonitorScope); // needs EpisodeAir rows to fire
        Assert.Single(_queue.Enqueued); // and the sync is queued so a pending reminder can bind
    }

    [Fact]
    public async Task Create_resolves_scheduled_already_released_and_pending_for_movies()
    {
        int userId;
        Guid scheduledId;
        Guid releasedId;
        Guid pendingId;
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var user = WatchlistTestData.SeedUser(database);
            userId = user.Id;

            var scheduled = WatchlistTestData.SeedTitle(database, MediaKind.Movie, "1", "Future Movie");
            WatchlistTestData.SeedRelease(database, scheduled, ReleaseType.Digital, Today.AddDays(31), region: "US");
            scheduledId = scheduled.Id;

            var released = WatchlistTestData.SeedTitle(database, MediaKind.Movie, "2", "Old Movie");
            WatchlistTestData.SeedRelease(database, released, ReleaseType.Digital, Today.AddDays(-100), region: "US");
            releasedId = released.Id;

            pendingId = WatchlistTestData.SeedTitle(database, MediaKind.Movie, "3", "Trailer Movie").Id;
        }

        var scheduledResult = await CreateAsync(userId, new CreateReminderRequest(scheduledId, null, null, ReleaseType.Digital, 3, null, null, null, null));
        Assert.Equal(ReminderResolutionDto.Scheduled, scheduledResult.Resolution!.State);
        Assert.Equal(Today.AddDays(31), scheduledResult.Resolution.Date);

        var releasedResult = await CreateAsync(userId, new CreateReminderRequest(releasedId, null, null, ReleaseType.Digital, 0, null, null, null, null));
        Assert.Equal(ReminderResolutionDto.AlreadyReleased, releasedResult.Resolution!.State);
        Assert.Equal(Today.AddDays(-100), releasedResult.Resolution.Date);

        // The trailer case: no date of that type yet — pending, binds when the sync stores one.
        var pendingResult = await CreateAsync(userId, new CreateReminderRequest(pendingId, null, null, ReleaseType.Theatrical, 3, null, null, null, null));
        Assert.Equal(ReminderResolutionDto.Pending, pendingResult.Resolution!.State);
        Assert.Null(pendingResult.Resolution.Date);
    }

    [Fact]
    public async Task Create_resolves_series_state_from_the_title_level_snapshot()
    {
        int userId;
        Guid airingId;
        Guid betweenSeasonsId;
        Guid endedId;
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var user = WatchlistTestData.SeedUser(database);
            userId = user.Id;

            var airing = WatchlistTestData.SeedTitle(database, MediaKind.Series, "1", "Airing Show", status: "Returning Series");
            airing.LastAiredSeason = 2;
            airing.LastAiredEpisode = 10;
            airing.LastAiredDate = Today.AddDays(-4);
            airing.NextAirSeason = 2;
            airing.NextAirEpisode = 11;
            airing.NextAirDate = Today.AddDays(3);
            airingId = airing.Id;

            var betweenSeasons = WatchlistTestData.SeedTitle(database, MediaKind.Series, "2", "Between Seasons", status: "Returning Series");
            betweenSeasons.LastAiredSeason = 3;
            betweenSeasons.LastAiredEpisode = 8;
            betweenSeasons.LastAiredDate = Today.AddDays(-200);
            betweenSeasonsId = betweenSeasons.Id;

            var ended = WatchlistTestData.SeedTitle(database, MediaKind.Series, "3", "Ended Show", status: "Ended");
            ended.LastAiredSeason = 5;
            ended.LastAiredEpisode = 16;
            ended.LastAiredDate = Today.AddDays(-800);
            endedId = ended.Id;
            database.SaveChanges();
        }

        // Currently airing: scheduled on the next episode, with the already-airing summary.
        var airingResult = await CreateAsync(userId, new CreateReminderRequest(airingId, null, null, ReleaseType.EpisodeAir, 0, null, null, null, null));
        Assert.Equal(ReminderResolutionDto.Scheduled, airingResult.Resolution!.State);
        Assert.Equal(Today.AddDays(3), airingResult.Resolution.Date);
        Assert.Contains("up to S2E10", airingResult.Resolution.Detail);

        // Between seasons: pending (waiting for the next date), still summarized — not delivered retroactively.
        var betweenResult = await CreateAsync(userId, new CreateReminderRequest(betweenSeasonsId, null, null, ReleaseType.EpisodeAir, 0, null, null, null, null));
        Assert.Equal(ReminderResolutionDto.Pending, betweenResult.Resolution!.State);
        Assert.Contains("up to S3E8", betweenResult.Resolution.Detail);

        // Over: already released as of the finale — resolved from the snapshot, not prunable episode rows.
        var endedResult = await CreateAsync(userId, new CreateReminderRequest(endedId, null, null, ReleaseType.EpisodeAir, 0, null, null, null, null));
        Assert.Equal(ReminderResolutionDto.AlreadyReleased, endedResult.Resolution!.State);
        Assert.Equal(Today.AddDays(-800), endedResult.Resolution.Date);
    }

    [Fact]
    public async Task Create_upserts_the_single_reminder_per_title_and_type()
    {
        int userId;
        Guid titleId;
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var user = WatchlistTestData.SeedUser(database);
            userId = user.Id;
            titleId = WatchlistTestData.SeedTitle(database, MediaKind.Movie, "27205", "Inception").Id;
        }

        var first = await CreateAsync(userId, new CreateReminderRequest(titleId, null, null, ReleaseType.Digital, 0, "09:00", null, null, null));
        Assert.True(first.Created);

        // Every entry point converges on the same (title, type) reminder: lead/time update in place.
        var second = await CreateAsync(userId, new CreateReminderRequest(titleId, null, null, ReleaseType.Digital, 7, "20:30", null, null, null));
        Assert.False(second.Created);
        Assert.Equal(7, second.Resolution!.Reminder.LeadDays);
        Assert.Equal("20:30", second.Resolution.Reminder.NotifyAt);

        using var fresh = WatchlistTestData.NewContext(_connection);
        Assert.Equal(1, await fresh.ReleaseReminders.CountAsync());
    }

    [Fact]
    public async Task Update_and_delete_are_scoped_to_the_owning_user()
    {
        int aliceId;
        int bobId;
        Guid reminderId;
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var alice = WatchlistTestData.SeedUser(database, "alice");
            var bob = WatchlistTestData.SeedUser(database, "bob");
            aliceId = alice.Id;
            bobId = bob.Id;
            var title = WatchlistTestData.SeedTitle(database, MediaKind.Movie, "27205", "Inception");
            WatchlistTestData.SeedEntry(database, alice, title);
            reminderId = WatchlistTestData.SeedReminder(database, alice, title, ReleaseType.Digital).Id;
        }

        Assert.Null(await ExecuteAsync(service => service.UpdateAsync(
            bobId, reminderId, new UpdateReminderRequest(5, null, null), CancellationToken.None)));
        Assert.False(await ExecuteAsync(service => service.DeleteAsync(bobId, reminderId, CancellationToken.None)));

        var updated = await ExecuteAsync(service => service.UpdateAsync(
            aliceId, reminderId, new UpdateReminderRequest(5, "18:00", false), CancellationToken.None));
        Assert.Equal(5, updated!.LeadDays);
        Assert.Equal("18:00", updated.NotifyAt);
        Assert.False(updated.Active);

        Assert.True(await ExecuteAsync(service => service.DeleteAsync(aliceId, reminderId, CancellationToken.None)));
        using var fresh = WatchlistTestData.NewContext(_connection);
        Assert.Empty(fresh.ReleaseReminders);
    }

    private Task<ReminderCreateResult> CreateAsync(int userId, CreateReminderRequest request) =>
        ExecuteAsync(service => service.CreateAsync(userId, request, CancellationToken.None));

    private async Task<T> ExecuteAsync<T>(Func<ReminderService, Task<T>> action)
    {
        using var database = WatchlistTestData.NewContext(_connection);
        var settings = new MediaServerSettings { WatchRegion = "US" };
        var watchlist = new WatchlistService(database, settings, _queue, _clock);
        var service = new ReminderService(database, settings, watchlist, _queue, _clock);
        return await action(service);
    }

    public void Dispose() => _connection.Dispose();
}
