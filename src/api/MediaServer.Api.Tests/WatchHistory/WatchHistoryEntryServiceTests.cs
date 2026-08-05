using MediaServer.Api.Data;
using MediaServer.Api.Tests.Jellyfin;
using MediaServer.Api.WatchHistory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.WatchHistory;

/// <summary>
/// Deleting one recorded play: whose entries a caller may touch, what the aggregates become, and what
/// the provider is — and is not — asked to remove.
/// </summary>
public sealed class WatchHistoryEntryServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly TestTimeProvider _time = new(DateTimeOffset.Parse("2026-08-05T12:00:00Z"));
    private readonly int _userId;
    private readonly int _otherUserId;
    private readonly MediaItem _movie;

    public WatchHistoryEntryServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(
            new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();

        var user = NewUser("host-1", "alex@example.com");
        var other = NewUser("host-2", "sam@example.com");
        _database.AppUsers.AddRange(user, other);

        var catalogId = Guid.NewGuid();
        _database.Catalogs.Add(new Catalog
        {
            Id = catalogId, Name = "Movies", Type = CatalogType.Movie, Root = "/m",
            CreatedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow(),
        });

        _movie = new MediaItem
        {
            Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString("N"), CatalogId = catalogId,
            Kind = MediaKind.Movie, Title = "Inception",
            IdentityProvider = "tmdb", IdentityProviderId = "27205",
            AddedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow(),
        };
        _database.MediaItems.Add(_movie);
        _database.SaveChanges();

        _userId = user.Id;
        _otherUserId = other.Id;
    }

    // ---- Ownership ----

    [Fact]
    public async Task AnUnknownEntryIsNotFound()
    {
        Assert.False(await Service().DeleteAsync(_userId, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task AnotherUsersEntryIsNeitherFoundNorDeleted()
    {
        // The route answers 404 off this, so a caller cannot probe for the existence of someone
        // else's viewing by watching the status code change.
        var theirs = AddPlay("2026-08-01T20:00:00Z", appUserId: _otherUserId);

        Assert.False(await Service().DeleteAsync(_userId, theirs.Id, CancellationToken.None));
        Assert.True(await _database.PlaybackHistoryEntries.AnyAsync(entry => entry.Id == theirs.Id));
    }

    [Fact]
    public async Task ItDeletesTheEntryItWasGiven()
    {
        var first = AddPlay("2026-08-01T20:00:00Z");
        var second = AddPlay("2026-08-02T21:00:00Z");

        Assert.True(await Service().DeleteAsync(_userId, second.Id, CancellationToken.None));

        var remaining = Assert.Single(await _database.PlaybackHistoryEntries.AsNoTracking().ToListAsync());
        Assert.Equal(first.Id, remaining.Id);
    }

    // ---- Aggregates ----

    [Fact]
    public async Task TheAggregatesFollowTheRemainingPlays()
    {
        var first = AddPlay("2026-08-01T20:00:00Z");
        var second = AddPlay("2026-08-02T21:00:00Z");
        AddRow(playCount: 2, played: true, lastWatchedAt: second.WatchedAt);

        await Service().DeleteAsync(_userId, second.Id, CancellationToken.None);

        var row = await RowAsync();
        Assert.Equal(1, row.PlayCount);
        Assert.Equal(first.WatchedAt, row.LastWatchedAt);
        // One play of two went; the item is still watched.
        Assert.True(row.Played);
    }

    [Fact]
    public async Task DeletingTheLastPlayClearsTheWatchedFlag()
    {
        var only = AddPlay("2026-08-01T20:00:00Z");
        AddRow(playCount: 1, played: true, lastWatchedAt: only.WatchedAt);

        await Service().DeleteAsync(_userId, only.Id, CancellationToken.None);

        var row = await RowAsync();
        Assert.Equal(0, row.PlayCount);
        Assert.Null(row.LastWatchedAt);
        Assert.False(row.Played);
        Assert.Equal(_time.GetUtcNow(), row.WatchedStateChangedAt);
    }

    [Fact]
    public async Task AnUnwatchedItemIsNotMarkedWatchedByADeletion()
    {
        // Unwatch keeps exact plays on purpose. Tidying one of them away is not a claim that the item
        // is watched again, and flipping the flag here would silently undo the user's toggle.
        AddPlay("2026-08-01T20:00:00Z");
        var second = AddPlay("2026-08-02T21:00:00Z");
        AddRow(playCount: 2, played: false, lastWatchedAt: second.WatchedAt);

        await Service().DeleteAsync(_userId, second.Id, CancellationToken.None);

        var row = await RowAsync();
        Assert.False(row.Played);
        Assert.Null(row.WatchedStateChangedAt);
    }

    [Fact]
    public async Task ACountAheadOfTheEntriesLosesOnlyTheDeletedPlay()
    {
        // A mark, an unwatch and a re-mark legitimately leave one entry and a count of two, so the
        // count is not a strict projection of the table. Deleting one play takes one play.
        AddPlay("2026-08-01T20:00:00Z");
        var second = AddPlay("2026-08-02T21:00:00Z");
        AddRow(playCount: 4, played: true, lastWatchedAt: second.WatchedAt);

        await Service().DeleteAsync(_userId, second.Id, CancellationToken.None);

        Assert.Equal(3, (await RowAsync()).PlayCount);
    }

    [Fact]
    public async Task DeletingTheLastEntryLeavesACleanSlateHoweverFarTheCountHadDrifted()
    {
        // The count outran the entries, then the only entry went. Keeping the remainder would leave
        // "watched once" with nothing at all behind it, which is what the user just asked to remove.
        var only = AddPlay("2026-08-01T20:00:00Z");
        AddRow(playCount: 3, played: true, lastWatchedAt: only.WatchedAt);

        await Service().DeleteAsync(_userId, only.Id, CancellationToken.None);

        var row = await RowAsync();
        Assert.Equal(0, row.PlayCount);
        Assert.Null(row.LastWatchedAt);
        Assert.False(row.Played);
    }

    [Fact]
    public async Task ADeletionNeverIncreasesThePlayCount()
    {
        // A remap merges history onto a row without recomputing it, so the entries can outnumber the
        // count. Deleting a play and watching the play count go up is the worst answer available.
        AddPlay("2026-08-01T20:00:00Z");
        AddPlay("2026-08-02T21:00:00Z");
        var third = AddPlay("2026-08-03T22:00:00Z");
        AddRow(playCount: 1, played: true, lastWatchedAt: third.WatchedAt);

        await Service().DeleteAsync(_userId, third.Id, CancellationToken.None);

        Assert.Equal(1, (await RowAsync()).PlayCount);
    }

    [Fact]
    public async Task DeletingANonLatestPlayLeavesTheLastWatchedTimeAlone()
    {
        var first = AddPlay("2026-08-01T20:00:00Z");
        var second = AddPlay("2026-08-02T21:00:00Z");
        AddRow(playCount: 2, played: true, lastWatchedAt: second.WatchedAt);

        await Service().DeleteAsync(_userId, first.Id, CancellationToken.None);

        Assert.Equal(second.WatchedAt, (await RowAsync()).LastWatchedAt);
    }

    [Fact]
    public async Task ASurvivingTimelessMarkKeepsTheItemWatchedWithNoLastWatchedTime()
    {
        // The pre-migration shape, reached by deleting the only dated play: watched, but nothing can
        // honestly say when.
        var dated = AddPlay("2026-08-01T20:00:00Z");
        AddTimelessPlay();
        AddRow(playCount: 2, played: true, lastWatchedAt: dated.WatchedAt);

        await Service().DeleteAsync(_userId, dated.Id, CancellationToken.None);

        var row = await RowAsync();
        Assert.True(row.Played);
        Assert.Equal(1, row.PlayCount);
        Assert.Null(row.LastWatchedAt);
    }

    [Fact]
    public async Task AnItemWithNoAggregateRowIsStillDeletable()
    {
        var entry = AddPlay("2026-08-01T20:00:00Z");

        Assert.True(await Service().DeleteAsync(_userId, entry.Id, CancellationToken.None));
        Assert.Empty(_database.PlaybackHistoryEntries);
    }

    // ---- Outbound removal ----

    [Fact]
    public async Task AnOwnedEntryQueuesItsRemovalWithTheRemoteId()
    {
        Connect();
        var entry = AddPlay("2026-08-01T20:00:00Z", remoteId: "111", owned: true);

        await Service().DeleteAsync(_userId, entry.Id, CancellationToken.None);

        var queued = Assert.Single(await _database.WatchHistoryOutboxEvents.AsNoTracking().ToListAsync());
        Assert.Equal(WatchHistoryOutboxOperation.RemoveOwnedEntries, queued.Operation);
        Assert.Contains("111", queued.RemoteIdSnapshot);
    }

    [Fact]
    public async Task AnImportedEntryIsNeverRemovedRemotely()
    {
        // A matching identity and timestamp is not evidence of ownership: removing it would take a
        // play another client recorded.
        Connect();
        var entry = AddPlay(
            "2026-08-01T20:00:00Z", remoteId: "111", owned: false, origin: PlaybackHistoryOrigin.ProviderSync);

        await Service().DeleteAsync(_userId, entry.Id, CancellationToken.None);

        Assert.Empty(_database.WatchHistoryOutboxEvents);
    }

    [Fact]
    public async Task AnUnresolvedEntryIsNeverRemovedRemotely()
    {
        // The add committed but its remote id was never pinned down. Guessing here destroys history
        // this app did not create.
        Connect();
        var entry = AddPlay(
            "2026-08-01T20:00:00Z", remoteId: "111", owned: true, link: PlaybackHistoryLinkStatus.Unresolved);

        await Service().DeleteAsync(_userId, entry.Id, CancellationToken.None);

        Assert.Empty(_database.WatchHistoryOutboxEvents);
    }

    [Fact]
    public async Task AnEntryWithNothingToRemoveQueuesNoWork()
    {
        // An empty removal would complete as a no-op, but until the worker reached it the user's
        // explicit sync would refuse to start, calling it undelivered work.
        Connect();
        var entry = AddPlay("2026-08-01T20:00:00Z");

        await Service().DeleteAsync(_userId, entry.Id, CancellationToken.None);

        Assert.Empty(_database.WatchHistoryOutboxEvents);
    }

    [Fact]
    public async Task WithoutAConnectionNothingIsQueued()
    {
        var entry = AddPlay("2026-08-01T20:00:00Z", remoteId: "111", owned: true);

        await Service().DeleteAsync(_userId, entry.Id, CancellationToken.None);

        Assert.Empty(_database.WatchHistoryOutboxEvents);
    }

    [Fact]
    public async Task DeletingTwoPlaysOfOneItemQueuesBothRemovals()
    {
        // Deleting two plays changes no watched state at all, so a row-derived idempotency key would
        // collide and the second removal would be swallowed as a duplicate.
        Connect();
        var first = AddPlay("2026-08-01T20:00:00Z", remoteId: "111", owned: true);
        var second = AddPlay("2026-08-02T21:00:00Z", remoteId: "222", owned: true);
        AddRow(playCount: 2, played: true, lastWatchedAt: second.WatchedAt);

        await Service().DeleteAsync(_userId, first.Id, CancellationToken.None);
        await Service().DeleteAsync(_userId, second.Id, CancellationToken.None);

        var queued = await _database.WatchHistoryOutboxEvents.AsNoTracking().ToListAsync();
        Assert.Equal(2, queued.Count);
        Assert.Contains(queued, item => item.RemoteIdSnapshot!.Contains("111"));
        Assert.Contains(queued, item => item.RemoteIdSnapshot!.Contains("222"));
    }

    // ---- Helpers ----

    private WatchHistoryEntryService Service() => new(
        _database,
        new WatchHistoryRecorder(
            _database,
            new WatchHistoryIdentityMapper(_database),
            _time,
            NullLogger<WatchHistoryRecorder>.Instance));

    private AppUser NewUser(string hostUserId, string email) => new()
    {
        HostUserId = hostUserId, Email = email, DisplayName = email, Role = AppUserRole.User,
        CreatedAt = _time.GetUtcNow(), LastSeenAt = _time.GetUtcNow(),
    };

    private void Connect()
    {
        var connection = new WatchHistoryProviderConnection
        {
            Id = Guid.NewGuid(),
            AppUserId = _userId,
            ProviderKey = "trakt",
            Status = WatchHistoryConnectionStatus.Connected,
            ConnectedAt = _time.GetUtcNow(),
        };
        connection.SecretKey = $"trakt.connection.{connection.Id:N}.tokens";
        _database.WatchHistoryConnections.Add(connection);
        _database.SaveChanges();
    }

    private PlaybackHistoryEntry AddPlay(
        string watchedAt,
        int? appUserId = null,
        string? remoteId = null,
        bool owned = false,
        PlaybackHistoryOrigin origin = PlaybackHistoryOrigin.LocalPlayback,
        PlaybackHistoryLinkStatus link = PlaybackHistoryLinkStatus.Resolved)
    {
        var entry = new PlaybackHistoryEntry
        {
            Id = Guid.NewGuid(),
            AppUserId = appUserId ?? _userId,
            MediaItemId = _movie.Id,
            CreatedAt = _time.GetUtcNow(),
            WatchedAt = DateTimeOffset.Parse(watchedAt),
            Origin = origin,
            ProviderKey = remoteId is null ? null : "trakt",
            ProviderHistoryId = remoteId,
            ProviderEntryOwned = owned,
            LinkStatus = remoteId is null ? PlaybackHistoryLinkStatus.None : link,
        };
        _database.PlaybackHistoryEntries.Add(entry);
        _database.SaveChanges();
        return entry;
    }

    private void AddTimelessPlay()
    {
        _database.PlaybackHistoryEntries.Add(new PlaybackHistoryEntry
        {
            Id = Guid.NewGuid(),
            AppUserId = _userId,
            MediaItemId = _movie.Id,
            CreatedAt = _time.GetUtcNow(),
            WatchedAt = null,
            Origin = PlaybackHistoryOrigin.Manual,
            LinkStatus = PlaybackHistoryLinkStatus.None,
        });
        _database.SaveChanges();
    }

    private void AddRow(int playCount, bool played, DateTimeOffset? lastWatchedAt)
    {
        _database.UserItemData.Add(new UserItemData
        {
            Id = Guid.NewGuid(),
            AppUserId = _userId,
            MediaItemId = _movie.Id,
            PlayCount = playCount,
            Played = played,
            LastWatchedAt = lastWatchedAt,
        });
        _database.SaveChanges();
    }

    private async Task<UserItemData> RowAsync() => await _database.UserItemData.AsNoTracking()
        .SingleAsync(row => row.AppUserId == _userId && row.MediaItemId == _movie.Id);

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }
}
