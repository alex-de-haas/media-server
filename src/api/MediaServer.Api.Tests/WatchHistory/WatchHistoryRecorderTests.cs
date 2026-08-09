using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Tests.Jellyfin;
using MediaServer.Api.WatchHistory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.WatchHistory;

/// <summary>
/// Recording per-play history and the outbound intent that follows it, through the real
/// <see cref="UserDataService"/> paths so the staging-and-one-commit contract is exercised end to end.
/// </summary>
public sealed class WatchHistoryRecorderTests : IDisposable
{
    private const long Runtime = 60L * 60 * 10_000_000;

    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly TestTimeProvider _time = new(DateTimeOffset.Parse("2026-07-23T12:00:00Z"));
    private readonly int _userId;
    private readonly Guid _movieId;
    private readonly string _moviePublicId;
    private readonly Guid _seasonId;
    private readonly string _seasonPublicId;

    public WatchHistoryRecorderTests()
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
            CreatedAt = _time.GetUtcNow(),
            LastSeenAt = _time.GetUtcNow(),
        };
        _database.AppUsers.Add(user);

        var movieCatalog = Guid.NewGuid();
        var seriesCatalog = Guid.NewGuid();
        _database.Catalogs.AddRange(
            new Catalog { Id = movieCatalog, Name = "Movies", Type = CatalogType.Movie, Root = "/m", CreatedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow() },
            new Catalog { Id = seriesCatalog, Name = "Shows", Type = CatalogType.Series, Root = "/s", CreatedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow() });

        var movie = NewItem(MediaKind.Movie, movieCatalog, "Inception");
        movie.IdentityProvider = "tmdb";
        movie.IdentityProviderId = "27205";
        _database.MediaItems.Add(movie);
        _database.MediaSources.Add(new MediaSource
        {
            Id = Guid.NewGuid(), MediaItemId = movie.Id, Container = "mkv", Path = "/m/a.mkv",
            SizeBytes = 1, DurationTicks = Runtime, CreatedAt = _time.GetUtcNow(),
        });

        var series = NewItem(MediaKind.Series, seriesCatalog, "Futurama");
        series.IdentityProvider = "tmdb";
        series.IdentityProviderId = "615";
        var season = NewItem(MediaKind.Season, seriesCatalog, "Season 1");
        season.ParentId = series.Id;
        season.SeriesId = series.Id;
        season.IndexNumber = 1;
        _database.MediaItems.AddRange(series, season);

        foreach (var number in new[] { 1, 2 })
        {
            var episode = NewItem(MediaKind.Episode, seriesCatalog, $"Episode {number}");
            episode.ParentId = season.Id;
            episode.SeasonId = season.Id;
            episode.SeriesId = series.Id;
            episode.IndexNumber = number;
            episode.ParentIndexNumber = 1;
            episode.IdentityProvider = "tmdb";
            episode.IdentityProviderId = "615";
            episode.IdentitySeasonNumber = 1;
            episode.IdentityEpisodeNumber = number;
            _database.MediaItems.Add(episode);
        }

        _database.SaveChanges();

        _userId = user.Id;
        _movieId = movie.Id;
        _moviePublicId = movie.PublicId!;
        _seasonId = season.Id;
        _seasonPublicId = season.PublicId!;
    }

    private MediaItem NewItem(MediaKind kind, Guid catalogId, string title) => new()
    {
        Id = Guid.NewGuid(),
        PublicId = Guid.NewGuid().ToString("N"),
        CatalogId = catalogId,
        Kind = kind,
        Title = title,
        AddedAt = _time.GetUtcNow(),
        UpdatedAt = _time.GetUtcNow(),
    };

    private UserDataService Service() => new(
        _database,
        _time,
        new WatchHistoryRecorder(
            _database,
            new WatchHistoryIdentityMapper(_database),
            _time,
            NullLogger<WatchHistoryRecorder>.Instance));

    private WatchHistoryProviderConnection Connect()
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
        return connection;
    }

    private async Task WatchToCompletionAsync(string session = "session-1")
    {
        await Service().ReportPlaybackAsync(_userId, _moviePublicId, (long)(Runtime * 0.5), false, session, null, CancellationToken.None);
        await Service().ReportPlaybackAsync(_userId, _moviePublicId, (long)(Runtime * 0.95), false, session, null, CancellationToken.None);
    }

    // ---- Completion ----

    [Fact]
    public async Task AProvenCompletionRecordsOneExactPlay()
    {
        await WatchToCompletionAsync();

        var entry = await _database.PlaybackHistoryEntries.AsNoTracking().SingleAsync();
        Assert.Equal(PlaybackHistoryOrigin.LocalPlayback, entry.Origin);
        Assert.Equal(_time.GetUtcNow(), entry.WatchedAt);
        Assert.Equal("session-1", entry.PlaySessionId);
        Assert.Contains("27205", entry.IdentitySnapshot);
    }

    [Fact]
    public async Task TheSessionGateIsLinkedToTheEntryItCreated()
    {
        // So a restart or a repeated report reuses the completion rather than re-deriving it.
        await WatchToCompletionAsync();

        var entry = await _database.PlaybackHistoryEntries.AsNoTracking().SingleAsync();
        var session = await _database.PlaybackSessions.AsNoTracking().SingleAsync();
        Assert.Equal(entry.Id, session.HistoryEntryId);
    }

    [Fact]
    public async Task ARewindAndSecondCrossingStillRecordsOnePlay()
    {
        // The session gate governs history too, not just the counter.
        await WatchToCompletionAsync();
        await Service().ReportPlaybackAsync(_userId, _moviePublicId, (long)(Runtime * 0.5), false, "session-1", null, CancellationToken.None);
        await Service().ReportPlaybackAsync(_userId, _moviePublicId, (long)(Runtime * 0.95), false, "session-1", null, CancellationToken.None);

        Assert.Single(_database.PlaybackHistoryEntries);
    }

    [Fact]
    public async Task ASecondSessionRecordsASecondPlay()
    {
        await WatchToCompletionAsync("session-a");
        await WatchToCompletionAsync("session-b");

        Assert.Equal(2, await _database.PlaybackHistoryEntries.CountAsync());
    }

    [Fact]
    public async Task TheProjectionFieldsFollowTheCompletion()
    {
        await WatchToCompletionAsync();

        var row = await _database.UserItemData.AsNoTracking().SingleAsync(data => data.MediaItemId == _movieId);
        Assert.Equal(_time.GetUtcNow(), row.LastWatchedAt);
        Assert.NotNull(row.WatchedStateChangedAt);
        Assert.True(row.StateRevision > 0);
    }

    // ---- Outbound intent ----

    [Fact]
    public async Task WithoutAConnectionHistoryIsStillRecordedButNothingIsQueued()
    {
        // The history is the local source of truth; connecting later has to have something to export.
        await WatchToCompletionAsync();

        Assert.Single(_database.PlaybackHistoryEntries);
        Assert.Empty(_database.WatchHistoryOutboxEvents);
    }

    [Fact]
    public async Task AConnectedUsersCompletionQueuesAnExactWatch()
    {
        var connection = Connect();

        await WatchToCompletionAsync();

        var queued = await _database.WatchHistoryOutboxEvents.AsNoTracking().SingleAsync();
        Assert.Equal(WatchHistoryOutboxOperation.AddExactWatch, queued.Operation);
        Assert.Equal(connection.Id, queued.ConnectionId);
        Assert.Equal(_time.GetUtcNow(), queued.OccurredAt);
        Assert.Contains("27205", queued.IdentitySnapshot);
    }

    [Fact]
    public async Task TheHistoryEntryAndItsOutboxEventCommitTogether()
    {
        // Both are staged and saved by the same SaveChangesAsync, so neither can exist alone.
        Connect();

        await WatchToCompletionAsync();

        var entry = await _database.PlaybackHistoryEntries.AsNoTracking().SingleAsync();
        var queued = await _database.WatchHistoryOutboxEvents.AsNoTracking().SingleAsync();
        Assert.Equal(entry.Id, queued.HistoryEntryId);
    }

    [Fact]
    public async Task AnUnidentifiedItemRecordsHistoryWithoutQueueingUndeliverableWork()
    {
        // Queueing work that can never be addressed would retry forever; the local change still stands.
        Connect();
        var unidentified = NewItem(MediaKind.Movie, (await _database.Catalogs.FirstAsync()).Id, "Unknown");
        _database.MediaItems.Add(unidentified);
        _database.MediaSources.Add(new MediaSource
        {
            Id = Guid.NewGuid(), MediaItemId = unidentified.Id, Container = "mkv", Path = "/m/u.mkv",
            SizeBytes = 1, DurationTicks = Runtime, CreatedAt = _time.GetUtcNow(),
        });
        await _database.SaveChangesAsync();

        await Service().ReportPlaybackAsync(_userId, unidentified.PublicId!, (long)(Runtime * 0.5), false, "s", null, CancellationToken.None);
        await Service().ReportPlaybackAsync(_userId, unidentified.PublicId!, (long)(Runtime * 0.95), false, "s", null, CancellationToken.None);

        Assert.Single(_database.PlaybackHistoryEntries);
        Assert.Empty(_database.WatchHistoryOutboxEvents);
    }

    // ---- Manual marks ----

    [Fact]
    public async Task AManualMarkRecordsOneTimelessPlay()
    {
        // Null, not "now": the mark says the item was watched, not when.
        await Service().SetPlayedAsync(_userId, _moviePublicId, played: true, playedAt: null, CancellationToken.None);

        var entry = await _database.PlaybackHistoryEntries.AsNoTracking().SingleAsync();
        Assert.Equal(PlaybackHistoryOrigin.Manual, entry.Origin);
        Assert.Null(entry.WatchedAt);
    }

    [Fact]
    public async Task MarkingTwiceDoesNotAddASecondTimelessPlay()
    {
        await Service().SetPlayedAsync(_userId, _moviePublicId, played: true, playedAt: null, CancellationToken.None);
        await Service().SetPlayedAsync(_userId, _moviePublicId, played: true, playedAt: null, CancellationToken.None);

        Assert.Single(_database.PlaybackHistoryEntries);
    }

    [Fact]
    public async Task AMarkAfterARealPlayAddsNoTimelessEntry()
    {
        // The flag says nothing about how many times something was seen; a toggle is not a viewing.
        await WatchToCompletionAsync();
        await Service().SetPlayedAsync(_userId, _moviePublicId, played: false, playedAt: null, CancellationToken.None);
        await Service().SetPlayedAsync(_userId, _moviePublicId, played: true, playedAt: null, CancellationToken.None);

        var entry = Assert.Single(_database.PlaybackHistoryEntries);
        Assert.Equal(PlaybackHistoryOrigin.LocalPlayback, entry.Origin);
    }

    [Fact]
    public async Task AManualMarkQueuesEnsureTimelessWatched()
    {
        Connect();

        await Service().SetPlayedAsync(_userId, _moviePublicId, played: true, playedAt: null, CancellationToken.None);

        var queued = await _database.WatchHistoryOutboxEvents.AsNoTracking().SingleAsync();
        Assert.Equal(WatchHistoryOutboxOperation.EnsureTimelessWatched, queued.Operation);
        Assert.Null(queued.OccurredAt);
    }

    // ---- Logged watches ----

    [Fact]
    public async Task ALoggedWatchRecordsOneDatedPlay()
    {
        // The whole point: a viewing the server never observed still lands on a day of the calendar.
        var watchedAt = DateTimeOffset.Parse("2026-07-20T21:30:00Z");

        var result = await Service().LogWatchAsync(_userId, _movieId, watchedAt, CancellationToken.None);

        Assert.Equal(LogWatchStatus.Recorded, result.Status);
        var entry = await _database.PlaybackHistoryEntries.AsNoTracking().SingleAsync();
        Assert.Equal(PlaybackHistoryOrigin.Manual, entry.Origin);
        Assert.Equal(watchedAt, entry.WatchedAt);
        Assert.Null(entry.PlaySessionId);
        Assert.Contains("27205", entry.IdentitySnapshot);
    }

    [Fact]
    public async Task LoggingTwiceRecordsTwoPlays()
    {
        // Unlike the toggle, which is idempotent: this is a claim about a viewing, and two claims are
        // two viewings — a rewatch is exactly what the second one is.
        await Service().LogWatchAsync(_userId, _movieId, DateTimeOffset.Parse("2026-07-20T21:30:00Z"), CancellationToken.None);
        await Service().LogWatchAsync(_userId, _movieId, DateTimeOffset.Parse("2026-07-22T18:00:00Z"), CancellationToken.None);

        Assert.Equal(2, await _database.PlaybackHistoryEntries.CountAsync());
        var row = await _database.UserItemData.AsNoTracking().SingleAsync(data => data.MediaItemId == _movieId);
        Assert.Equal(2, row.PlayCount);
        Assert.True(row.Played);
    }

    [Fact]
    public async Task EachLoggedWatchQueuesItsOwnExactWatch()
    {
        // Keyed on the entry: a second log changes no state on the row, so a row-derived idempotency
        // key would collide and the second event would be swallowed as a duplicate.
        Connect();
        var first = DateTimeOffset.Parse("2026-07-20T21:30:00Z");
        var second = DateTimeOffset.Parse("2026-07-22T18:00:00Z");

        await Service().LogWatchAsync(_userId, _movieId, first, CancellationToken.None);
        await Service().LogWatchAsync(_userId, _movieId, second, CancellationToken.None);

        var queued = await _database.WatchHistoryOutboxEvents.AsNoTracking().ToListAsync();
        Assert.Equal(2, queued.Count);
        Assert.All(queued, item => Assert.Equal(WatchHistoryOutboxOperation.AddExactWatch, item.Operation));
        Assert.Contains(queued, item => item.OccurredAt == first);
        Assert.Contains(queued, item => item.OccurredAt == second);
    }

    [Fact]
    public async Task ABackdatedLogNeverMovesTheAggregatesBackwards()
    {
        // Logging a viewing from years ago must not rewrite "last watched" to years ago.
        await WatchToCompletionAsync();
        var completedAt = _time.GetUtcNow();

        await Service().LogWatchAsync(_userId, _movieId, DateTimeOffset.Parse("2019-01-05T20:00:00Z"), CancellationToken.None);

        var row = await _database.UserItemData.AsNoTracking().SingleAsync(data => data.MediaItemId == _movieId);
        Assert.Equal(completedAt, row.LastWatchedAt);
        Assert.Equal(completedAt, row.LastPlayedDate);
        Assert.Equal(2, row.PlayCount);
    }

    [Fact]
    public async Task ALaterLogAdvancesTheAggregates()
    {
        var watchedAt = DateTimeOffset.Parse("2026-07-22T18:00:00Z");

        await Service().LogWatchAsync(_userId, _movieId, watchedAt, CancellationToken.None);

        var row = await _database.UserItemData.AsNoTracking().SingleAsync(data => data.MediaItemId == _movieId);
        Assert.Equal(watchedAt, row.LastWatchedAt);
        Assert.Equal(watchedAt, row.LastPlayedDate);
    }

    [Fact]
    public async Task TheWatchedFlagChangesOnceHoweverManyPlaysAreLogged()
    {
        // WatchedStateChangedAt is the idempotency discriminator for manual marks; bumping it without a
        // transition would let a later mark-watched queue an event for a click that changed nothing.
        await Service().LogWatchAsync(_userId, _movieId, DateTimeOffset.Parse("2026-07-20T21:30:00Z"), CancellationToken.None);
        var changedAt = (await _database.UserItemData.AsNoTracking().SingleAsync(data => data.MediaItemId == _movieId))
            .WatchedStateChangedAt;
        _time.Advance(TimeSpan.FromHours(1));

        await Service().LogWatchAsync(_userId, _movieId, DateTimeOffset.Parse("2026-07-22T18:00:00Z"), CancellationToken.None);

        var row = await _database.UserItemData.AsNoTracking().SingleAsync(data => data.MediaItemId == _movieId);
        Assert.Equal(changedAt, row.WatchedStateChangedAt);
    }

    [Fact]
    public async Task ABackdatedLogLeavesTheResumePointAlone()
    {
        // The user may be halfway through the film right now; recording that they also saw it in 2019
        // is no reason to throw that position away.
        await Service().ReportPlaybackAsync(_userId, _moviePublicId, (long)(Runtime * 0.5), true, "session-1", null, CancellationToken.None);

        await Service().LogWatchAsync(_userId, _movieId, DateTimeOffset.Parse("2019-01-05T20:00:00Z"), CancellationToken.None);

        var row = await _database.UserItemData.AsNoTracking().SingleAsync(data => data.MediaItemId == _movieId);
        Assert.Equal((long)(Runtime * 0.5), row.PlaybackPositionTicks);
    }

    [Fact]
    public async Task ALogOfTheLatestViewingClearsTheResumePoint()
    {
        await Service().ReportPlaybackAsync(_userId, _moviePublicId, (long)(Runtime * 0.5), true, "session-1", null, CancellationToken.None);

        await Service().LogWatchAsync(_userId, _movieId, _time.GetUtcNow(), CancellationToken.None);

        var row = await _database.UserItemData.AsNoTracking().SingleAsync(data => data.MediaItemId == _movieId);
        Assert.Equal(0, row.PlaybackPositionTicks);
    }

    [Fact]
    public async Task AFolderCannotHaveAWatchLogged()
    {
        // Marking a season is a fan-out over episodes; logging one viewing against the folder itself is
        // a different gesture and not this one.
        var result = await Service().LogWatchAsync(_userId, _seasonId, _time.GetUtcNow(), CancellationToken.None);

        Assert.Equal(LogWatchStatus.NotPlayable, result.Status);
        Assert.Empty(_database.PlaybackHistoryEntries);
    }

    [Fact]
    public async Task AnInstantInTheFutureIsRefused()
    {
        var result = await Service().LogWatchAsync(_userId, _movieId, _time.GetUtcNow().AddHours(2), CancellationToken.None);

        Assert.Equal(LogWatchStatus.FutureInstant, result.Status);
        Assert.Empty(_database.PlaybackHistoryEntries);
    }

    [Fact]
    public async Task AClockSkewedNowIsStillAccepted()
    {
        // The instant is composed from the browser's clock; refusing one a minute ahead of ours would
        // fail the most common action there is.
        var result = await Service().LogWatchAsync(_userId, _movieId, _time.GetUtcNow().AddMinutes(1), CancellationToken.None);

        Assert.Equal(LogWatchStatus.Recorded, result.Status);
    }

    [Fact]
    public async Task AnUnknownItemIsNotFound()
    {
        var result = await Service().LogWatchAsync(_userId, Guid.NewGuid(), _time.GetUtcNow(), CancellationToken.None);

        Assert.Equal(LogWatchStatus.ItemNotFound, result.Status);
        Assert.Empty(_database.PlaybackHistoryEntries);
    }

    [Fact]
    public async Task AnUnidentifiedItemStillRecordsTheLoggedPlay()
    {
        // Same rule as an observed completion: history is local truth, and undeliverable work is not
        // queued rather than retried forever.
        Connect();
        var unidentified = NewItem(MediaKind.Movie, (await _database.Catalogs.FirstAsync()).Id, "Unknown");
        _database.MediaItems.Add(unidentified);
        await _database.SaveChangesAsync();

        var result = await Service().LogWatchAsync(_userId, unidentified.Id, _time.GetUtcNow(), CancellationToken.None);

        Assert.Equal(LogWatchStatus.Recorded, result.Status);
        Assert.Single(_database.PlaybackHistoryEntries);
        Assert.Empty(_database.WatchHistoryOutboxEvents);
    }

    // ---- Unwatch ----

    [Fact]
    public async Task UnwatchLeavesALoggedPlayAlone()
    {
        // A logged play is a viewing that happened, exactly like an observed one. Unwatch is a statement
        // about current state and drops only the timeless marks this app created.
        await Service().LogWatchAsync(_userId, _movieId, DateTimeOffset.Parse("2026-07-20T21:30:00Z"), CancellationToken.None);

        await Service().SetPlayedAsync(_userId, _moviePublicId, played: false, playedAt: null, CancellationToken.None);

        var entry = Assert.Single(_database.PlaybackHistoryEntries);
        Assert.NotNull(entry.WatchedAt);
        var row = await _database.UserItemData.AsNoTracking().SingleAsync(data => data.MediaItemId == _movieId);
        Assert.False(row.Played);
        Assert.Equal(1, row.PlayCount);
    }

    [Fact]
    public async Task UnwatchDropsOnlyTheTimelessEntriesThisAppCreated()
    {
        // Exact plays and imported history survive: unwatch is a statement about current state, not a
        // claim that the viewings never happened.
        await WatchToCompletionAsync();
        _database.PlaybackHistoryEntries.Add(new PlaybackHistoryEntry
        {
            Id = Guid.NewGuid(), AppUserId = _userId, MediaItemId = _movieId,
            CreatedAt = _time.GetUtcNow(), WatchedAt = null,
            Origin = PlaybackHistoryOrigin.Manual, LinkStatus = PlaybackHistoryLinkStatus.None,
        });
        _database.PlaybackHistoryEntries.Add(new PlaybackHistoryEntry
        {
            Id = Guid.NewGuid(), AppUserId = _userId, MediaItemId = _movieId,
            CreatedAt = _time.GetUtcNow(), WatchedAt = _time.GetUtcNow().AddDays(-3),
            Origin = PlaybackHistoryOrigin.ProviderSync, LinkStatus = PlaybackHistoryLinkStatus.Resolved,
        });
        await _database.SaveChangesAsync();

        await Service().SetPlayedAsync(_userId, _moviePublicId, played: false, playedAt: null, CancellationToken.None);

        var remaining = await _database.PlaybackHistoryEntries.AsNoTracking().ToListAsync();
        Assert.Equal(2, remaining.Count);
        Assert.DoesNotContain(remaining, entry => entry.Origin == PlaybackHistoryOrigin.Manual);
    }

    [Fact]
    public async Task UnwatchKeepsThePlayCount()
    {
        await WatchToCompletionAsync();

        await Service().SetPlayedAsync(_userId, _moviePublicId, played: false, playedAt: null, CancellationToken.None);

        var row = await _database.UserItemData.AsNoTracking().SingleAsync(data => data.MediaItemId == _movieId);
        Assert.False(row.Played);
        Assert.Equal(1, row.PlayCount);
        Assert.NotNull(row.LastWatchedAt);
    }

    [Fact]
    public async Task UnwatchQueuesAnOwnedOnlyRemoval()
    {
        Connect();
        await Service().SetPlayedAsync(_userId, _moviePublicId, played: true, playedAt: null, CancellationToken.None);

        await Service().SetPlayedAsync(_userId, _moviePublicId, played: false, playedAt: null, CancellationToken.None);

        Assert.Contains(
            await _database.WatchHistoryOutboxEvents.AsNoTracking().ToListAsync(),
            queued => queued.Operation == WatchHistoryOutboxOperation.RemoveOwnedTimelessEntries);
    }

    // ---- Folder marks ----

    [Fact]
    public async Task MarkingASeasonRecordsPerEpisodeHistoryAndIntent()
    {
        // Providers know episodes, not seasons, so the fan-out has to happen on this side.
        Connect();

        await Service().SetPlayedAsync(_userId, _seasonPublicId, played: true, playedAt: null, CancellationToken.None);

        var entries = await _database.PlaybackHistoryEntries.AsNoTracking().ToListAsync();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.NotEqual(_seasonId, entry.MediaItemId));

        var queued = await _database.WatchHistoryOutboxEvents.AsNoTracking().ToListAsync();
        Assert.Equal(2, queued.Count);
        Assert.All(queued, item => Assert.Equal(WatchHistoryOutboxOperation.EnsureTimelessWatched, item.Operation));
    }

    [Fact]
    public async Task TheEnsureEventCarriesTheEntryItMustRecordOwnershipOn()
    {
        // Without it, a mark undone before delivery leaves a remote timeless mark with no local
        // owner — and ownership is the only thing that permits removing it later.
        Connect();

        await Service().SetPlayedAsync(_userId, _moviePublicId, played: true, playedAt: null, CancellationToken.None);

        var entry = await _database.PlaybackHistoryEntries.AsNoTracking().SingleAsync();
        var queued = await _database.WatchHistoryOutboxEvents.AsNoTracking().SingleAsync();
        Assert.Equal(entry.Id, queued.HistoryEntryId);
    }

    [Fact]
    public async Task AMarkWithExistingHistoryStillPointsAtAnOwnableEntry()
    {
        // No new entry is created, but the event may still add a remote mark that needs an owner.
        Connect();
        _database.PlaybackHistoryEntries.Add(new PlaybackHistoryEntry
        {
            Id = Guid.NewGuid(), AppUserId = _userId, MediaItemId = _movieId,
            CreatedAt = _time.GetUtcNow(), WatchedAt = null,
            Origin = PlaybackHistoryOrigin.Manual, LinkStatus = PlaybackHistoryLinkStatus.None,
        });
        await _database.SaveChangesAsync();

        await Service().SetPlayedAsync(_userId, _moviePublicId, played: true, playedAt: null, CancellationToken.None);

        var queued = await _database.WatchHistoryOutboxEvents.AsNoTracking().SingleAsync();
        Assert.NotNull(queued.HistoryEntryId);
    }

    [Fact]
    public async Task AnOverlongSessionKeyLeavesHistoryUnkeyedRatherThanStoringOneTheGateRefused()
    {
        // The gate declines a key over 200 characters and falls back to the historical rule; history
        // must not then be keyed on the value the gate refused.
        var overlong = new string('s', 400);

        await Service().ReportPlaybackAsync(_userId, _moviePublicId, (long)(Runtime * 0.5), false, overlong, null, CancellationToken.None);
        await Service().ReportPlaybackAsync(_userId, _moviePublicId, (long)(Runtime * 0.95), false, overlong, null, CancellationToken.None);

        var entry = await _database.PlaybackHistoryEntries.AsNoTracking().SingleAsync();
        Assert.Null(entry.PlaySessionId);
        Assert.Empty(_database.PlaybackSessions);
    }

    [Fact]
    public async Task TheIdempotencyKeyFitsItsColumn()
    {
        // A 200-character session key plus two ids and the longest operation name overruns 256, and
        // silent truncation would let two different changes collide and the second be swallowed.
        Connect();

        await WatchToCompletionAsync(new string('s', 200));

        var queued = await _database.WatchHistoryOutboxEvents.AsNoTracking().SingleAsync();
        Assert.True(queued.IdempotencyKey.Length <= 256, $"key was {queued.IdempotencyKey.Length} characters");
    }

    // ---- Idempotency ----

    [Fact]
    public async Task RepeatingTheSameChangeDoesNotQueueTwice()
    {
        // Trakt does not deduplicate by item and timestamp, so a duplicate enqueue would show up as a
        // second viewing on the user's profile.
        Connect();

        await Service().SetPlayedAsync(_userId, _moviePublicId, played: true, playedAt: null, CancellationToken.None);
        var afterFirst = await _database.WatchHistoryOutboxEvents.CountAsync();
        await Service().SetPlayedAsync(_userId, _moviePublicId, played: true, playedAt: null, CancellationToken.None);

        Assert.Equal(afterFirst, await _database.WatchHistoryOutboxEvents.CountAsync());
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }
}
