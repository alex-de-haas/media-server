using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Jobs;
using MediaServer.Api.Metadata;
using MediaServer.Api.Realtime;
using MediaServer.Api.Watchlist;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Api.Tests.Watchlist;

/// <summary>
/// The 24h date-sync loop: per-region movie upserts, PreviousDate on moved dates, settled-title skip,
/// series title-level next/last episode with tracking off (no season fetch), opt-in season enumeration
/// within the −7…+90d horizon, and pruning that spares rows backing unsent reminders.
/// </summary>
public sealed class WatchlistSyncServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 7, 14);

    private readonly SqliteConnection _connection = WatchlistTestData.OpenDatabase();
    private readonly ServiceProvider _provider;
    private readonly FakeScheduleProvider _schedule = new();
    private readonly FixedTimeProvider _clock = new(Now);

    public WatchlistSyncServiceTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<MediaServerDbContext>(options => options.UseSqlite(_connection));
        services.AddSingleton(new MediaServerSettings { WatchRegion = "US" });
        services.AddSingleton<IReleaseScheduleProvider>(_schedule);
        services.AddSingleton<IRealtimeNotifier, NullNotifier>();
        services.AddSingleton<TimeProvider>(_clock);
        services.AddScoped<JobService>();
        services.AddScoped<WatchlistLibraryLinker>();
        services.AddScoped<WatchlistSyncService>();
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<MediaServerDbContext>().Database.Migrate();
    }

    [Fact]
    public async Task Movie_sync_upserts_typed_rows_for_the_watch_region_and_snapshot()
    {
        Guid titleId;
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var user = WatchlistTestData.SeedUser(database);
            var title = WatchlistTestData.SeedTitle(database, MediaKind.Movie, "27205", title: "");
            WatchlistTestData.SeedEntry(database, user, title);
            titleId = title.Id;
        }

        _schedule.Movies["27205"] = new MovieReleaseSchedule("Inception", 2010, "poster", "Released",
        [
            new TypedReleaseDate("US", ReleaseType.Theatrical, 3, new DateOnly(2026, 8, 1), null),
            new TypedReleaseDate("US", ReleaseType.Digital, 4, new DateOnly(2026, 11, 15), null),
        ]);

        Assert.Equal(WatchlistSyncService.SyncOutcome.Synced, await SyncAsync(titleId));

        using var fresh = WatchlistTestData.NewContext(_connection);
        var stored = await fresh.TrackedTitles.Include(candidate => candidate.Releases).SingleAsync();
        Assert.Equal("Inception", stored.Title);
        Assert.Equal(2010, stored.Year);
        Assert.Equal("Released", stored.ProductionStatus);
        Assert.NotNull(stored.LastRefreshedAt);
        Assert.Equal(2, stored.Releases.Count);
        Assert.All(stored.Releases, release => Assert.Equal("US", release.Region));
        Assert.Equal(new DateOnly(2026, 11, 15), stored.Releases.Single(release => release.Type == ReleaseType.Digital).Date);

        // The provider was asked for exactly the effective watch region.
        Assert.Equal(["US"], Assert.Single(_schedule.MovieRegionRequests));
    }

    [Fact]
    public async Task Movie_sync_fetches_the_union_of_watch_region_and_entry_overrides()
    {
        Guid titleId;
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var alice = WatchlistTestData.SeedUser(database, "alice");
            var bob = WatchlistTestData.SeedUser(database, "bob");
            var title = WatchlistTestData.SeedTitle(database, MediaKind.Movie, "27205");
            WatchlistTestData.SeedEntry(database, alice, title);
            WatchlistTestData.SeedEntry(database, bob, title, regionOverride: "ru");
            titleId = title.Id;
        }

        _schedule.Movies["27205"] = new MovieReleaseSchedule("Inception", 2010, null, "Released",
        [
            new TypedReleaseDate("US", ReleaseType.Digital, 4, new DateOnly(2026, 11, 15), null),
            new TypedReleaseDate("RU", ReleaseType.Digital, 4, new DateOnly(2026, 12, 1), null),
        ]);

        await SyncAsync(titleId);

        var regions = Assert.Single(_schedule.MovieRegionRequests);
        Assert.Equal(["RU", "US"], regions.OrderBy(region => region)); // the override is normalized to upper-case

        using var fresh = WatchlistTestData.NewContext(_connection);
        Assert.Equal(2, await fresh.TrackedReleases.CountAsync());
    }

    [Fact]
    public async Task Moved_date_records_previous_date()
    {
        Guid titleId;
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var title = WatchlistTestData.SeedTitle(database, MediaKind.Movie, "27205");
            WatchlistTestData.SeedRelease(database, title, ReleaseType.Digital, new DateOnly(2026, 8, 14), region: "US", rawType: 4);
            titleId = title.Id;
        }

        _schedule.Movies["27205"] = new MovieReleaseSchedule("Inception", 2010, null, "Released",
            [new TypedReleaseDate("US", ReleaseType.Digital, 4, new DateOnly(2026, 9, 1), null)]);

        await SyncAsync(titleId);

        using var fresh = WatchlistTestData.NewContext(_connection);
        var release = await fresh.TrackedReleases.SingleAsync();
        Assert.Equal(new DateOnly(2026, 9, 1), release.Date);
        Assert.Equal(new DateOnly(2026, 8, 14), release.PreviousDate);
    }

    [Fact]
    public async Task Settled_movie_is_skipped_unless_forced()
    {
        Guid titleId;
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            // Released, digital date known and past → nothing left to learn on a periodic pass.
            var title = WatchlistTestData.SeedTitle(database, MediaKind.Movie, "27205", status: "Released");
            WatchlistTestData.SeedRelease(database, title, ReleaseType.Digital, Today.AddDays(-30), region: "US", rawType: 4);
            titleId = title.Id;
        }

        _schedule.Movies["27205"] = new MovieReleaseSchedule("Inception", 2010, null, "Released", []);

        Assert.Equal(WatchlistSyncService.SyncOutcome.Skipped, await SyncAsync(titleId));
        Assert.Empty(_schedule.Calls); // the provider was never hit

        Assert.Equal(WatchlistSyncService.SyncOutcome.Synced, await SyncAsync(titleId, force: true));
        Assert.Single(_schedule.Calls);
    }

    [Fact]
    public async Task Released_movie_without_a_digital_date_keeps_syncing()
    {
        Guid titleId;
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            // In cinemas already, but the digital date isn't announced — a periodic pass must keep looking.
            var title = WatchlistTestData.SeedTitle(database, MediaKind.Movie, "27205", status: "Released");
            WatchlistTestData.SeedRelease(database, title, ReleaseType.Theatrical, Today.AddDays(-30), region: "US", rawType: 3);
            titleId = title.Id;
        }

        _schedule.Movies["27205"] = new MovieReleaseSchedule("Inception", 2010, null, "Released",
        [
            new TypedReleaseDate("US", ReleaseType.Theatrical, 3, Today.AddDays(-30), null),
            new TypedReleaseDate("US", ReleaseType.Digital, 4, Today.AddDays(20), null),
        ]);

        Assert.Equal(WatchlistSyncService.SyncOutcome.Synced, await SyncAsync(titleId));

        using var fresh = WatchlistTestData.NewContext(_connection);
        Assert.NotNull(await fresh.TrackedReleases.SingleOrDefaultAsync(release => release.Type == ReleaseType.Digital));
    }

    [Fact]
    public async Task Series_title_level_sync_stores_next_episode_without_a_season_fetch()
    {
        Guid titleId;
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var user = WatchlistTestData.SeedUser(database);
            var title = WatchlistTestData.SeedTitle(database, MediaKind.Series, "1396");
            WatchlistTestData.SeedEntry(database, user, title); // MonitorScope null: episode tracking off
            titleId = title.Id;
        }

        _schedule.Series["1396"] = new SeriesReleaseSchedule("Breaking Bad", 2008, null, "Returning Series",
            new EpisodeAirDate(3, 1, Today.AddDays(30), "Premiere"),
            new EpisodeAirDate(2, 13, Today.AddDays(-3), "Finale"),
            [1, 2, 3]);

        await SyncAsync(titleId);

        Assert.Equal(["series:1396"], _schedule.Calls); // no /season/{n} calls with tracking off

        using var fresh = WatchlistTestData.NewContext(_connection);
        var stored = await fresh.TrackedTitles.Include(candidate => candidate.Releases).SingleAsync();
        Assert.Equal(2, stored.Releases.Count); // next + last episode, both within horizon
        Assert.All(stored.Releases, release => Assert.Equal(ReleaseType.EpisodeAir, release.Type));
        Assert.Contains(stored.Releases, release => release is { Season: 3, Episode: 1 });
        // Title-level last-aired snapshot backs the series already-airing resolution.
        Assert.Equal(2, stored.LastAiredSeason);
        Assert.Equal(13, stored.LastAiredEpisode);
        Assert.Equal(Today.AddDays(-3), stored.LastAiredDate);
    }

    [Fact]
    public async Task Series_opt_in_enumeration_fetches_monitored_seasons_within_horizon()
    {
        Guid titleId;
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var user = WatchlistTestData.SeedUser(database);
            var title = WatchlistTestData.SeedTitle(database, MediaKind.Series, "1396");
            WatchlistTestData.SeedEntry(database, user, title, scope: SeriesMonitorScope.Seasons, monitoredSeasons: [2]);
            titleId = title.Id;
        }

        _schedule.Series["1396"] = new SeriesReleaseSchedule("Breaking Bad", 2008, null, "Returning Series",
            NextEpisode: null, LastEpisode: null, Seasons: [1, 2]);
        _schedule.Seasons[("1396", 2)] =
        [
            new EpisodeAirDate(2, 1, Today.AddDays(-30), "Too old"), // outside −7d
            new EpisodeAirDate(2, 2, Today.AddDays(-2), "Recent"),
            new EpisodeAirDate(2, 3, Today.AddDays(5), "Upcoming"),
            new EpisodeAirDate(2, 4, Today.AddDays(120), "Beyond horizon"), // outside +90d
        ];

        await SyncAsync(titleId);

        Assert.Equal(["series:1396", "season:1396:2"], _schedule.Calls); // only the monitored season

        using var fresh = WatchlistTestData.NewContext(_connection);
        var episodes = await fresh.TrackedReleases.OrderBy(release => release.Episode).ToListAsync();
        Assert.Equal([2, 3], episodes.Select(release => release.Episode!.Value)); // horizon-filtered
    }

    [Fact]
    public async Task Horizon_pruning_spares_rows_backing_unsent_reminders()
    {
        Guid titleId;
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var user = WatchlistTestData.SeedUser(database);
            var title = WatchlistTestData.SeedTitle(database, MediaKind.Series, "1396");
            WatchlistTestData.SeedEntry(
                database, user, title, scope: SeriesMonitorScope.WholeShow, createdAt: Now.AddDays(-60));
            var reminder = WatchlistTestData.SeedReminder(
                database, user, title, ReleaseType.EpisodeAir, createdAt: Now.AddDays(-60));

            // Both aged out of the −7d horizon; e1 was delivered, e2 was not.
            var delivered = WatchlistTestData.SeedRelease(database, title, ReleaseType.EpisodeAir, Today.AddDays(-20), season: 1, episode: 1);
            WatchlistTestData.SeedRelease(database, title, ReleaseType.EpisodeAir, Today.AddDays(-15), season: 1, episode: 2);
            database.ReminderDeliveries.Add(new ReminderDelivery
            {
                Id = Guid.NewGuid(),
                ReminderId = reminder.Id,
                TrackedReleaseId = delivered.Id,
                SentAt = Now.AddDays(-20),
            });
            database.SaveChanges();
            titleId = title.Id;
        }

        _schedule.Series["1396"] = new SeriesReleaseSchedule("Breaking Bad", 2008, null, "Returning Series",
            NextEpisode: null, LastEpisode: null, Seasons: [1]);
        _schedule.Seasons[("1396", 1)] = []; // the provider returns nothing inside the horizon

        await SyncAsync(titleId);

        using var fresh = WatchlistTestData.NewContext(_connection);
        var remaining = await fresh.TrackedReleases.ToListAsync();
        // The delivered row aged out and was pruned; the undelivered one is spared for the reminder.
        Assert.Equal(2, Assert.Single(remaining).Episode);
    }

    [Fact]
    public async Task Stale_movie_rows_are_dropped_when_the_provider_no_longer_reports_them()
    {
        Guid titleId;
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var title = WatchlistTestData.SeedTitle(database, MediaKind.Movie, "27205");
            WatchlistTestData.SeedRelease(database, title, ReleaseType.Premiere, new DateOnly(2026, 7, 1), region: "US", rawType: 1);
            titleId = title.Id;
        }

        _schedule.Movies["27205"] = new MovieReleaseSchedule("Inception", 2010, null, "Post Production",
            [new TypedReleaseDate("US", ReleaseType.Theatrical, 3, new DateOnly(2026, 8, 1), null)]);

        await SyncAsync(titleId);

        using var fresh = WatchlistTestData.NewContext(_connection);
        var release = await fresh.TrackedReleases.SingleAsync();
        Assert.Equal(ReleaseType.Theatrical, release.Type);
    }

    [Fact]
    public async Task Full_pass_skips_recently_synced_titles()
    {
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            WatchlistTestData.SeedTitle(database, MediaKind.Movie, "1", lastRefreshedAt: Now.AddHours(-2));
            WatchlistTestData.SeedTitle(database, MediaKind.Movie, "2", lastRefreshedAt: Now.AddHours(-30));
            WatchlistTestData.SeedTitle(database, MediaKind.Movie, "3");
        }

        using var scope = _provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WatchlistSyncService>();
        var stale = await service.ListStaleTitleIdsAsync(CancellationToken.None);

        Assert.Equal(2, stale.Count); // "1" was refreshed 2h ago — the 24h cadence hasn't elapsed
    }

    private async Task<WatchlistSyncService.SyncOutcome> SyncAsync(Guid titleId, bool force = false)
    {
        using var scope = _provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WatchlistSyncService>();
        return await service.SyncTitleAsync(titleId, force, CancellationToken.None);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    private sealed class NullNotifier : IRealtimeNotifier
    {
        public Task DownloadProgressAsync(DownloadProgress progress, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DownloadStateChangedAsync(DownloadStateChanged change, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task IngestStageChangedAsync(IngestStageChanged change, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task VpnStatusChangedAsync(VpnStatusChanged status, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task JobChangedAsync(string eventName, JobEvent job, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
