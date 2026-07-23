using MediaServer.Api.Data;
using MediaServer.Api.Tests.Jellyfin;
using MediaServer.Api.WatchHistory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.WatchHistory;

public sealed class WatchHistorySyncApplyServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly TestTimeProvider _time = new(DateTimeOffset.Parse("2026-07-23T12:00:00Z"));
    private readonly FakeProvider _provider = new();
    private readonly int _userId;
    private readonly Guid _catalogId = Guid.NewGuid();
    private readonly Guid _otherCatalogId = Guid.NewGuid();

    public WatchHistorySyncApplyServiceTests()
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
        private long _nextId = 500;

        public List<WatchHistoryPlay> History { get; } = [];

        public List<WatchHistoryPlay> Added { get; } = [];

        public WatchHistoryFailure? FailAdds { get; set; }

        public string Key => "fake";

        public string DisplayName => "Fake";

        public WatchHistoryCapabilities Capabilities => new()
        {
            ExactTimestampWrites = true, TimelessWrites = true, AggregateWatchedReads = false,
            FullHistoryReads = true, IndividualEntryRemoval = true,
        };

        public Task<WatchHistoryResult<IReadOnlyList<WatchHistoryPlay>>> GetHistoryAsync(
            int appUserId, IReadOnlyCollection<WatchHistoryIdentity> identities, CancellationToken cancellationToken) =>
            Task.FromResult(WatchHistoryResult<IReadOnlyList<WatchHistoryPlay>>.Success([.. History]));

        public Task<WatchHistoryResult<IReadOnlyList<WatchHistoryPlay>>> AddPlaysAsync(
            int appUserId, IReadOnlyCollection<WatchHistoryPlay> plays, CancellationToken cancellationToken)
        {
            if (FailAdds is { } failure)
            {
                return Task.FromResult(WatchHistoryResult<IReadOnlyList<WatchHistoryPlay>>.Failed(failure, "stub"));
            }

            foreach (var play in plays)
            {
                Added.Add(play);
                History.Add(play with { RemoteId = (_nextId++).ToString() });
            }

            return Task.FromResult(WatchHistoryResult<IReadOnlyList<WatchHistoryPlay>>.Success([.. plays]));
        }

        public Task<WatchHistoryResult<int>> RemoveEntriesAsync(
            int appUserId, IReadOnlyCollection<string> remoteIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Apply must never remove remote history.");
    }

    private sealed class StubRegistry(IWatchHistoryProvider provider) : IWatchHistoryProviderRegistry
    {
        public IReadOnlyList<WatchHistoryProviderDescriptor> Describe() => [];

        public IWatchHistoryProvider? Find(string providerKey) =>
            string.Equals(providerKey, provider.Key, StringComparison.OrdinalIgnoreCase) ? provider : null;

        public IWatchHistoryProviderAuthorization? FindAuthorization(string providerKey) => null;
    }

    private WatchHistorySyncApplyService Apply() => new(
        _database, new StubRegistry(_provider), new WatchHistoryIdentityMapper(_database),
        _time, NullLogger<WatchHistorySyncApplyService>.Instance);

    private WatchHistorySyncPreviewService Preview() => new(
        _database, new StubRegistry(_provider), new WatchHistoryIdentityMapper(_database),
        _time, NullLogger<WatchHistorySyncPreviewService>.Instance);

    private MediaItem AddMovie(string tmdbId, Guid? catalogId = null)
    {
        var movie = new MediaItem
        {
            Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString("N"),
            CatalogId = catalogId ?? _catalogId, Kind = MediaKind.Movie, Title = "Inception",
            IdentityProvider = "tmdb", IdentityProviderId = tmdbId,
            AddedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow(),
        };
        _database.MediaItems.Add(movie);
        _database.SaveChanges();
        return movie;
    }

    private UserItemData AddRow(Guid itemId, bool played, int playCount, long resumeTicks = 0)
    {
        var row = new UserItemData
        {
            Id = Guid.NewGuid(), AppUserId = _userId, MediaItemId = itemId,
            Played = played, PlayCount = playCount, PlaybackPositionTicks = resumeTicks,
        };
        _database.UserItemData.Add(row);
        _database.SaveChanges();
        return row;
    }

    private void AddLocalPlay(Guid itemId, DateTimeOffset? watchedAt, PlaybackHistoryOrigin origin = PlaybackHistoryOrigin.LocalPlayback)
    {
        _database.PlaybackHistoryEntries.Add(new PlaybackHistoryEntry
        {
            Id = Guid.NewGuid(), AppUserId = _userId, MediaItemId = itemId,
            CreatedAt = _time.GetUtcNow(), WatchedAt = watchedAt, Origin = origin,
            PlaySessionId = watchedAt is null ? null : Guid.NewGuid().ToString("N"),
            LinkStatus = PlaybackHistoryLinkStatus.None,
        });
        _database.SaveChanges();
    }

    private static WatchHistoryIdentity Movie(int tmdb) =>
        new() { Kind = WatchHistoryMediaKind.Movie, TmdbId = tmdb };

    private async Task<Guid> PreviewRunAsync(WatchHistorySyncScope? scope = null)
    {
        var preview = await Preview().BuildAsync(_userId, scope ?? WatchHistorySyncScope.Everything, CancellationToken.None);
        Assert.True(preview.Succeeded, preview.Detail);
        _database.ChangeTracker.Clear();
        return preview.Value!.RunId;
    }

    private async Task<WatchHistorySyncApplyResult> ApplyAsync(Guid runId)
    {
        var result = await Apply().ApplyAsync(_userId, runId, CancellationToken.None);
        Assert.True(result.Succeeded, result.Detail);
        _database.ChangeTracker.Clear();
        return result.Value!;
    }

    // ---- Import ----

    [Fact]
    public async Task RemoteHistoryBecomesLocalHistory()
    {
        var movie = AddMovie("27205");
        var watchedAt = _time.GetUtcNow().AddDays(-3);
        _provider.History.Add(new WatchHistoryPlay(Movie(27205), watchedAt, "1"));

        await ApplyAsync(await PreviewRunAsync());

        var entry = await _database.PlaybackHistoryEntries.AsNoTracking().SingleAsync();
        Assert.Equal(PlaybackHistoryOrigin.ProviderSync, entry.Origin);
        Assert.Equal(watchedAt, entry.WatchedAt);
        // Imported, not created here, so this app may never delete it remotely.
        Assert.False(entry.ProviderEntryOwned);

        var row = await _database.UserItemData.AsNoTracking().SingleAsync();
        Assert.True(row.Played);
        Assert.Equal(1, row.PlayCount);
        Assert.Equal(watchedAt, row.LastWatchedAt);
    }

    [Fact]
    public async Task SeveralRemoteUnknownEntriesCollapseToOneLocally()
    {
        // Their individual times are unknowable, so more than one carries no information.
        var movie = AddMovie("27205");
        _provider.History.Add(new WatchHistoryPlay(Movie(27205), null, "1"));
        _provider.History.Add(new WatchHistoryPlay(Movie(27205), null, "2"));
        _provider.History.Add(new WatchHistoryPlay(Movie(27205), null, "3"));

        await ApplyAsync(await PreviewRunAsync());

        var entry = Assert.Single(await _database.PlaybackHistoryEntries.AsNoTracking().ToListAsync());
        Assert.Null(entry.WatchedAt);
        Assert.Equal(1, (await _database.UserItemData.AsNoTracking().SingleAsync()).PlayCount);
    }

    [Fact]
    public async Task AWatchedItemLosesItsResumePointButAnUnknownOneKeepsIt()
    {
        var watched = AddMovie("27205");
        var unknown = AddMovie("1375666");
        AddRow(watched.Id, played: false, playCount: 0, resumeTicks: 5_000);
        AddRow(unknown.Id, played: false, playCount: 0, resumeTicks: 9_000);
        _provider.History.Add(new WatchHistoryPlay(Movie(27205), _time.GetUtcNow(), "1"));

        await ApplyAsync(await PreviewRunAsync());

        var rows = await _database.UserItemData.AsNoTracking().ToDictionaryAsync(row => row.MediaItemId);
        Assert.Equal(0, rows[watched.Id].PlaybackPositionTicks);
        // Still genuinely useful to the user, so it survives.
        Assert.Equal(9_000, rows[unknown.Id].PlaybackPositionTicks);
    }

    [Fact]
    public async Task LastPlayedDateIsNeverWrittenFromTheProvider()
    {
        // Imported history would otherwise reshuffle Continue Watching and Next Up.
        var movie = AddMovie("27205");
        var row = AddRow(movie.Id, played: false, playCount: 0);
        row.LastPlayedDate = _time.GetUtcNow().AddYears(-1);
        await _database.SaveChangesAsync();
        _provider.History.Add(new WatchHistoryPlay(Movie(27205), _time.GetUtcNow(), "1"));

        await ApplyAsync(await PreviewRunAsync());

        var reloaded = await _database.UserItemData.AsNoTracking().SingleAsync();
        Assert.Equal(_time.GetUtcNow().AddYears(-1), reloaded.LastPlayedDate);
    }

    // ---- Export ----

    [Fact]
    public async Task ALocalPlayTheProviderLacksIsExportedWithItsExactTime()
    {
        var movie = AddMovie("27205");
        var watchedAt = _time.GetUtcNow().AddDays(-2);
        AddRow(movie.Id, played: true, playCount: 1);
        AddLocalPlay(movie.Id, watchedAt);

        var result = await ApplyAsync(await PreviewRunAsync());

        Assert.Equal(1, result.Exported);
        Assert.Equal(watchedAt, Assert.Single(_provider.Added).WatchedAt);
    }

    [Fact]
    public async Task APlayTheProviderAlreadyHasIsNotSentAgain()
    {
        // Providers do not deduplicate, so re-posting would be a second viewing.
        var movie = AddMovie("27205");
        var watchedAt = _time.GetUtcNow().AddDays(-2);
        AddRow(movie.Id, played: true, playCount: 1);
        AddLocalPlay(movie.Id, watchedAt);
        _provider.History.Add(new WatchHistoryPlay(Movie(27205), watchedAt, "1"));

        var result = await ApplyAsync(await PreviewRunAsync());

        Assert.Equal(0, result.Exported);
        Assert.Empty(_provider.Added);
    }

    [Fact]
    public async Task ALocalCountOfSeveralExportsAtMostOneTimelessMark()
    {
        // Five plays with no recorded times cannot become five "unknown" entries.
        var movie = AddMovie("27205");
        AddRow(movie.Id, played: true, playCount: 5);

        await ApplyAsync(await PreviewRunAsync());

        var sent = Assert.Single(_provider.Added);
        Assert.Null(sent.WatchedAt);
    }

    [Fact]
    public async Task AnUnwatchedRowIsNeverExportedEvenWithARetainedCount()
    {
        // Re-uploading would undo the user's unwatch on the provider's side.
        var movie = AddMovie("27205");
        AddRow(movie.Id, played: false, playCount: 3);

        var result = await ApplyAsync(await PreviewRunAsync());

        Assert.Equal(0, result.Exported);
        Assert.Empty(_provider.Added);
    }

    [Fact]
    public async Task AFailedExportLeavesThatItemsLocalStateAlone()
    {
        // The remote snapshot is not authoritative for a play that never made it across.
        var movie = AddMovie("27205");
        AddRow(movie.Id, played: true, playCount: 1);
        AddLocalPlay(movie.Id, _time.GetUtcNow().AddDays(-2));
        _provider.FailAdds = WatchHistoryFailure.Transient;

        var result = await ApplyAsync(await PreviewRunAsync());

        Assert.Equal(1, result.Skipped[WatchHistorySyncSkip.ExportFailed]);
        var row = await _database.UserItemData.AsNoTracking().SingleAsync();
        Assert.True(row.Played);
        Assert.Equal(1, row.PlayCount);
        Assert.Single(_database.PlaybackHistoryEntries);
    }

    // ---- Guards ----

    [Fact]
    public async Task ARowThatChangedSinceThePreviewIsSkipped()
    {
        // A play recorded while the job ran must not be overwritten by a snapshot read before it.
        var movie = AddMovie("27205");
        var row = AddRow(movie.Id, played: false, playCount: 0);
        _provider.History.Add(new WatchHistoryPlay(Movie(27205), _time.GetUtcNow(), "1"));
        var runId = await PreviewRunAsync();

        var tracked = await _database.UserItemData.SingleAsync();
        tracked.PlaybackPositionTicks = 1234;
        await _database.SaveChangesAsync();
        _database.ChangeTracker.Clear();

        var result = await ApplyAsync(runId);

        Assert.Equal(1, result.Skipped[WatchHistorySyncSkip.LocalStateChangedDuringSync]);
        Assert.Empty(_database.PlaybackHistoryEntries);
    }

    [Fact]
    public async Task TwoEditionsOfOneFilmAreLeftAlone()
    {
        AddMovie("27205");
        AddMovie("27205", catalogId: _otherCatalogId);
        _provider.History.Add(new WatchHistoryPlay(Movie(27205), _time.GetUtcNow(), "1"));

        var result = await ApplyAsync(await PreviewRunAsync());

        Assert.Equal(2, result.Skipped[WatchHistorySyncSkip.AmbiguousLocalIdentity]);
        Assert.Empty(_database.PlaybackHistoryEntries);
    }

    [Fact]
    public async Task AnUnidentifiedItemIsSkipped()
    {
        _database.MediaItems.Add(new MediaItem
        {
            Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString("N"), CatalogId = _catalogId,
            Kind = MediaKind.Movie, Title = "Unknown", AddedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow(),
        });
        await _database.SaveChangesAsync();

        var result = await ApplyAsync(await PreviewRunAsync());

        Assert.Equal(1, result.Skipped[WatchHistorySyncSkip.UnidentifiedLocally]);
    }

    [Fact]
    public async Task ALegacyCountTheProviderKnowsNothingAboutIsPreserved()
    {
        // Recomputing it to zero would discard the only record that those viewings happened.
        var movie = AddMovie("27205");
        AddRow(movie.Id, played: false, playCount: 4);

        await ApplyAsync(await PreviewRunAsync());

        var row = await _database.UserItemData.AsNoTracking().SingleAsync();
        Assert.Equal(4, row.PlayCount);
        Assert.False(row.Played);
    }

    [Fact]
    public async Task AnExpiredPreviewCannotBeApplied()
    {
        AddMovie("27205");
        var runId = await PreviewRunAsync();
        _time.Advance(WatchHistorySyncPreviewService.PreviewLifetime + TimeSpan.FromMinutes(1));

        var result = await Apply().ApplyAsync(_userId, runId, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(
            WatchHistorySyncStatus.Abandoned,
            (await _database.WatchHistorySyncRuns.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task StuckOutboundWorkBlocksApply()
    {
        // It will never reflect those local changes, so applying would overwrite them with a snapshot
        // that predates them.
        var movie = AddMovie("27205");
        var runId = await PreviewRunAsync();
        var connection = await _database.WatchHistoryConnections.FirstAsync();
        _database.WatchHistoryOutboxEvents.Add(new WatchHistoryOutboxEvent
        {
            Id = Guid.NewGuid(), ConnectionId = connection.Id, AppUserId = _userId, MediaItemId = movie.Id,
            Operation = WatchHistoryOutboxOperation.AddExactWatch, IdempotencyKey = "k",
            Status = WatchHistoryOutboxStatus.Terminal, CreatedAt = _time.GetUtcNow(),
        });
        await _database.SaveChangesAsync();

        var result = await Apply().ApplyAsync(_userId, runId, CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ARunCannotBeAppliedTwice()
    {
        AddMovie("27205");
        _provider.History.Add(new WatchHistoryPlay(Movie(27205), _time.GetUtcNow(), "1"));
        var runId = await PreviewRunAsync();

        await ApplyAsync(runId);
        var second = await Apply().ApplyAsync(_userId, runId, CancellationToken.None);

        Assert.False(second.Succeeded);
    }

    [Fact]
    public async Task ApplyingTheSameStateAgainChangesNothing()
    {
        // Idempotent at the play level: no re-post, no extra unknown entry.
        var movie = AddMovie("27205");
        var watchedAt = _time.GetUtcNow().AddDays(-2);
        _provider.History.Add(new WatchHistoryPlay(Movie(27205), watchedAt, "1"));

        await ApplyAsync(await PreviewRunAsync());
        var afterFirst = await _database.PlaybackHistoryEntries.AsNoTracking().CountAsync();
        await ApplyAsync(await PreviewRunAsync());

        Assert.Equal(afterFirst, await _database.PlaybackHistoryEntries.AsNoTracking().CountAsync());
        Assert.Empty(_provider.Added);
    }

    [Fact]
    public async Task ACatalogScopeLimitsWhatIsTouched()
    {
        var inScope = AddMovie("27205");
        var outOfScope = AddMovie("1375666", catalogId: _otherCatalogId);
        _provider.History.Add(new WatchHistoryPlay(Movie(27205), _time.GetUtcNow(), "1"));
        _provider.History.Add(new WatchHistoryPlay(Movie(1375666), _time.GetUtcNow(), "2"));

        await ApplyAsync(await PreviewRunAsync(new WatchHistorySyncScope([_catalogId], [])));

        var entry = Assert.Single(await _database.PlaybackHistoryEntries.AsNoTracking().ToListAsync());
        Assert.Equal(inScope.Id, entry.MediaItemId);
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }
}
