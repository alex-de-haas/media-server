using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.Watchlist;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Watchlist;

/// <summary>
/// The local-only dispatch loop: fire-time resolution (leadDays + NotifyAt in the app timezone), the
/// once-per-event ledger (movie one-shot vs series recurring), season-drop collapse, retroactivity and
/// scope guards, per-user Core targeting, and retirement of finished series reminders.
/// </summary>
public sealed class ReminderDispatchServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 7, 14);

    private readonly SqliteConnection _connection = WatchlistTestData.OpenDatabase();
    private readonly FixedTimeProvider _clock = new(Now);
    private readonly RecordingCoreClient _core = new();

    public ReminderDispatchServiceTests()
    {
        using var database = WatchlistTestData.NewContext(_connection);
        database.Database.Migrate();
    }

    [Fact]
    public async Task Movie_reminder_fires_at_lead_days_before_at_notify_time_and_only_once()
    {
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var user = WatchlistTestData.SeedUser(database);
            var title = WatchlistTestData.SeedTitle(database, MediaKind.Movie, "27205", "Dune: Part Two");
            WatchlistTestData.SeedEntry(database, user, title);
            // Digital release Jul 16; lead 2 days at 09:00 UTC → fires Jul 14 09:00.
            WatchlistTestData.SeedRelease(database, title, ReleaseType.Digital, new DateOnly(2026, 7, 16), region: "US");
            WatchlistTestData.SeedReminder(database, user, title, ReleaseType.Digital, leadDays: 2, createdAt: Now.AddDays(-10));
        }

        // 08:59 — not yet.
        _clock.Now = new DateTimeOffset(2026, 7, 14, 8, 59, 0, TimeSpan.Zero);
        Assert.Equal(0, (await DispatchAsync()).Delivered);
        Assert.Empty(_core.Published);

        // 09:01 — due.
        _clock.Now = new DateTimeOffset(2026, 7, 14, 9, 1, 0, TimeSpan.Zero);
        Assert.Equal(1, (await DispatchAsync()).Delivered);
        var notification = Assert.Single(_core.Published);
        Assert.Contains("Dune: Part Two", notification.Title);
        Assert.Contains("Digital", notification.Title);
        Assert.Equal("host-1", notification.Target); // per-user targeting

        // Ledgered: one row, and a later tick never re-fires (movie one-shot).
        using (var fresh = WatchlistTestData.NewContext(_connection))
        {
            Assert.Equal(1, await fresh.ReminderDeliveries.CountAsync());
        }

        _clock.Now = new DateTimeOffset(2026, 7, 15, 9, 1, 0, TimeSpan.Zero);
        Assert.Equal(0, (await DispatchAsync()).Delivered);
        Assert.Single(_core.Published);
    }

    [Fact]
    public async Task Fire_time_resolves_in_the_app_timezone()
    {
        // 09:00 local in UTC+2 is 07:00 UTC.
        _clock.TimeZone = TimeZoneInfo.CreateCustomTimeZone("UTC+2", TimeSpan.FromHours(2), "UTC+2", "UTC+2");
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var user = WatchlistTestData.SeedUser(database);
            var title = WatchlistTestData.SeedTitle(database, MediaKind.Movie, "27205", "Dune");
            WatchlistTestData.SeedEntry(database, user, title);
            WatchlistTestData.SeedRelease(database, title, ReleaseType.Digital, Today, region: "US");
            WatchlistTestData.SeedReminder(database, user, title, ReleaseType.Digital, leadDays: 0, createdAt: Now.AddDays(-1));
        }

        _clock.Now = new DateTimeOffset(2026, 7, 14, 6, 30, 0, TimeSpan.Zero); // 08:30 local
        Assert.Equal(0, (await DispatchAsync()).Delivered);

        _clock.Now = new DateTimeOffset(2026, 7, 14, 7, 30, 0, TimeSpan.Zero); // 09:30 local
        Assert.Equal(1, (await DispatchAsync()).Delivered);
    }

    [Fact]
    public async Task A_fire_time_already_past_fires_on_the_next_tick()
    {
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var user = WatchlistTestData.SeedUser(database);
            var title = WatchlistTestData.SeedTitle(database, MediaKind.Movie, "27205", "Old Movie");
            WatchlistTestData.SeedEntry(database, user, title);
            // Released a month ago; the reminder was created afterwards — still delivered once.
            WatchlistTestData.SeedRelease(database, title, ReleaseType.Digital, Today.AddDays(-30), region: "US");
            WatchlistTestData.SeedReminder(database, user, title, ReleaseType.Digital, leadDays: 2, createdAt: Now.AddHours(-1));
        }

        Assert.Equal(1, (await DispatchAsync()).Delivered);
        Assert.Contains("released", Assert.Single(_core.Published).Title);
    }

    [Fact]
    public async Task Reminder_follows_a_moved_date()
    {
        Guid releaseId;
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var user = WatchlistTestData.SeedUser(database);
            var title = WatchlistTestData.SeedTitle(database, MediaKind.Movie, "27205", "Dune");
            WatchlistTestData.SeedEntry(database, user, title);
            releaseId = WatchlistTestData.SeedRelease(database, title, ReleaseType.Digital, Today.AddDays(1), region: "US").Id;
            WatchlistTestData.SeedReminder(database, user, title, ReleaseType.Digital, leadDays: 0, createdAt: Now.AddDays(-10));
        }

        Assert.Equal(0, (await DispatchAsync()).Delivered); // tomorrow: not due

        // The sync moved the date up to today (same row identity).
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var release = await database.TrackedReleases.SingleAsync(candidate => candidate.Id == releaseId);
            release.PreviousDate = release.Date;
            release.Date = Today;
            await database.SaveChangesAsync();
        }

        Assert.Equal(1, (await DispatchAsync()).Delivered); // followed the move, fired once
    }

    [Fact]
    public async Task Movie_reminder_resolves_the_entrys_effective_region()
    {
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var user = WatchlistTestData.SeedUser(database);
            var title = WatchlistTestData.SeedTitle(database, MediaKind.Movie, "27205", "Dune");
            WatchlistTestData.SeedEntry(database, user, title, regionOverride: "RU");
            // The US date is out; the RU date (the entry's region) is not.
            WatchlistTestData.SeedRelease(database, title, ReleaseType.Digital, Today.AddDays(-1), region: "US");
            WatchlistTestData.SeedRelease(database, title, ReleaseType.Digital, Today.AddDays(30), region: "RU");
            WatchlistTestData.SeedReminder(database, user, title, ReleaseType.Digital, createdAt: Now.AddDays(-10));
        }

        Assert.Equal(0, (await DispatchAsync()).Delivered); // RU date is still a month out
    }

    [Fact]
    public async Task Series_reminder_recurs_per_episode_and_keeps_going_as_new_episodes_appear()
    {
        Guid titleId;
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var user = WatchlistTestData.SeedUser(database);
            var title = WatchlistTestData.SeedTitle(database, MediaKind.Series, "1396", "Slow Horses", status: "Returning Series");
            WatchlistTestData.SeedEntry(database, user, title, scope: SeriesMonitorScope.WholeShow, createdAt: Now.AddDays(-30));
            WatchlistTestData.SeedRelease(database, title, ReleaseType.EpisodeAir, Today.AddDays(-7), season: 1, episode: 10);
            WatchlistTestData.SeedRelease(database, title, ReleaseType.EpisodeAir, Today, season: 2, episode: 1);
            WatchlistTestData.SeedReminder(database, user, title, ReleaseType.EpisodeAir, createdAt: Now.AddDays(-30));
            titleId = title.Id;
        }

        // Both episodes are due (different dates → two notifications), each ledgered separately.
        Assert.Equal(2, (await DispatchAsync()).Delivered);
        Assert.Equal(2, _core.Published.Count);

        // The sync later discovers the next episode; the same reminder fires again — recurring.
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var title = await database.TrackedTitles.SingleAsync(candidate => candidate.Id == titleId);
            WatchlistTestData.SeedRelease(database, title, ReleaseType.EpisodeAir, Today.AddDays(7), season: 2, episode: 2);
        }

        _clock.Now = Now.AddDays(7);
        Assert.Equal(1, (await DispatchAsync()).Delivered);
        Assert.Equal(3, _core.Published.Count);
    }

    [Fact]
    public async Task Season_drop_collapses_into_one_notification_but_ledgers_every_episode()
    {
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var user = WatchlistTestData.SeedUser(database);
            var title = WatchlistTestData.SeedTitle(database, MediaKind.Series, "66732", "Stranger Things", status: "Returning Series");
            WatchlistTestData.SeedEntry(database, user, title, scope: SeriesMonitorScope.WholeShow, createdAt: Now.AddDays(-30));
            for (var episode = 1; episode <= 8; episode++)
            {
                WatchlistTestData.SeedRelease(database, title, ReleaseType.EpisodeAir, Today, season: 5, episode: episode);
            }

            WatchlistTestData.SeedReminder(database, user, title, ReleaseType.EpisodeAir, createdAt: Now.AddDays(-30));
        }

        var report = await DispatchAsync();

        Assert.Equal(1, report.Delivered); // one "Season 5 available" notification…
        var notification = Assert.Single(_core.Published);
        Assert.Contains("Season 5 available", notification.Title);

        using var fresh = WatchlistTestData.NewContext(_connection);
        Assert.Equal(8, await fresh.ReminderDeliveries.CountAsync()); // …but every episode is ledgered

        // Nothing re-fires on the next tick.
        Assert.Equal(0, (await DispatchAsync()).Delivered);
    }

    [Fact]
    public async Task Episodes_aired_before_the_reminder_existed_are_not_delivered_retroactively()
    {
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var user = WatchlistTestData.SeedUser(database);
            var title = WatchlistTestData.SeedTitle(database, MediaKind.Series, "1396", "Slow Horses", status: "Returning Series");
            WatchlistTestData.SeedEntry(database, user, title, scope: SeriesMonitorScope.WholeShow, createdAt: Now.AddDays(-30));
            WatchlistTestData.SeedRelease(database, title, ReleaseType.EpisodeAir, Today.AddDays(-5), season: 1, episode: 1);
            WatchlistTestData.SeedRelease(database, title, ReleaseType.EpisodeAir, Today.AddDays(1), season: 1, episode: 2);
            // The reminder was created today — only the future episode may fire.
            WatchlistTestData.SeedReminder(database, user, title, ReleaseType.EpisodeAir, createdAt: Now);
        }

        Assert.Equal(0, (await DispatchAsync()).Delivered);

        _clock.Now = Now.AddDays(2);
        Assert.Equal(1, (await DispatchAsync()).Delivered);
        Assert.Contains("S1E2", Assert.Single(_core.Published).Title);
    }

    [Fact]
    public async Task Scope_filtering_limits_which_episodes_fire()
    {
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var user = WatchlistTestData.SeedUser(database);
            var title = WatchlistTestData.SeedTitle(database, MediaKind.Series, "1396", "Slow Horses", status: "Returning Series");
            WatchlistTestData.SeedEntry(
                database, user, title, scope: SeriesMonitorScope.Seasons, monitoredSeasons: [2], createdAt: Now.AddDays(-30));
            WatchlistTestData.SeedRelease(database, title, ReleaseType.EpisodeAir, Today, season: 1, episode: 5);
            WatchlistTestData.SeedRelease(database, title, ReleaseType.EpisodeAir, Today, season: 2, episode: 1);
            WatchlistTestData.SeedReminder(database, user, title, ReleaseType.EpisodeAir, createdAt: Now.AddDays(-30));
        }

        Assert.Equal(1, (await DispatchAsync()).Delivered);
        Assert.Contains("S2E1", Assert.Single(_core.Published).Title); // season 1 is out of scope
    }

    [Fact]
    public async Task Finished_series_reminder_retires_only_when_everything_covered_was_delivered()
    {
        Guid titleId;
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var user = WatchlistTestData.SeedUser(database);
            var title = WatchlistTestData.SeedTitle(database, MediaKind.Series, "1396", "Breaking Bad", status: "Returning Series");
            WatchlistTestData.SeedEntry(database, user, title, scope: SeriesMonitorScope.WholeShow, createdAt: Now.AddDays(-30));
            WatchlistTestData.SeedRelease(database, title, ReleaseType.EpisodeAir, Today.AddDays(-1), season: 5, episode: 16);
            WatchlistTestData.SeedReminder(database, user, title, ReleaseType.EpisodeAir, createdAt: Now.AddDays(-30));
            titleId = title.Id;
        }

        // Still "Returning Series": delivers the finale, does not retire.
        var first = await DispatchAsync();
        Assert.Equal(1, first.Delivered);
        Assert.Equal(0, first.Retired);

        // The sync marks the show Ended with nothing left ahead → the reminder retires with a final notice.
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var title = await database.TrackedTitles.SingleAsync(candidate => candidate.Id == titleId);
            title.ProductionStatus = "Ended";
            await database.SaveChangesAsync();
        }

        var second = await DispatchAsync();
        Assert.Equal(1, second.Retired);
        Assert.Contains(_core.Published, notification => notification.Title.Contains("has ended"));

        using var fresh = WatchlistTestData.NewContext(_connection);
        Assert.False((await fresh.ReleaseReminders.SingleAsync()).Active);
    }

    [Fact]
    public async Task Ended_series_with_a_future_episode_does_not_retire()
    {
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var user = WatchlistTestData.SeedUser(database);
            var title = WatchlistTestData.SeedTitle(database, MediaKind.Series, "1396", "Breaking Bad", status: "Ended");
            WatchlistTestData.SeedEntry(database, user, title, scope: SeriesMonitorScope.WholeShow, createdAt: Now.AddDays(-30));
            // Announced final episode still ahead.
            WatchlistTestData.SeedRelease(database, title, ReleaseType.EpisodeAir, Today.AddDays(10), season: 5, episode: 16);
            WatchlistTestData.SeedReminder(database, user, title, ReleaseType.EpisodeAir, createdAt: Now.AddDays(-30));
        }

        var report = await DispatchAsync();
        Assert.Equal(0, report.Retired);

        using var fresh = WatchlistTestData.NewContext(_connection);
        Assert.True((await fresh.ReleaseReminders.SingleAsync()).Active);
    }

    [Fact]
    public async Task Inactive_reminders_are_ignored_and_users_are_targeted_individually()
    {
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var alice = WatchlistTestData.SeedUser(database, "alice");
            var bob = WatchlistTestData.SeedUser(database, "bob");
            var title = WatchlistTestData.SeedTitle(database, MediaKind.Movie, "27205", "Dune");
            WatchlistTestData.SeedEntry(database, alice, title);
            WatchlistTestData.SeedEntry(database, bob, title);
            WatchlistTestData.SeedRelease(database, title, ReleaseType.Digital, Today, region: "US");
            WatchlistTestData.SeedReminder(database, alice, title, ReleaseType.Digital, createdAt: Now.AddDays(-10));
            WatchlistTestData.SeedReminder(database, bob, title, ReleaseType.Digital, active: false, createdAt: Now.AddDays(-10));
        }

        Assert.Equal(1, (await DispatchAsync()).Delivered);
        Assert.Equal("alice", Assert.Single(_core.Published).Target); // bob's is soft-disabled
    }

    private async Task<ReminderDispatchReport> DispatchAsync()
    {
        using var database = WatchlistTestData.NewContext(_connection);
        var service = new ReminderDispatchService(
            database,
            new MediaServerSettings { WatchRegion = "US" },
            _core,
            _clock,
            NullLogger<ReminderDispatchService>.Instance);
        return await service.DispatchAsync(CancellationToken.None);
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>Captures per-user Core notifications.</summary>
    internal sealed class RecordingCoreClient : IHostyCoreClient
    {
        public sealed record Notification(CoreNotificationLevel Level, string Title, string? Body, string? DedupeKey, string Target);

        public List<Notification> Published { get; } = [];

        public bool IsEnabled => true;

        public Task<CoreBackupResult?> CreateBackupAsync(string? note, CancellationToken cancellationToken) =>
            Task.FromResult<CoreBackupResult?>(null);

        public Task<bool> PublishNotificationAsync(
            CoreNotificationLevel level, string title, string? body, string? link, string? dedupeKey,
            string target = HostyCoreClient.BroadcastTarget, CancellationToken cancellationToken = default)
        {
            Published.Add(new Notification(level, title, body, dedupeKey, target));
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<CoreDirectoryUser>?> ListDirectoryUsersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CoreDirectoryUser>?>([]);
    }
}
