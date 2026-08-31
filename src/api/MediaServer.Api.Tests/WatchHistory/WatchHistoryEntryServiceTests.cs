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
/// what the aggregates become, and what the provider is — and is not — asked to change.
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
    public async Task AnOwnedEntryIsStillRemovedWhenItsItemCanNoLongerBeIdentified()
    {
        // The removal is addressed by the remote id alone. Refusing it because the item has since been
        // re-identified or lost its metadata would leave the remote entry behind — and the next sync
        // would re-import the very play the user deleted.
        Connect();
        var unidentifiable = AddItem(identified: false);
        var entry = AddPlay("2026-08-01T20:00:00Z", itemId: unidentifiable.Id, remoteId: "111", owned: true);

        await Service().DeleteAsync(_userId, entry.Id, CancellationToken.None);

        var queued = Assert.Single(await _database.WatchHistoryOutboxEvents.AsNoTracking().ToListAsync());
        Assert.Equal(WatchHistoryOutboxOperation.RemoveOwnedEntries, queued.Operation);
        Assert.Contains("111", queued.RemoteIdSnapshot);
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

    [Fact]
    public async Task DatingAnOwnedMarkRetiresItRemotelyAndRestatesItAsAnExactPlay()
    {
        // The provider holds this play as timeless. Adding the exact one without removing that mark
        // would leave the account with the same viewing twice — and the next sync would import the
        // timeless one straight back into the undated list the user just emptied.
        Connect();
        var mark = AddTimelessPlay(remoteId: "111", owned: true);
        AddRow(playCount: 1, played: true, lastWatchedAt: null);
        var watchedAt = DateTimeOffset.Parse("2026-08-04T21:15:00Z");

        await Service().SetWatchedAtAsync(_userId, mark.Id, watchedAt, CancellationToken.None);

        var queued = await _database.WatchHistoryOutboxEvents.AsNoTracking().ToListAsync();
        Assert.Equal(2, queued.Count);
        var removal = Assert.Single(queued, item => item.Operation == WatchHistoryOutboxOperation.RemoveOwnedEntries);
        Assert.Contains("111", removal.RemoteIdSnapshot);
        var add = Assert.Single(queued, item => item.Operation == WatchHistoryOutboxOperation.AddExactWatch);
        Assert.Equal(watchedAt, add.OccurredAt);
    }

    [Fact]
    public async Task DatingAnOwnedMarkDropsTheLinkItNoLongerHas()
    {
        // The remote entry is being removed, so the local one must stop naming it: left in place, a
        // later deletion of this play would ask the provider to remove an id that is already gone.
        Connect();
        var mark = AddTimelessPlay(remoteId: "111", owned: true);

        await Service().SetWatchedAtAsync(_userId, mark.Id, DateTimeOffset.Parse("2026-08-04T21:15:00Z"), CancellationToken.None);

        var entry = await _database.PlaybackHistoryEntries.AsNoTracking().SingleAsync();
        Assert.False(entry.ProviderEntryOwned);
        Assert.Null(entry.ProviderHistoryId);
        Assert.Equal(PlaybackHistoryLinkStatus.None, entry.LinkStatus);
    }

    [Fact]
    public async Task DatingAnUnownedMarkRemovesNothingRemotely()
    {
        // Nothing here is this app's to delete — an unresolved add, or a mark another client made — so
        // the exact play is stated and the remote mark is left alone.
        Connect();
        var mark = AddTimelessPlay(remoteId: "111", owned: true, link: PlaybackHistoryLinkStatus.Unresolved);

        await Service().SetWatchedAtAsync(_userId, mark.Id, DateTimeOffset.Parse("2026-08-04T21:15:00Z"), CancellationToken.None);

        var queued = Assert.Single(await _database.WatchHistoryOutboxEvents.AsNoTracking().ToListAsync());
        Assert.Equal(WatchHistoryOutboxOperation.AddExactWatch, queued.Operation);
    }

    [Fact]
    public async Task WithoutAConnectionDatingAMarkQueuesNothing()
    {
        var mark = AddTimelessPlay(remoteId: "111", owned: true);
        AddRow(playCount: 1, played: true, lastWatchedAt: null);

        await Service().SetWatchedAtAsync(_userId, mark.Id, DateTimeOffset.Parse("2026-08-04T21:15:00Z"), CancellationToken.None);

        Assert.Empty(_database.WatchHistoryOutboxEvents);
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
        Connect();
        var watchedAt = DateTimeOffset.Parse("2026-08-01T20:00:00Z");
        var play = AddPlay("2026-08-01T20:00:00Z", remoteId: "111", owned: true);

        var status = await Service().SetWatchedAtAsync(_userId, play.Id, watchedAt, CancellationToken.None);

        Assert.Equal(SetWatchedAtStatus.Updated, status);
        Assert.Empty(_database.WatchHistoryOutboxEvents);
        var entry = await _database.PlaybackHistoryEntries.AsNoTracking().SingleAsync();
        Assert.Equal("111", entry.ProviderHistoryId);
    }

    [Fact]
    public async Task CorrectingAnOwnedPlayRetiresItRemotelyAndRestatesItAtTheNewTime()
    {
        Connect();
        var play = AddPlay("2026-08-01T20:00:00Z", remoteId: "111", owned: true);
        var corrected = DateTimeOffset.Parse("2026-07-30T18:30:00Z");

        await Service().SetWatchedAtAsync(_userId, play.Id, corrected, CancellationToken.None);

        var queued = await _database.WatchHistoryOutboxEvents.AsNoTracking().ToListAsync();
        Assert.Equal(2, queued.Count);
        var removal = Assert.Single(queued, item => item.Operation == WatchHistoryOutboxOperation.RemoveOwnedEntries);
        Assert.Contains("111", removal.RemoteIdSnapshot);
        var add = Assert.Single(queued, item => item.Operation == WatchHistoryOutboxOperation.AddExactWatch);
        Assert.Equal(corrected, add.OccurredAt);
    }

    [Fact]
    public async Task CorrectingAPlayTheProviderMayHoldQueuesNothing()
    {
        // Nothing here can retire the remote copy — an exact add never resolves its id — so stating the
        // new time would leave the account with the same viewing twice, and the next explicit sync
        // would import the stale one back as another local play. The correction stays local.
        Connect();
        var play = AddPlay("2026-08-01T20:00:00Z");
        AddRow(playCount: 1, played: true, lastWatchedAt: DateTimeOffset.Parse("2026-08-01T20:00:00Z"));
        var corrected = DateTimeOffset.Parse("2026-07-30T18:30:00Z");

        await Service().SetWatchedAtAsync(_userId, play.Id, corrected, CancellationToken.None);

        Assert.Empty(_database.WatchHistoryOutboxEvents);
        // Local history still moved: the provider's limits are not the user's problem here.
        Assert.Equal(corrected, (await _database.PlaybackHistoryEntries.AsNoTracking().SingleAsync()).WatchedAt);
    }

    [Fact]
    public async Task CorrectingAPlayWhoseAddWasNeverSentReplacesThatClaim()
    {
        // The queued add has never been attempted, so the provider has not seen it. Dropping it is the
        // one way a correction can supersede an earlier claim without risking a duplicate.
        Connect();
        var play = AddPlay("2026-08-01T20:00:00Z");
        var queued = QueueAdd(play, DateTimeOffset.Parse("2026-08-01T20:00:00Z"), attempts: 0);
        var corrected = DateTimeOffset.Parse("2026-07-30T18:30:00Z");

        await Service().SetWatchedAtAsync(_userId, play.Id, corrected, CancellationToken.None);

        var add = Assert.Single(await _database.WatchHistoryOutboxEvents.AsNoTracking().ToListAsync());
        Assert.Equal(WatchHistoryOutboxOperation.AddExactWatch, add.Operation);
        Assert.Equal(corrected, add.OccurredAt);
        Assert.NotEqual(queued.Id, add.Id);
    }

    [Fact]
    public async Task AnAddAlreadyAttemptedIsLeftAloneAndNotReplaced()
    {
        // An attempt may have reached the provider before the process died — which is why delivery
        // re-reads history on a retry rather than re-posting. Replacing it here would be guessing.
        Connect();
        var play = AddPlay("2026-08-01T20:00:00Z");
        var queued = QueueAdd(play, DateTimeOffset.Parse("2026-08-01T20:00:00Z"), attempts: 1);

        await Service().SetWatchedAtAsync(
            _userId, play.Id, DateTimeOffset.Parse("2026-07-30T18:30:00Z"), CancellationToken.None);

        var remaining = Assert.Single(await _database.WatchHistoryOutboxEvents.AsNoTracking().ToListAsync());
        Assert.Equal(queued.Id, remaining.Id);
        Assert.Equal(DateTimeOffset.Parse("2026-08-01T20:00:00Z"), remaining.OccurredAt);
    }

    [Fact]
    public async Task CorrectingAPlayTwiceBeforeDeliveryStatesOnlyTheLatestTime()
    {
        // Each correction supersedes the untried one before it, so the provider is never told a time
        // the user has already replaced — and the second add is not swallowed as a duplicate of the
        // first, which an entry-only idempotency key would do.
        Connect();
        var mark = AddTimelessPlay(remoteId: "111", owned: true);
        var first = DateTimeOffset.Parse("2026-07-30T18:30:00Z");
        var second = DateTimeOffset.Parse("2026-07-29T21:00:00Z");

        await Service().SetWatchedAtAsync(_userId, mark.Id, first, CancellationToken.None);
        await Service().SetWatchedAtAsync(_userId, mark.Id, second, CancellationToken.None);

        var add = Assert.Single(
            await _database.WatchHistoryOutboxEvents.AsNoTracking().ToListAsync(),
            item => item.Operation == WatchHistoryOutboxOperation.AddExactWatch);
        Assert.Equal(second, add.OccurredAt);
        // The timeless mark's removal still stands: that remote entry has to go whatever time the play
        // ends up carrying.
        Assert.Single(
            await _database.WatchHistoryOutboxEvents.AsNoTracking().ToListAsync(),
            item => item.Operation == WatchHistoryOutboxOperation.RemoveOwnedEntries);
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
        new WatchHistoryRecorder(
            _database,
            new WatchHistoryIdentityMapper(_database),
            _time,
            NullLogger<WatchHistoryRecorder>.Instance),
        // Deleting the last play of a removed title takes the tombstone with it.
        new LibraryDeleteService(_database, new LibraryFileEraser(new CatalogPathSandbox(), NullLogger<LibraryFileEraser>.Instance)),
        _time);

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
        string? remoteId = null,
        bool owned = false,
        PlaybackHistoryOrigin origin = PlaybackHistoryOrigin.LocalPlayback,
        PlaybackHistoryLinkStatus link = PlaybackHistoryLinkStatus.Resolved)
    {
        var entry = new PlaybackHistoryEntry
        {
            Id = Guid.NewGuid(),
            AppUserId = appUserId ?? _userId,
            MediaItemId = itemId ?? _movie.Id,
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

    private PlaybackHistoryEntry AddTimelessPlay(
        int? appUserId = null,
        string? remoteId = null,
        bool owned = false,
        PlaybackHistoryLinkStatus link = PlaybackHistoryLinkStatus.Resolved)
    {
        var entry = new PlaybackHistoryEntry
        {
            Id = Guid.NewGuid(),
            AppUserId = appUserId ?? _userId,
            MediaItemId = _movie.Id,
            CreatedAt = _time.GetUtcNow(),
            WatchedAt = null,
            Origin = PlaybackHistoryOrigin.Manual,
            ProviderKey = remoteId is null ? null : "trakt",
            ProviderHistoryId = remoteId,
            ProviderEntryOwned = owned,
            LinkStatus = remoteId is null ? PlaybackHistoryLinkStatus.None : link,
        };
        _database.PlaybackHistoryEntries.Add(entry);
        _database.SaveChanges();
        return entry;
    }

    /// <summary>An add already queued for one entry, at whatever stage of delivery a test needs.</summary>
    private WatchHistoryOutboxEvent QueueAdd(PlaybackHistoryEntry entry, DateTimeOffset occurredAt, int attempts)
    {
        var queued = new WatchHistoryOutboxEvent
        {
            Id = Guid.NewGuid(),
            ConnectionId = _database.WatchHistoryConnections.Single(link => link.AppUserId == _userId).Id,
            AppUserId = _userId,
            MediaItemId = entry.MediaItemId,
            HistoryEntryId = entry.Id,
            Operation = WatchHistoryOutboxOperation.AddExactWatch,
            OccurredAt = occurredAt,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            Status = WatchHistoryOutboxStatus.Pending,
            Attempts = attempts,
            CreatedAt = _time.GetUtcNow(),
            NextAttemptAt = _time.GetUtcNow(),
        };
        _database.WatchHistoryOutboxEvents.Add(queued);
        _database.SaveChanges();
        return queued;
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
