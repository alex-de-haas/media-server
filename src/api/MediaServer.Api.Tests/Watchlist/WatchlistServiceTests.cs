using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Watchlist;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.Watchlist;

/// <summary>
/// Layer-1 watchlist operations: add (dedupe of the global title across users, immediate sync), per-user
/// scoping of reads and mutations, remove (drops the user's reminders, cleans up orphaned titles), and the
/// calendar read (region + scope filtering, reminder/library flags).
/// </summary>
public sealed class WatchlistServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 7, 14);

    private readonly SqliteConnection _connection = WatchlistTestData.OpenDatabase();
    private readonly FixedTimeProvider _clock = new(Now);
    private readonly RecordingSyncQueue _queue = new();

    public WatchlistServiceTests()
    {
        using var database = WatchlistTestData.NewContext(_connection);
        database.Database.Migrate();
    }

    [Fact]
    public async Task Add_creates_the_entry_dedupes_the_global_title_and_queues_an_immediate_sync()
    {
        int aliceId;
        int bobId;
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            aliceId = WatchlistTestData.SeedUser(database, "alice").Id;
            bobId = WatchlistTestData.SeedUser(database, "bob").Id;
        }

        var request = new AddWatchlistRequest(
            new ProviderRefBody("tmdb", "27205"), MediaKind.Movie, null, null, null, null, "Inception", 2010, "poster");

        var first = await ExecuteAsync(service => service.AddAsync(aliceId, request, CancellationToken.None));
        Assert.Null(first.Error);
        Assert.True(first.Created);
        Assert.Equal("Inception", first.Item!.Title);
        Assert.False(first.Item.InLibrary);

        // A second user tracking the same identity reuses the same global title (stored/synced once)…
        var second = await ExecuteAsync(service => service.AddAsync(bobId, request, CancellationToken.None));
        Assert.True(second.Created);
        Assert.Equal(first.Item.TrackedTitleId, second.Item!.TrackedTitleId);

        // …and re-adding is idempotent for the same user.
        var third = await ExecuteAsync(service => service.AddAsync(aliceId, request, CancellationToken.None));
        Assert.False(third.Created);

        using var fresh = WatchlistTestData.NewContext(_connection);
        Assert.Equal(1, await fresh.TrackedTitles.CountAsync());
        Assert.Equal(2, await fresh.WatchlistEntries.CountAsync());

        // Newly added titles sync immediately (once per creation).
        Assert.Equal([first.Item.TrackedTitleId, first.Item.TrackedTitleId], _queue.Enqueued);
    }

    [Theory]
    [InlineData(MediaKind.Season)]
    [InlineData(MediaKind.Episode)]
    public async Task Add_rejects_non_trackable_kinds(MediaKind kind)
    {
        int userId;
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            userId = WatchlistTestData.SeedUser(database).Id;
        }

        var result = await ExecuteAsync(service => service.AddAsync(
            userId,
            new AddWatchlistRequest(new ProviderRefBody("tmdb", "1"), kind, null, null, null, null, null, null, null),
            CancellationToken.None));

        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Add_validates_scope_and_region()
    {
        int userId;
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            userId = WatchlistTestData.SeedUser(database).Id;
        }

        // Movies never monitor episodes.
        var movieScope = await ExecuteAsync(service => service.AddAsync(
            userId,
            new AddWatchlistRequest(new ProviderRefBody("tmdb", "1"), MediaKind.Movie, SeriesMonitorScope.WholeShow, null, null, null, null, null, null),
            CancellationToken.None));
        Assert.NotNull(movieScope.Error);

        // Seasons scope needs chosen seasons.
        var noSeasons = await ExecuteAsync(service => service.AddAsync(
            userId,
            new AddWatchlistRequest(new ProviderRefBody("tmdb", "2"), MediaKind.Series, SeriesMonitorScope.Seasons, null, null, null, null, null, null),
            CancellationToken.None));
        Assert.NotNull(noSeasons.Error);

        // A region override must be a bare alpha-2 code.
        var badRegion = await ExecuteAsync(service => service.AddAsync(
            userId,
            new AddWatchlistRequest(new ProviderRefBody("tmdb", "3"), MediaKind.Movie, null, null, "USA", null, null, null, null),
            CancellationToken.None));
        Assert.NotNull(badRegion.Error);
    }

    [Fact]
    public async Task Reads_and_mutations_are_scoped_to_the_acting_user()
    {
        int aliceId;
        int bobId;
        Guid aliceEntryId;
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var alice = WatchlistTestData.SeedUser(database, "alice");
            var bob = WatchlistTestData.SeedUser(database, "bob");
            aliceId = alice.Id;
            bobId = bob.Id;
            var title = WatchlistTestData.SeedTitle(database, MediaKind.Series, "1396", "Slow Horses");
            aliceEntryId = WatchlistTestData.SeedEntry(database, alice, title).Id;
        }

        Assert.Single(await ExecuteAsync(service => service.ListAsync(aliceId, CancellationToken.None)));
        Assert.Empty(await ExecuteAsync(service => service.ListAsync(bobId, CancellationToken.None)));

        // Bob cannot touch Alice's entry.
        Assert.Null(await ExecuteAsync(service => service.UpdateAsync(
            bobId, aliceEntryId, new UpdateWatchlistRequest(true, SeriesMonitorScope.WholeShow, null, null, null, null, null), CancellationToken.None)));
        Assert.False(await ExecuteAsync(service => service.RemoveAsync(bobId, aliceEntryId, CancellationToken.None)));
        Assert.False(await ExecuteAsync(service => service.RefreshAsync(bobId, aliceEntryId, CancellationToken.None)));

        // Alice can: a scope change persists and re-queues the sync.
        var updated = await ExecuteAsync(service => service.UpdateAsync(
            aliceId, aliceEntryId, new UpdateWatchlistRequest(true, SeriesMonitorScope.Seasons, [2], null, null, null, null), CancellationToken.None));
        Assert.Equal(SeriesMonitorScope.Seasons, updated!.MonitorScope);
        Assert.Equal([2], updated.MonitoredSeasons!);
        Assert.Single(_queue.Enqueued);

        Assert.True(await ExecuteAsync(service => service.RefreshAsync(aliceId, aliceEntryId, CancellationToken.None)));
        Assert.Equal(2, _queue.Enqueued.Count);
    }

    [Fact]
    public async Task Remove_drops_the_users_reminders_and_cleans_up_an_orphaned_title()
    {
        int aliceId;
        Guid aliceEntryId;
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var alice = WatchlistTestData.SeedUser(database, "alice");
            aliceId = alice.Id;
            var title = WatchlistTestData.SeedTitle(database, MediaKind.Movie, "27205", "Inception");
            aliceEntryId = WatchlistTestData.SeedEntry(database, alice, title).Id;
            WatchlistTestData.SeedReminder(database, alice, title, ReleaseType.Digital);
            WatchlistTestData.SeedRelease(database, title, ReleaseType.Digital, Today.AddDays(3), region: "US");
        }

        Assert.True(await ExecuteAsync(service => service.RemoveAsync(aliceId, aliceEntryId, CancellationToken.None)));

        using var fresh = WatchlistTestData.NewContext(_connection);
        Assert.Empty(fresh.WatchlistEntries);
        Assert.Empty(fresh.ReleaseReminders);
        Assert.Empty(fresh.TrackedTitles); // nobody tracks it, not in the library → cleaned up (releases cascade)
        Assert.Empty(fresh.TrackedReleases);
    }

    [Fact]
    public async Task Remove_keeps_the_title_while_another_user_still_tracks_it()
    {
        int aliceId;
        Guid aliceEntryId;
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var alice = WatchlistTestData.SeedUser(database, "alice");
            var bob = WatchlistTestData.SeedUser(database, "bob");
            aliceId = alice.Id;
            var title = WatchlistTestData.SeedTitle(database, MediaKind.Movie, "27205", "Inception");
            aliceEntryId = WatchlistTestData.SeedEntry(database, alice, title).Id;
            WatchlistTestData.SeedEntry(database, bob, title);
            WatchlistTestData.SeedReminder(database, bob, title, ReleaseType.Digital);
        }

        Assert.True(await ExecuteAsync(service => service.RemoveAsync(aliceId, aliceEntryId, CancellationToken.None)));

        using var fresh = WatchlistTestData.NewContext(_connection);
        Assert.Single(fresh.TrackedTitles);
        Assert.Single(fresh.WatchlistEntries); // bob's
        Assert.Single(fresh.ReleaseReminders); // bob's reminder is untouched
    }

    [Fact]
    public async Task Calendar_returns_the_entries_dated_events_in_range()
    {
        int userId;
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var user = WatchlistTestData.SeedUser(database);
            userId = user.Id;

            var movie = WatchlistTestData.SeedTitle(database, MediaKind.Movie, "27205", "Inception");
            WatchlistTestData.SeedEntry(database, user, movie);
            WatchlistTestData.SeedRelease(database, movie, ReleaseType.Theatrical, Today.AddDays(2), region: "US");
            WatchlistTestData.SeedRelease(database, movie, ReleaseType.Digital, Today.AddDays(60), region: "US"); // outside range
            WatchlistTestData.SeedRelease(database, movie, ReleaseType.Digital, Today.AddDays(3), region: "RU"); // other region
            WatchlistTestData.SeedReminder(database, user, movie, ReleaseType.Theatrical);

            var series = WatchlistTestData.SeedTitle(database, MediaKind.Series, "1396", "Slow Horses");
            WatchlistTestData.SeedEntry(database, user, series, scope: SeriesMonitorScope.Seasons, monitoredSeasons: [4]);
            WatchlistTestData.SeedRelease(database, series, ReleaseType.EpisodeAir, Today.AddDays(5), season: 4, episode: 1);
            WatchlistTestData.SeedRelease(database, series, ReleaseType.EpisodeAir, Today.AddDays(6), season: 3, episode: 9); // out of scope
        }

        var events = await ExecuteAsync(service => service.CalendarAsync(userId, Today, Today.AddDays(30), CancellationToken.None));

        Assert.Equal(2, events.Count);
        var theatrical = events[0];
        Assert.Equal(ReleaseType.Theatrical, theatrical.Type);
        Assert.True(theatrical.HasReminder);
        Assert.False(theatrical.InLibrary);
        var episode = events[1];
        Assert.Equal(4, episode.Season);
        Assert.False(episode.HasReminder);
    }

    [Fact]
    public async Task Calendar_shows_only_the_title_level_episodes_for_a_tracking_off_series()
    {
        int userId;
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var user = WatchlistTestData.SeedUser(database);
            userId = user.Id;

            var series = WatchlistTestData.SeedTitle(database, MediaKind.Series, "1396", "Slow Horses");
            // Another user's WholeShow scope materialized the full season; this user tracks calendar-only.
            series.NextAirSeason = 4;
            series.NextAirEpisode = 2;
            series.NextAirDate = Today.AddDays(9);
            database.SaveChanges();
            WatchlistTestData.SeedEntry(database, user, series, scope: null);
            WatchlistTestData.SeedRelease(database, series, ReleaseType.EpisodeAir, Today.AddDays(9), season: 4, episode: 2);
            WatchlistTestData.SeedRelease(database, series, ReleaseType.EpisodeAir, Today.AddDays(16), season: 4, episode: 3);
        }

        var events = await ExecuteAsync(service => service.CalendarAsync(userId, Today, Today.AddDays(30), CancellationToken.None));

        var single = Assert.Single(events); // only the "when it returns" signal, not the whole materialized season
        Assert.Equal(2, single.Episode);
    }

    private async Task<T> ExecuteAsync<T>(Func<WatchlistService, Task<T>> action)
    {
        using var database = WatchlistTestData.NewContext(_connection);
        var service = new WatchlistService(database, new MediaServerSettings { WatchRegion = "US" }, _queue, _clock);
        return await action(service);
    }

    public void Dispose() => _connection.Dispose();
}

/// <summary>Records queued title ids without a worker.</summary>
internal sealed class RecordingSyncQueue : IWatchlistSyncQueue
{
    public List<Guid> Enqueued { get; } = [];

    public void Enqueue(Guid trackedTitleId) => Enqueued.Add(trackedTitleId);

    public async IAsyncEnumerable<Guid> DequeueAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }
}
