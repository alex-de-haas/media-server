using MediaServer.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MediaServer.Api.Tests.WatchHistory;

public sealed class LocalWatchHistoryMigrationTests
{
    private const string PreviousMigration = "20260910171856_AddVideoPartJoining";

    [Fact]
    public async Task Upgrade_PreservesAllPlaysAndUserState_AndRemovesOnlyIntegrationSchema()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var database = new MediaServerDbContext(
            new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(connection).Options);
        await database.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        var now = DateTimeOffset.Parse("2026-09-10T20:00:00Z");
        var storedNow = (string)new UtcDateTimeOffsetConverter().ConvertToProvider(now)!;
        var user = new AppUser
        {
            HostUserId = "viewer", Email = "viewer@example.com", DisplayName = "Viewer",
            CreatedAt = now, LastSeenAt = now,
        };
        var catalog = new Catalog
        {
            Id = Guid.NewGuid(), Name = "Movies", Type = CatalogType.Movie, Root = "/movies",
            CreatedAt = now, UpdatedAt = now,
        };
        var item = new MediaItem
        {
            Id = Guid.NewGuid(), PublicId = "movie", CatalogId = catalog.Id, Kind = MediaKind.Movie,
            Title = "Movie", AddedAt = now, UpdatedAt = now,
        };
        database.AddRange(user, catalog, item);
        await database.SaveChangesAsync();

        var expected = new List<(Guid Id, PlaybackHistoryOrigin Origin, DateTimeOffset? WatchedAt)>();
        foreach (var origin in Enum.GetValues<PlaybackHistoryOrigin>())
        {
            foreach (var watchedAt in new DateTimeOffset?[] { null, now })
            {
                var id = Guid.NewGuid();
                expected.Add((id, origin, watchedAt));
                // The fixture names the old required columns explicitly. The current entity cannot
                // represent them, and an upgrade must not discard imported or unresolved entries.
                await database.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "PlaybackHistoryEntries"
                        ("Id", "AppUserId", "MediaItemId", "CreatedAt", "WatchedAt", "Origin",
                         "PlaySessionId", "IdentitySnapshot", "LinkStatus", "ProviderEntryOwned", "ProviderKey", "ProviderHistoryId")
                    VALUES ({id}, {user.Id}, {item.Id}, {storedNow}, {(watchedAt.HasValue ? storedNow : null)}, {(int)origin},
                            {(origin == PlaybackHistoryOrigin.LocalPlayback ? id.ToString("N") : null)}, {"{}"}, 3, 1, 'external', {id.ToString("N")});
                    """);
            }
        }

        database.UserItemData.Add(new UserItemData
        {
            Id = Guid.NewGuid(), AppUserId = user.Id, MediaItemId = item.Id, Played = true,
            PlayCount = expected.Count, IsFavorite = true, Rating = 5, LastWatchedAt = now,
            PlaybackPositionTicks = 12345,
        });
        database.PlaybackSessions.Add(new PlaybackSession
        {
            Id = Guid.NewGuid(), AppUserId = user.Id, MediaItemId = item.Id, SessionKey = "active",
            StartedAt = now, LastReportAt = now, CompletedAt = now, HistoryEntryId = expected[0].Id,
        });
        await database.SaveChangesAsync();
        var stateBefore = await database.UserItemData.AsNoTracking().SingleAsync();
        var oldConnection = Guid.NewGuid();
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "WatchHistoryConnections"
                ("Id", "AppUserId", "ProviderKey", "SecretKey", "Status", "ConnectedAt")
            VALUES ({oldConnection}, {user.Id}, 'external', 'old-key', 0, {storedNow});
            INSERT INTO "WatchHistoryOutboxEvents"
                ("Id", "ConnectionId", "AppUserId", "MediaItemId", "Operation", "IdempotencyKey",
                 "Status", "Attempts", "CreatedAt", "NextAttemptAt")
            VALUES ({Guid.NewGuid()}, {oldConnection}, {user.Id}, {item.Id}, 0, 'queued', 0, 0, {storedNow}, {storedNow});
            """);

        await database.Database.MigrateAsync();
        database.ChangeTracker.Clear();

        var plays = await database.PlaybackHistoryEntries.ToListAsync();
        Assert.Equal(expected.Count, plays.Count);
        foreach (var (id, origin, watchedAt) in expected)
        {
            var play = Assert.Single(plays, entry => entry.Id == id);
            Assert.Equal(origin, play.Origin);
            Assert.Equal(watchedAt, play.WatchedAt);
        }
        var state = await database.UserItemData.SingleAsync();
        Assert.True(state.Played);
        Assert.True(state.IsFavorite);
        Assert.Equal(5, state.Rating);
        Assert.Equal(expected.Count, state.PlayCount);
        Assert.Equal(now, state.LastWatchedAt);
        Assert.Equal(12345, state.PlaybackPositionTicks);
        Assert.Equal(stateBefore.StateRevision, state.StateRevision);
        Assert.Equal(expected[0].Id, (await database.PlaybackSessions.SingleAsync()).HistoryEntryId);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name LIKE 'WatchHistory%';";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('PlaybackHistoryEntries') WHERE name IN ('ProviderKey', 'ProviderHistoryId', 'ProviderEntryOwned', 'LinkStatus', 'IdentitySnapshot');";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
        command.CommandText = "PRAGMA foreign_key_check;";
        Assert.Null(await command.ExecuteScalarAsync());
        Assert.False(database.Database.HasPendingModelChanges());
    }
}
