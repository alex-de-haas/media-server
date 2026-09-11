using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Tests.Jellyfin;
using MediaServer.Api.WatchHistory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.WatchHistory;

/// <summary>
/// Editing one recorded play — deleting it, or moving it in time: whose entries a caller may touch,
/// what the aggregates become, and which recording session is reopened.
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

    // ---- The session gate ----

    [Fact]
    public async Task DeletingAPlayReopensTheSessionThatRecordedIt()
    {
        // Sessions are kept for 24 hours and decide a crossing by asking whether this session already
        // completed. Left pointing at a deleted play it would answer "already counted" all day: the
        // same client session finishing again would mark the item played and record nothing.
        var entry = AddPlay("2026-08-01T20:00:00Z");
        var session = AddSession(entry.Id, observedBelowThreshold: true);

        await Service().DeleteAsync(_userId, entry.Id, CancellationToken.None);

        var reloaded = await _database.PlaybackSessions.AsNoTracking().SingleAsync(row => row.Id == session.Id);
        Assert.Null(reloaded.CompletedAt);
        Assert.Null(reloaded.HistoryEntryId);
        // Still a true observation about the session; deleting a play does not unmake it.
        Assert.True(reloaded.ObservedBelowThreshold);
    }

    [Fact]
    public async Task AnotherPlaysSessionIsLeftAlone()
    {
        var first = AddPlay("2026-08-01T20:00:00Z");
        var second = AddPlay("2026-08-02T21:00:00Z");
        var untouched = AddSession(first.Id, observedBelowThreshold: true);

        await Service().DeleteAsync(_userId, second.Id, CancellationToken.None);

        var reloaded = await _database.PlaybackSessions.AsNoTracking().SingleAsync(row => row.Id == untouched.Id);
        Assert.NotNull(reloaded.CompletedAt);
        Assert.Equal(first.Id, reloaded.HistoryEntryId);
    }

    // ---- Imported history ----

    [Fact]
    public async Task AnImportedEntryCanBeDeletedLocally()
    {
        // Once recorded locally, an imported play obeys the same deletion rules.
        var entry = AddPlay(
            "2026-08-01T20:00:00Z", origin: PlaybackHistoryOrigin.ProviderSync);

        await Service().DeleteAsync(_userId, entry.Id, CancellationToken.None);

    }

    // ---- Dating an undated mark ----

    [Fact]
    public async Task AnUndatedMarkTakesTheInstantItIsGiven()
    {
        // The fix for a real viewing that arrived without a time: the play was always there, only its
        // time was missing, so it is stamped rather than re-recorded.
        var mark = AddTimelessPlay();
        AddRow(playCount: 1, played: true, lastWatchedAt: null);
        var watchedAt = DateTimeOffset.Parse("2026-08-04T21:15:00Z");

        var status = await Service().SetWatchedAtAsync(_userId, mark.Id, watchedAt, CancellationToken.None);

        Assert.Equal(SetWatchedAtStatus.Updated, status);
        var entry = await _database.PlaybackHistoryEntries.AsNoTracking().SingleAsync();
        Assert.Equal(watchedAt, entry.WatchedAt);
        Assert.Equal(PlaybackHistoryOrigin.Manual, entry.Origin);
    }

    [Fact]
    public async Task DatingAMarkDoesNotChangeThePlayCount()
    {
        // Nothing was watched twice: a play that always existed simply became locatable in time.
        var mark = AddTimelessPlay();
        AddRow(playCount: 1, played: true, lastWatchedAt: null);

        await Service().SetWatchedAtAsync(_userId, mark.Id, DateTimeOffset.Parse("2026-08-04T21:15:00Z"), CancellationToken.None);

        var row = await _database.UserItemData.AsNoTracking().SingleAsync();
        Assert.Equal(1, row.PlayCount);
        Assert.True(row.Played);
    }

    [Fact]
    public async Task DatingAMarkTeachesTheRowWhenItWasWatched()
    {
        var mark = AddTimelessPlay();
        AddRow(playCount: 1, played: true, lastWatchedAt: null);
        var watchedAt = DateTimeOffset.Parse("2026-08-04T21:15:00Z");

        await Service().SetWatchedAtAsync(_userId, mark.Id, watchedAt, CancellationToken.None);

        var row = await _database.UserItemData.AsNoTracking().SingleAsync();
        Assert.Equal(watchedAt, row.LastWatchedAt);
    }

    [Fact]
    public async Task DatingAnOlderMarkLeavesTheLatestWatchAlone()
    {
        // Backfilling a viewing from years ago does not make it the most recent one.
        var mark = AddTimelessPlay();
        var latest = DateTimeOffset.Parse("2026-08-01T20:00:00Z");
        AddRow(playCount: 2, played: true, lastWatchedAt: latest);

        await Service().SetWatchedAtAsync(_userId, mark.Id, DateTimeOffset.Parse("2019-01-05T20:00:00Z"), CancellationToken.None);

        var row = await _database.UserItemData.AsNoTracking().SingleAsync();
        Assert.Equal(latest, row.LastWatchedAt);
    }

    [Fact]
    public async Task AnUnknownOrForeignMarkIsNotFoundAndUnchanged()
    {
        var theirs = AddTimelessPlay(appUserId: _otherUserId);
        var watchedAt = DateTimeOffset.Parse("2026-08-04T21:15:00Z");

        Assert.Equal(
            SetWatchedAtStatus.NotFound,
            await Service().SetWatchedAtAsync(_userId, Guid.NewGuid(), watchedAt, CancellationToken.None));
        Assert.Equal(
            SetWatchedAtStatus.NotFound,
            await Service().SetWatchedAtAsync(_userId, theirs.Id, watchedAt, CancellationToken.None));
        Assert.Null((await _database.PlaybackHistoryEntries.AsNoTracking().SingleAsync()).WatchedAt);
    }

    [Fact]
    public async Task AFutureInstantIsRefused()
    {
        var mark = AddTimelessPlay();

        var status = await Service().SetWatchedAtAsync(
            _userId, mark.Id, _time.GetUtcNow().AddHours(2), CancellationToken.None);

        Assert.Equal(SetWatchedAtStatus.FutureInstant, status);
        Assert.Null((await _database.PlaybackHistoryEntries.AsNoTracking().SingleAsync()).WatchedAt);
    }

    // ---- Correcting a play that already has a time ----

    [Fact]
    public async Task ADatedPlayMovesToTheInstantItIsGiven()
    {
        // A report can land at an instant the viewer does not recognise — a play left running, or a
        // viewing logged onto the wrong evening. The play is real; only its time was wrong.
        var play = AddPlay("2026-08-01T20:00:00Z");
        AddRow(playCount: 1, played: true, lastWatchedAt: DateTimeOffset.Parse("2026-08-01T20:00:00Z"));
        var corrected = DateTimeOffset.Parse("2026-07-30T18:30:00Z");

        var status = await Service().SetWatchedAtAsync(_userId, play.Id, corrected, CancellationToken.None);

        Assert.Equal(SetWatchedAtStatus.Updated, status);
        var entry = await _database.PlaybackHistoryEntries.AsNoTracking().SingleAsync();
        Assert.Equal(corrected, entry.WatchedAt);
        // Moved, not re-recorded: one play before, one play after.
        Assert.Equal(1, (await RowAsync()).PlayCount);
    }

    [Fact]
    public async Task CorrectingTheLatestPlayPullsTheRowBackWithIt()
    {
        // The row was pointing at this very play. Left where it is, the item would advertise an
        // instant nothing was watched at.
        var play = AddPlay("2026-08-01T20:00:00Z");
        AddRow(playCount: 1, played: true, lastWatchedAt: DateTimeOffset.Parse("2026-08-01T20:00:00Z"));
        var corrected = DateTimeOffset.Parse("2026-07-30T18:30:00Z");

        await Service().SetWatchedAtAsync(_userId, play.Id, corrected, CancellationToken.None);

        Assert.Equal(corrected, (await RowAsync()).LastWatchedAt);
    }

    [Fact]
    public async Task CorrectingTheLatestPlayHandsTheTitleToTheNextOne()
    {
        // Pulled back past a sibling, this is no longer the item's most recent viewing — that one is,
        // and the row has to say so rather than take the corrected instant.
        var play = AddPlay("2026-08-01T20:00:00Z");
        AddPlay("2026-07-31T19:00:00Z");
        AddRow(playCount: 2, played: true, lastWatchedAt: DateTimeOffset.Parse("2026-08-01T20:00:00Z"));

        await Service().SetWatchedAtAsync(
            _userId, play.Id, DateTimeOffset.Parse("2026-07-20T18:30:00Z"), CancellationToken.None);

        Assert.Equal(DateTimeOffset.Parse("2026-07-31T19:00:00Z"), (await RowAsync()).LastWatchedAt);
    }

    [Fact]
    public async Task CorrectingAnOlderPlayLeavesTheLatestWatchAlone()
    {
        // The row is pointing at a different, later viewing. Nothing about this correction unmakes it.
        var play = AddPlay("2026-07-20T20:00:00Z");
        var latest = DateTimeOffset.Parse("2026-08-01T20:00:00Z");
        AddRow(playCount: 2, played: true, lastWatchedAt: latest);

        await Service().SetWatchedAtAsync(
            _userId, play.Id, DateTimeOffset.Parse("2026-07-19T20:00:00Z"), CancellationToken.None);

        Assert.Equal(latest, (await RowAsync()).LastWatchedAt);
    }

    [Fact]
    public async Task TheInstantAPlayAlreadyCarriesChangesNothing()
    {
        // Re-confirming a time is not a correction. Queueing one would ask the provider to retire and
        // re-state the play for a change nobody made.
        var watchedAt = DateTimeOffset.Parse("2026-08-01T20:00:00Z");
        var play = AddPlay("2026-08-01T20:00:00Z");

        var status = await Service().SetWatchedAtAsync(_userId, play.Id, watchedAt, CancellationToken.None);

        Assert.Equal(SetWatchedAtStatus.Updated, status);
        var entry = await _database.PlaybackHistoryEntries.AsNoTracking().SingleAsync();
    }

    [Fact]
    public async Task AFuturePlayTimeIsRefused()
    {
        var play = AddPlay("2026-08-01T20:00:00Z");

        var status = await Service().SetWatchedAtAsync(
            _userId, play.Id, _time.GetUtcNow().AddHours(2), CancellationToken.None);

        Assert.Equal(SetWatchedAtStatus.FutureInstant, status);
        Assert.Equal(
            DateTimeOffset.Parse("2026-08-01T20:00:00Z"),
            (await _database.PlaybackHistoryEntries.AsNoTracking().SingleAsync()).WatchedAt);
    }

    // ---- Removed titles ----

    [Fact]
    public async Task DeletingTheLastPlayOfARemovedTitleTakesTheGhostWithIt()
    {
        // A removed title survives on its user signal alone. Once the calendar's last play of it is gone,
        // nothing is holding the row up and nothing can ever reach it again.
        var only = AddPlay("2026-08-01T20:00:00Z");
        AddRow(playCount: 1, played: true, lastWatchedAt: only.WatchedAt);
        Tombstone();

        Assert.True(await Service().DeleteAsync(_userId, only.Id, CancellationToken.None));

        Assert.False(await _database.MediaItems.AsNoTracking().AnyAsync(item => item.Id == _movie.Id));
        Assert.False(await _database.UserItemData.AsNoTracking().AnyAsync(row => row.MediaItemId == _movie.Id));
    }

    [Fact]
    public async Task ARemovedTitleSomeoneElseWatchedOutlivesThisUsersLastPlay()
    {
        // Signal is judged across every user, not the caller alone: purging here would erase someone
        // else's history because this user tidied up their own.
        var mine = AddPlay("2026-08-01T20:00:00Z");
        AddPlay("2026-08-02T20:00:00Z", appUserId: _otherUserId);
        Tombstone();

        Assert.True(await Service().DeleteAsync(_userId, mine.Id, CancellationToken.None));

        Assert.True(await _database.MediaItems.AsNoTracking().AnyAsync(item => item.Id == _movie.Id));
    }

    [Fact]
    public async Task AGhostLeafUnderALiveTitleIsJudgedOnItsOwn()
    {
        // A deleted episode of a series that is still published. The series is alive and none of this is
        // about it, so the ghost's own emptiness is what decides — otherwise the row would linger
        // forever, invisible and unreachable, until the whole series was deleted.
        var episodeId = SeedGhostEpisodeUnderALiveSeries(out var seriesId);
        var only = AddPlay("2026-08-01T20:00:00Z", itemId: episodeId);

        Assert.True(await Service().DeleteAsync(_userId, only.Id, CancellationToken.None));

        Assert.False(await _database.MediaItems.AsNoTracking().AnyAsync(item => item.Id == episodeId));
        Assert.True(await _database.MediaItems.AsNoTracking().AnyAsync(item => item.Id == seriesId));
    }

    private Guid SeedGhostEpisodeUnderALiveSeries(out Guid seriesId)
    {
        var now = _time.GetUtcNow();
        var series = new MediaItem
        {
            Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString("N"), CatalogId = _movie.CatalogId,
            Kind = MediaKind.Series, Title = "Dark", AddedAt = now, UpdatedAt = now,
        };
        var episode = new MediaItem
        {
            Id = Guid.NewGuid(), CatalogId = _movie.CatalogId, Kind = MediaKind.Episode, Title = "Secrets",
            SeriesId = series.Id, ParentId = series.Id, RemovedAt = now, AddedAt = now, UpdatedAt = now,
        };
        _database.MediaItems.AddRange(series, episode);
        _database.SaveChanges();
        seriesId = series.Id;
        return episode.Id;
    }

    [Fact]
    public async Task DeletingTheLastPlayOfAPublishedTitleLeavesTheTitleAlone()
    {
        var only = AddPlay("2026-08-01T20:00:00Z");
        AddRow(playCount: 1, played: true, lastWatchedAt: only.WatchedAt);

        Assert.True(await Service().DeleteAsync(_userId, only.Id, CancellationToken.None));

        Assert.True(await _database.MediaItems.AsNoTracking().AnyAsync(item => item.Id == _movie.Id));
    }

    /// <summary>Turns the seeded movie into a tombstone: unpublished, stamped, sourceless.</summary>
    private void Tombstone()
    {
        _movie.PublicId = null;
        _movie.RemovedAt = _time.GetUtcNow();
        _database.SaveChanges();
    }

    // ---- Helpers ----

    private WatchHistoryEntryService Service() => new(
        _database,
        new WatchHistoryRecorder(_database, _time),
        // Deleting the last play of a removed title takes the tombstone with it.
        new LibraryDeleteService(_database, new LibraryFileEraser(new CatalogPathSandbox(), NullLogger<LibraryFileEraser>.Instance)),
        _time);

    private AppUser NewUser(string hostUserId, string email) => new()
    {
        HostUserId = hostUserId, Email = email, DisplayName = email, Role = AppUserRole.User,
        CreatedAt = _time.GetUtcNow(), LastSeenAt = _time.GetUtcNow(),
    };

    /// <summary>A second item, optionally one the identity mapper cannot resolve.</summary>
    private MediaItem AddItem(bool identified)
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString("N"), CatalogId = _movie.CatalogId,
            Kind = MediaKind.Movie, Title = "Solaris",
            IdentityProvider = identified ? "tmdb" : null,
            IdentityProviderId = identified ? "1000" : null,
            AddedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow(),
        };
        _database.MediaItems.Add(item);
        _database.SaveChanges();
        return item;
    }

    private PlaybackSession AddSession(Guid historyEntryId, bool observedBelowThreshold)
    {
        var session = new PlaybackSession
        {
            Id = Guid.NewGuid(),
            AppUserId = _userId,
            MediaItemId = _movie.Id,
            SessionKey = $"session-{historyEntryId:N}",
            StartedAt = _time.GetUtcNow(),
            LastReportAt = _time.GetUtcNow(),
            ObservedBelowThreshold = observedBelowThreshold,
            CompletedAt = _time.GetUtcNow(),
            HistoryEntryId = historyEntryId,
        };
        _database.PlaybackSessions.Add(session);
        _database.SaveChanges();
        return session;
    }

    private PlaybackHistoryEntry AddPlay(
        string watchedAt,
        int? appUserId = null,
        Guid? itemId = null,
        PlaybackHistoryOrigin origin = PlaybackHistoryOrigin.LocalPlayback)
    {
        var entry = new PlaybackHistoryEntry
        {
            Id = Guid.NewGuid(),
            AppUserId = appUserId ?? _userId,
            MediaItemId = itemId ?? _movie.Id,
            CreatedAt = _time.GetUtcNow(),
            WatchedAt = DateTimeOffset.Parse(watchedAt),
            Origin = origin,
        };
        _database.PlaybackHistoryEntries.Add(entry);
        _database.SaveChanges();
        return entry;
    }

    private PlaybackHistoryEntry AddTimelessPlay(
        int? appUserId = null)
    {
        var entry = new PlaybackHistoryEntry
        {
            Id = Guid.NewGuid(),
            AppUserId = appUserId ?? _userId,
            MediaItemId = _movie.Id,
            CreatedAt = _time.GetUtcNow(),
            WatchedAt = null,
            Origin = PlaybackHistoryOrigin.Manual,
        };
        _database.PlaybackHistoryEntries.Add(entry);
        _database.SaveChanges();
        return entry;
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
