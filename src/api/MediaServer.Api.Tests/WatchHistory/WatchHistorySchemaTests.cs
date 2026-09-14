using MediaServer.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.WatchHistory;

/// <summary>
/// Exercises the shipped migration against a real SQLite database, so the constraints the design
/// depends on are proven rather than assumed.
/// </summary>
public sealed class WatchHistorySchemaTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly int _userId;
    private readonly Guid _itemId;

    public WatchHistorySchemaTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();

        var user = new AppUser
        {
            HostUserId = "host-1",
            Email = "alex@example.com",
            DisplayName = "Alex",
            Role = AppUserRole.User,
            CreatedAt = DateTimeOffset.UnixEpoch,
            LastSeenAt = DateTimeOffset.UnixEpoch,
        };
        _database.AppUsers.Add(user);

        var catalog = new Catalog
        {
            Id = Guid.NewGuid(),
            Name = "Movies",
            Type = CatalogType.Movie,
            Root = "/movies",
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };
        _database.Catalogs.Add(catalog);

        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            PublicId = Guid.NewGuid().ToString("N"),
            CatalogId = catalog.Id,
            Kind = MediaKind.Movie,
            Title = "Inception",
            AddedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };
        _database.MediaItems.Add(item);
        _database.SaveChanges();

        _userId = user.Id;
        _itemId = item.Id;
    }

    private PlaybackHistoryEntry Entry(string? sessionId, DateTimeOffset? watchedAt = null, PlaybackHistoryOrigin origin = PlaybackHistoryOrigin.LocalPlayback) => new()
    {
        Id = Guid.NewGuid(),
        AppUserId = _userId,
        MediaItemId = _itemId,
        CreatedAt = DateTimeOffset.UnixEpoch,
        WatchedAt = watchedAt,
        Origin = origin,
        PlaySessionId = sessionId,
    };


    [Fact]
    public void OneSessionCanOnlyRecordOnePlay()
    {
        // The rule that stops a rewind past the watched threshold recording a second viewing.
        _database.PlaybackHistoryEntries.Add(Entry("session-1"));
        _database.SaveChanges();

        _database.PlaybackHistoryEntries.Add(Entry("session-1"));

        Assert.Throws<DbUpdateException>(() => _database.SaveChanges());
    }

    [Fact]
    public void EntriesWithoutASessionAreNotConstrainedAgainstEachOther()
    {
        // Imported and manual plays all leave the session id null; an unfiltered unique index would
        // treat those nulls as one value in some providers and silently reject legitimate history.
        _database.PlaybackHistoryEntries.Add(Entry(null, DateTimeOffset.UnixEpoch, PlaybackHistoryOrigin.ProviderSync));
        _database.PlaybackHistoryEntries.Add(Entry(null, DateTimeOffset.UnixEpoch.AddHours(2), PlaybackHistoryOrigin.ProviderSync));
        _database.PlaybackHistoryEntries.Add(Entry(null, null, PlaybackHistoryOrigin.Manual));
        _database.SaveChanges();

        Assert.Equal(3, _database.PlaybackHistoryEntries.Count());
    }

    [Fact]
    public void TwoRealPlaysMayShareATimestamp()
    {
        // Deliberately not deduplicated by (item, timestamp): two genuine plays can land in the same
        // second at our precision, and collapsing them would lose a viewing.
        var when = DateTimeOffset.UnixEpoch.AddHours(5);
        _database.PlaybackHistoryEntries.Add(Entry("session-a", when));
        _database.PlaybackHistoryEntries.Add(Entry("session-b", when));
        _database.SaveChanges();

        Assert.Equal(2, _database.PlaybackHistoryEntries.Count());
    }

    [Fact]
    public void DeletingAnItemDropsItsHistory()
    {
        // A deleted item's plays can no longer be projected or exported.
        _database.PlaybackHistoryEntries.Add(Entry("session-1"));
        _database.SaveChanges();

        _database.MediaItems.Remove(_database.MediaItems.Single(item => item.Id == _itemId));
        _database.SaveChanges();

        Assert.Empty(_database.PlaybackHistoryEntries);
    }

    [Fact]
    public void UserItemDataCarriesTheProjectionFieldsAndRevision()
    {
        _database.UserItemData.Add(new UserItemData
        {
            Id = Guid.NewGuid(),
            AppUserId = _userId,
            MediaItemId = _itemId,
            Played = true,
            PlayCount = 2,
            LastWatchedAt = DateTimeOffset.UnixEpoch.AddDays(3),
            WatchedStateChangedAt = DateTimeOffset.UnixEpoch.AddDays(3),
            StateRevision = 7,
        });
        _database.SaveChanges();

        var row = _database.UserItemData.AsNoTracking().Single();
        Assert.Equal(DateTimeOffset.UnixEpoch.AddDays(3), row.LastWatchedAt);

        // Both synchronous and asynchronous saves bump the revision for delta consumers.
        // The seeded 7 is therefore stored as 8.
        Assert.Equal(8, row.StateRevision);
    }

    [Fact]
    public void APlaybackSessionCanPointAtTheEntryItsCompletionCreated()
    {
        var entry = Entry("session-1", DateTimeOffset.UnixEpoch.AddHours(1));
        _database.PlaybackHistoryEntries.Add(entry);
        _database.PlaybackSessions.Add(new PlaybackSession
        {
            Id = Guid.NewGuid(),
            AppUserId = _userId,
            MediaItemId = _itemId,
            SessionKey = "session-1",
            StartedAt = DateTimeOffset.UnixEpoch,
            LastReportAt = DateTimeOffset.UnixEpoch.AddHours(1),
            CompletedAt = DateTimeOffset.UnixEpoch.AddHours(1),
            HistoryEntryId = entry.Id,
        });
        _database.SaveChanges();

        Assert.Equal(entry.Id, _database.PlaybackSessions.AsNoTracking().Single().HistoryEntryId);
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }
}
