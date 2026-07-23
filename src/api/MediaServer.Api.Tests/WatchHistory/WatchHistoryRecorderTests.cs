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

    private Task WatchToCompletionAsync(string session = "session-1") =>
        Task.Run(async () =>
        {
            await Service().ReportPlaybackAsync(_userId, _moviePublicId, (long)(Runtime * 0.5), false, session, null, CancellationToken.None);
            await Service().ReportPlaybackAsync(_userId, _moviePublicId, (long)(Runtime * 0.95), false, session, null, CancellationToken.None);
        });

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

    // ---- Unwatch ----

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
