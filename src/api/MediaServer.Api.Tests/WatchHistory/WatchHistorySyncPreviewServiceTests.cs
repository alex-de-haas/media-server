using System.Text.Json;
using MediaServer.Api.Data;
using MediaServer.Api.Tests.Jellyfin;
using MediaServer.Api.WatchHistory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.WatchHistory;

public sealed class WatchHistorySyncPreviewServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly TestTimeProvider _time = new(DateTimeOffset.Parse("2026-07-23T12:00:00Z"));
    private readonly FakeProvider _provider = new();
    private readonly int _userId;
    private readonly Guid _catalogId = Guid.NewGuid();
    private readonly Guid _otherCatalogId = Guid.NewGuid();

    public WatchHistorySyncPreviewServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();

        var user = new AppUser
        {
            HostUserId = "host-1", Email = "alex@example.com", DisplayName = "Alex",
            Role = AppUserRole.User, CreatedAt = _time.GetUtcNow(), LastSeenAt = _time.GetUtcNow(),
        };
        _database.AppUsers.Add(user);
        _database.SaveChanges();
        _userId = user.Id;

        _database.Catalogs.AddRange(
            new Catalog { Id = _catalogId, Name = "Movies", Type = CatalogType.Movie, Root = "/m", CreatedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow() },
            new Catalog { Id = _otherCatalogId, Name = "4K", Type = CatalogType.Movie, Root = "/4k", CreatedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow() });

        var connection = new WatchHistoryProviderConnection
        {
            Id = Guid.NewGuid(), AppUserId = _userId, ProviderKey = "fake",
            Status = WatchHistoryConnectionStatus.Connected, ConnectedAt = _time.GetUtcNow(),
        };
        connection.SecretKey = "fake.connection.x.tokens";
        _database.WatchHistoryConnections.Add(connection);
        _database.SaveChanges();
    }

    private sealed class FakeProvider : IWatchHistoryProvider
    {
        public List<WatchHistoryPlay> History { get; } = [];

        public WatchHistoryFailure? FailWith { get; set; }

        public WatchHistoryCapabilities Overrides { get; set; } = new()
        {
            ExactTimestampWrites = true, TimelessWrites = true, AggregateWatchedReads = false,
            FullHistoryReads = true, IndividualEntryRemoval = true,
        };

        public string Key => "fake";

        public string DisplayName => "Fake";

        public WatchHistoryCapabilities Capabilities => Overrides;

        public Task<WatchHistoryResult<IReadOnlyList<WatchHistoryPlay>>> GetHistoryAsync(
            int appUserId, IReadOnlyCollection<WatchHistoryIdentity> identities, CancellationToken cancellationToken) =>
            Task.FromResult(FailWith is { } failure
                ? WatchHistoryResult<IReadOnlyList<WatchHistoryPlay>>.Failed(failure, "stub")
                : WatchHistoryResult<IReadOnlyList<WatchHistoryPlay>>.Success([.. History]));

        public Task<WatchHistoryResult<IReadOnlyList<WatchHistoryPlay>>> AddPlaysAsync(
            int appUserId, IReadOnlyCollection<WatchHistoryPlay> plays, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The preview must not write.");

        public Task<WatchHistoryResult<int>> RemoveEntriesAsync(
            int appUserId, IReadOnlyCollection<string> remoteIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The preview must not write.");
    }

    private sealed class StubRegistry(IWatchHistoryProvider provider) : IWatchHistoryProviderRegistry
    {
        public IReadOnlyList<WatchHistoryProviderDescriptor> Describe() => [];

        public IWatchHistoryProvider? Find(string providerKey) =>
            string.Equals(providerKey, provider.Key, StringComparison.OrdinalIgnoreCase) ? provider : null;

        public IWatchHistoryProviderAuthorization? FindAuthorization(string providerKey) => null;
    }

    private WatchHistorySyncPreviewService Service() => new(
        _database, new StubRegistry(_provider), new WatchHistoryIdentityMapper(_database),
        _time, NullLogger<WatchHistorySyncPreviewService>.Instance);

    private MediaItem AddMovie(string tmdbId, Guid? catalogId = null, string title = "Inception")
    {
        var movie = new MediaItem
        {
            Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString("N"),
            CatalogId = catalogId ?? _catalogId, Kind = MediaKind.Movie, Title = title,
            IdentityProvider = "tmdb", IdentityProviderId = tmdbId,
            AddedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow(),
        };
        _database.MediaItems.Add(movie);
        _database.SaveChanges();
        return movie;
    }

    private void MarkWatchedLocally(Guid mediaItemId, int playCount = 1, bool played = true)
    {
        _database.UserItemData.Add(new UserItemData
        {
            Id = Guid.NewGuid(), AppUserId = _userId, MediaItemId = mediaItemId,
            Played = played, PlayCount = playCount,
        });
        _database.PlaybackHistoryEntries.Add(new PlaybackHistoryEntry
        {
            Id = Guid.NewGuid(), AppUserId = _userId, MediaItemId = mediaItemId,
            CreatedAt = _time.GetUtcNow(), WatchedAt = _time.GetUtcNow().AddDays(-1),
            Origin = PlaybackHistoryOrigin.LocalPlayback, PlaySessionId = Guid.NewGuid().ToString("N"),
            LinkStatus = PlaybackHistoryLinkStatus.None,
        });
        _database.SaveChanges();
    }

    private static WatchHistoryIdentity Movie(int tmdb) =>
        new() { Kind = WatchHistoryMediaKind.Movie, TmdbId = tmdb };

    private async Task<WatchHistorySyncPreview> PreviewAsync(WatchHistorySyncScope? scope = null)
    {
        var result = await Service().BuildAsync(_userId, scope ?? WatchHistorySyncScope.Everything, CancellationToken.None);
        Assert.True(result.Succeeded, result.Detail);
        return result.Value!;
    }

    [Fact]
    public async Task AnItemWatchedOnBothSidesNeedsNothing()
    {
        var movie = AddMovie("27205");
        MarkWatchedLocally(movie.Id);
        _provider.History.Add(new WatchHistoryPlay(Movie(27205), _time.GetUtcNow().AddDays(-1), "1"));

        var preview = await PreviewAsync();

        Assert.Equal(1, preview.Counts[WatchHistorySyncClassification.InSync]);
        Assert.Empty(preview.Sample);
    }

    [Fact]
    public async Task HistoryOnlyTheProviderHasIsReportedAsRemoteOnly()
    {
        var movie = AddMovie("27205");
        _provider.History.Add(new WatchHistoryPlay(Movie(27205), _time.GetUtcNow().AddDays(-1), "1"));

        var preview = await PreviewAsync();

        var entry = Assert.Single(preview.Sample);
        Assert.Equal(WatchHistorySyncClassification.RemoteOnly, entry.Classification);
        Assert.Equal(movie.Id, entry.MediaItemId);
    }

    [Fact]
    public async Task HistoryOnlyThisLibraryHasIsReportedAsLocalOnly()
    {
        var movie = AddMovie("27205");
        MarkWatchedLocally(movie.Id);

        var preview = await PreviewAsync();

        Assert.Equal(WatchHistorySyncClassification.LocalOnly, Assert.Single(preview.Sample).Classification);
    }

    [Fact]
    public async Task AnUnwatchedRowWithAHistoricalCountIsNotTreatedAsSomethingToExport()
    {
        // Unwatch is a statement about current state; re-uploading would undo the user's action.
        var movie = AddMovie("27205");
        MarkWatchedLocally(movie.Id, playCount: 3, played: false);

        var preview = await PreviewAsync();

        Assert.Equal(
            WatchHistorySyncClassification.LocalUnwatchedWithHistory,
            Assert.Single(preview.Sample).Classification);
    }

    [Fact]
    public async Task AnUnidentifiedItemIsReportedRatherThanCompared()
    {
        var movie = new MediaItem
        {
            Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString("N"), CatalogId = _catalogId,
            Kind = MediaKind.Movie, Title = "Unknown", AddedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow(),
        };
        _database.MediaItems.Add(movie);
        await _database.SaveChangesAsync();

        var preview = await PreviewAsync();

        Assert.Equal(
            WatchHistorySyncClassification.UnidentifiedLocally,
            Assert.Single(preview.Sample).Classification);
    }

    [Fact]
    public async Task TwoEditionsOfOneFilmAreFlaggedRatherThanPickedBetween()
    {
        // Applying a remote state to one row is arbitrary; to both can clear an edition's resume point.
        AddMovie("27205");
        AddMovie("27205", catalogId: _otherCatalogId);
        _provider.History.Add(new WatchHistoryPlay(Movie(27205), _time.GetUtcNow(), "1"));

        var preview = await PreviewAsync();

        Assert.Equal(2, preview.Counts[WatchHistorySyncClassification.AmbiguousLocalIdentity]);
    }

    [Fact]
    public async Task ACatalogScopeDisambiguatesEditions()
    {
        // Selecting one catalog is how the user says which copy they mean.
        var wanted = AddMovie("27205");
        AddMovie("27205", catalogId: _otherCatalogId);
        _provider.History.Add(new WatchHistoryPlay(Movie(27205), _time.GetUtcNow(), "1"));

        var preview = await PreviewAsync(new WatchHistorySyncScope([_catalogId], []));

        var entry = Assert.Single(preview.Sample);
        Assert.Equal(wanted.Id, entry.MediaItemId);
        Assert.Equal(WatchHistorySyncClassification.RemoteOnly, entry.Classification);
    }

    [Fact]
    public async Task RemoteHistoryForItemsNotInTheLibraryIsSimplyIgnored()
    {
        // The preview compares only what the library contains: apply never creates items, and
        // GetHistoryAsync answers for the identities it is given, so unrelated remote history cannot
        // change any local item's classification.
        var movie = AddMovie("27205");
        MarkWatchedLocally(movie.Id);
        _provider.History.Add(new WatchHistoryPlay(Movie(99999), _time.GetUtcNow(), "1"));

        var preview = await PreviewAsync();

        var entry = Assert.Single(preview.Sample);
        Assert.Equal(movie.Id, entry.MediaItemId);
        Assert.Equal(WatchHistorySyncClassification.LocalOnly, entry.Classification);
        Assert.Equal(0, entry.RemotePlayCount);
    }

    [Fact]
    public async Task OnlyALegacyRowWithNoPerPlayHistoryWarnsAboutACollapsingCount()
    {
        // A count of N with no per-play rows can export at most one timeless mark, so N becomes 1.
        var movie = AddMovie("27205");
        _database.UserItemData.Add(new UserItemData
        {
            Id = Guid.NewGuid(), AppUserId = _userId, MediaItemId = movie.Id, Played = true, PlayCount = 4,
        });
        await _database.SaveChangesAsync();

        Assert.True((await PreviewAsync()).AggregateCountsMayCollapse);
    }

    [Fact]
    public async Task AnOrdinaryLibraryDoesNotWarnAboutCollapsingCounts()
    {
        // An unwatched item has no aggregate history to lose; warning here would cry wolf on every
        // preview and train the user to click through it.
        var watched = AddMovie("27205");
        MarkWatchedLocally(watched.Id);
        AddMovie("1375666", title: "Unwatched");

        Assert.False((await PreviewAsync()).AggregateCountsMayCollapse);
    }

    [Fact]
    public async Task PendingOutboundWorkIsSurfacedSoApplyCanBlockOnIt()
    {
        // Applying over undelivered local changes would read a remote snapshot that does not yet
        // reflect them.
        var movie = AddMovie("27205");
        var connection = await _database.WatchHistoryConnections.FirstAsync();
        _database.WatchHistoryOutboxEvents.Add(new WatchHistoryOutboxEvent
        {
            Id = Guid.NewGuid(), ConnectionId = connection.Id, AppUserId = _userId, MediaItemId = movie.Id,
            Operation = WatchHistoryOutboxOperation.AddExactWatch, IdempotencyKey = "k",
            Status = WatchHistoryOutboxStatus.Pending, CreatedAt = _time.GetUtcNow(),
        });
        await _database.SaveChangesAsync();

        var preview = await PreviewAsync();

        Assert.True(preview.HasPendingOutboundWork);
        Assert.False(preview.HasTerminalOutboundWork);
    }

    [Fact]
    public async Task TerminalOutboundWorkIsSurfacedSeparately()
    {
        // It cannot be drained by waiting, so the user has to see it rather than have it discarded.
        var movie = AddMovie("27205");
        var connection = await _database.WatchHistoryConnections.FirstAsync();
        _database.WatchHistoryOutboxEvents.Add(new WatchHistoryOutboxEvent
        {
            Id = Guid.NewGuid(), ConnectionId = connection.Id, AppUserId = _userId, MediaItemId = movie.Id,
            Operation = WatchHistoryOutboxOperation.AddExactWatch, IdempotencyKey = "k",
            Status = WatchHistoryOutboxStatus.Terminal, CreatedAt = _time.GetUtcNow(),
        });
        await _database.SaveChangesAsync();

        var preview = await PreviewAsync();

        Assert.True(preview.HasTerminalOutboundWork);
        Assert.False(preview.HasPendingOutboundWork);
    }

    [Fact]
    public async Task TheRunCapturesStateRevisionsForApplyToCheckAgainst()
    {
        var movie = AddMovie("27205");
        MarkWatchedLocally(movie.Id);

        var preview = await PreviewAsync();

        var run = await _database.WatchHistorySyncRuns.AsNoTracking().SingleAsync(entry => entry.Id == preview.RunId);
        var revisions = JsonSerializer.Deserialize<Dictionary<string, int>>(run.CapturedRevisions!);
        Assert.True(revisions!.ContainsKey(movie.Id.ToString("N")));
    }

    [Fact]
    public async Task ThePreviewExpiresSoAStaleOneCannotBeApplied()
    {
        AddMovie("27205");

        var preview = await PreviewAsync();

        var run = await _database.WatchHistorySyncRuns.AsNoTracking().SingleAsync(entry => entry.Id == preview.RunId);
        Assert.Equal(WatchHistorySyncStatus.Previewed, run.Status);
        Assert.Equal(_time.GetUtcNow().Add(WatchHistorySyncPreviewService.PreviewLifetime), run.ExpiresAt);
    }

    [Fact]
    public async Task ThePreviewWritesNothingToTheProvider()
    {
        // The fake throws on any write; the point is that a preview is read-only.
        AddMovie("27205");
        MarkWatchedLocally((await _database.MediaItems.FirstAsync()).Id);

        await PreviewAsync();
    }

    [Fact]
    public async Task WithoutAConnectionThereIsNothingToPreview()
    {
        _database.WatchHistoryConnections.RemoveRange(_database.WatchHistoryConnections);
        await _database.SaveChangesAsync();

        var result = await Service().BuildAsync(_userId, WatchHistorySyncScope.Everything, CancellationToken.None);

        Assert.Equal(WatchHistoryFailure.AuthenticationRequired, result.Failure);
    }

    [Fact]
    public async Task AProviderThatCannotReportHistoryCannotBeReconciledAgainst()
    {
        // An aggregate watched flag cannot tell an imported play from a local one.
        _provider.Overrides = _provider.Overrides with { FullHistoryReads = false };

        var result = await Service().BuildAsync(_userId, WatchHistorySyncScope.Everything, CancellationToken.None);

        Assert.Equal(WatchHistoryFailure.Unsupported, result.Failure);
    }

    [Fact]
    public async Task AFailedRemoteReadDoesNotLeaveAHalfBuiltRun()
    {
        AddMovie("27205");
        _provider.FailWith = WatchHistoryFailure.RateLimited;

        var result = await Service().BuildAsync(_userId, WatchHistorySyncScope.Everything, CancellationToken.None);

        Assert.Equal(WatchHistoryFailure.RateLimited, result.Failure);
        Assert.Empty(_database.WatchHistorySyncRuns);
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }
}
