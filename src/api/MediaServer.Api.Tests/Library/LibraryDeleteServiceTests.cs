using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Library;

/// <summary>
/// Coverage for deleting parts of a series: one episode, a whole season, and the container pruning that
/// follows. Each test seeds its own catalog with a real temp root so file erasure is exercised for real.
/// </summary>
public sealed class LibraryDeleteServiceTests : IDisposable
{
    private readonly JellyfinDatabase _db = new();
    private readonly MediaServerDbContext _context;
    private readonly string _root;

    private Guid _catalogId;
    private Guid _seriesId;
    private Guid _seasonId;
    private Guid _episode1Id;
    private Guid _episode2Id;

    public LibraryDeleteServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ms-child-del-" + Guid.NewGuid().ToString("N"));
        CatalogPaths.For(_root).EnsureCreated();
        Seed();
        _context = _db.Create();
    }

    private LibraryDeleteService Service() =>
        new(_context, new LibraryFileEraser(new CatalogPathSandbox(), NullLogger<LibraryFileEraser>.Instance));

    [Fact]
    public async Task Deleting_one_episode_keeps_its_siblings_and_the_containers()
    {
        var result = await Service().DeleteEpisodeAsync(_episode1Id, deleteFiles: false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.SeasonRemoved);
        Assert.False(result.SeriesRemoved);

        await using var verify = _db.Create();
        Assert.False(await verify.MediaItems.AnyAsync(item => item.Id == _episode1Id));
        Assert.False(await verify.MediaSources.AnyAsync(source => source.MediaItemId == _episode1Id));
        Assert.True(await verify.MediaItems.AnyAsync(item => item.Id == _episode2Id));
        Assert.True(await verify.MediaItems.AnyAsync(item => item.Id == _seasonId));
        Assert.True(await verify.MediaItems.AnyAsync(item => item.Id == _seriesId));
    }

    [Fact]
    public async Task Deleting_an_episode_detaches_its_source_file_and_drops_its_user_rows()
    {
        // The ingest keeps its file row (a rescan can re-adopt it); the per-user state and the plays go.
        var result = await Service().DeleteEpisodeAsync(_episode1Id, deleteFiles: false, CancellationToken.None);
        Assert.NotNull(result);

        await using var verify = _db.Create();
        var sourceFile = await verify.SourceFiles.SingleAsync();
        Assert.Null(sourceFile.MediaItemId);
        Assert.False(await verify.UserItemData.AnyAsync(data => data.MediaItemId == _episode1Id));
        // History follows the item by DB cascade; the outbox row has no FK and keeps its frozen identity.
        Assert.False(await verify.PlaybackHistoryEntries.AnyAsync(entry => entry.MediaItemId == _episode1Id));
        Assert.True(await verify.WatchHistoryOutboxEvents.AnyAsync(item => item.MediaItemId == _episode1Id));
    }

    [Fact]
    public async Task Deleting_an_episode_with_files_erases_only_that_episodes_file()
    {
        var kept = AbsolutePath("Breaking Bad/Season 1/S01E02.mkv");
        var erased = AbsolutePath("Breaking Bad/Season 1/S01E01.mkv");

        var result = await Service().DeleteEpisodeAsync(_episode1Id, deleteFiles: true, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(File.Exists(erased));
        Assert.True(File.Exists(kept)); // the season folder stays: a sibling still lives there
    }

    [Fact]
    public async Task Deleting_an_episode_without_files_leaves_the_file_for_a_rescan()
    {
        var path = AbsolutePath("Breaking Bad/Season 1/S01E01.mkv");

        await Service().DeleteEpisodeAsync(_episode1Id, deleteFiles: false, CancellationToken.None);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Deleting_the_last_episode_prunes_the_season_and_then_the_series()
    {
        await Service().DeleteEpisodeAsync(_episode1Id, deleteFiles: false, CancellationToken.None);
        var result = await Service().DeleteEpisodeAsync(_episode2Id, deleteFiles: false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.SeasonRemoved);
        Assert.True(result.SeriesRemoved);

        await using var verify = _db.Create();
        Assert.False(await verify.MediaItems.AnyAsync());
    }

    [Fact]
    public async Task A_leftover_season_scoped_extra_keeps_its_season_alive()
    {
        // Extras carry SeasonId, so emptiness counts them: pruning the season around one would fail the
        // Restrict self-FK on ParentId, and the operator never asked for the extra to go.
        var extraId = await AddExtraAsync(seasonScoped: true);

        await Service().DeleteEpisodeAsync(_episode1Id, deleteFiles: false, CancellationToken.None);
        var result = await Service().DeleteEpisodeAsync(_episode2Id, deleteFiles: false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.SeasonRemoved);
        Assert.False(result.SeriesRemoved);

        await using var verify = _db.Create();
        Assert.True(await verify.MediaItems.AnyAsync(item => item.Id == extraId));
        Assert.True(await verify.MediaItems.AnyAsync(item => item.Id == _seasonId));
        Assert.True(await verify.MediaItems.AnyAsync(item => item.Id == _seriesId));
    }

    [Fact]
    public async Task A_series_level_extra_keeps_the_series_after_its_season_is_pruned()
    {
        var extraId = await AddExtraAsync(seasonScoped: false);

        await Service().DeleteEpisodeAsync(_episode1Id, deleteFiles: false, CancellationToken.None);
        var result = await Service().DeleteEpisodeAsync(_episode2Id, deleteFiles: false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.SeasonRemoved);
        Assert.False(result.SeriesRemoved);

        await using var verify = _db.Create();
        Assert.False(await verify.MediaItems.AnyAsync(item => item.Id == _seasonId));
        Assert.True(await verify.MediaItems.AnyAsync(item => item.Id == extraId));
        Assert.True(await verify.MediaItems.AnyAsync(item => item.Id == _seriesId));
    }

    [Fact]
    public async Task Deleting_a_season_takes_its_episodes_its_extras_and_the_series_it_emptied()
    {
        var extraId = await AddExtraAsync(seasonScoped: true);

        var result = await Service().DeleteSeasonAsync(_seasonId, deleteFiles: true, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.SeasonRemoved);
        Assert.True(result.SeriesRemoved);
        Assert.False(File.Exists(AbsolutePath("Breaking Bad/Season 1/S01E01.mkv")));
        Assert.False(File.Exists(AbsolutePath("Breaking Bad/Season 1/S01E02.mkv")));

        await using var verify = _db.Create();
        Assert.False(await verify.MediaItems.AnyAsync(item =>
            item.Id == extraId || item.Id == _episode1Id || item.Id == _episode2Id ||
            item.Id == _seasonId || item.Id == _seriesId));
        Assert.False(await verify.MediaSources.AnyAsync());
    }

    [Fact]
    public async Task The_episode_route_refuses_anything_that_is_not_a_published_episode()
    {
        var service = Service();

        Assert.Null(await service.DeleteEpisodeAsync(_seriesId, deleteFiles: false, CancellationToken.None));
        Assert.Null(await service.DeleteEpisodeAsync(_seasonId, deleteFiles: false, CancellationToken.None));
        Assert.Null(await service.DeleteEpisodeAsync(Guid.NewGuid(), deleteFiles: false, CancellationToken.None));
        Assert.Null(await service.DeleteSeasonAsync(_episode1Id, deleteFiles: false, CancellationToken.None));
        Assert.Null(await service.DeleteSeasonAsync(_seriesId, deleteFiles: false, CancellationToken.None));
    }

    [Fact]
    public async Task An_unpublished_episode_is_not_deletable()
    {
        await using (var seed = _db.Create())
        {
            await seed.MediaItems.Where(item => item.Id == _episode1Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.PublicId, (string?)null));
        }

        Assert.Null(await Service().DeleteEpisodeAsync(_episode1Id, deleteFiles: false, CancellationToken.None));
    }

    private string AbsolutePath(string relative) =>
        Path.Combine(_root, Path.Combine(relative.Split('/')));

    private async Task<Guid> AddExtraAsync(bool seasonScoped)
    {
        var now = DateTimeOffset.UtcNow;
        await using var seed = _db.Create();
        var extra = new MediaItem
        {
            Id = Guid.NewGuid(),
            PublicId = Guid.NewGuid().ToString("N"),
            CatalogId = _catalogId,
            Kind = MediaKind.Video,
            Title = "Creditless Opening 1",
            ParentId = seasonScoped ? _seasonId : _seriesId,
            SeriesId = _seriesId,
            SeasonId = seasonScoped ? _seasonId : null,
            AddedAt = now,
            UpdatedAt = now,
        };
        seed.MediaItems.Add(extra);
        await seed.SaveChangesAsync();
        return extra.Id;
    }

    /// <summary>Series → one season → two episodes, each with a file on disk.</summary>
    private void Seed()
    {
        var now = DateTimeOffset.UtcNow;
        using var context = _db.Create();

        var user = new AppUser
        {
            HostUserId = "host-1", Email = "user@example.com", Role = AppUserRole.User,
            CreatedAt = now, LastSeenAt = now,
        };
        context.AppUsers.Add(user);

        var catalog = new Catalog
        {
            Id = Guid.NewGuid(), Name = "Shows", Type = CatalogType.Series, Root = _root,
            CreatedAt = now, UpdatedAt = now,
        };
        _catalogId = catalog.Id;
        context.Catalogs.Add(catalog);

        var series = new MediaItem
        {
            Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString("N"), CatalogId = catalog.Id,
            Kind = MediaKind.Series, Title = "Breaking Bad", Year = 2008,
            IdentityProvider = "tmdb", IdentityProviderId = "1396", AddedAt = now, UpdatedAt = now,
        };
        var season = new MediaItem
        {
            Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString("N"), CatalogId = catalog.Id,
            Kind = MediaKind.Season, Title = "Season 1", ParentId = series.Id, SeriesId = series.Id,
            IndexNumber = 1, AddedAt = now, UpdatedAt = now,
        };
        _seriesId = series.Id;
        _seasonId = season.Id;
        context.MediaItems.AddRange(series, season);

        var ingest = new IngestItem
        {
            Id = Guid.NewGuid(), CatalogId = catalog.Id, Stage = IngestStage.Publish,
            Status = IngestStatus.Done, CreatedAt = now, UpdatedAt = now,
        };
        context.IngestItems.Add(ingest);

        // An outbox event needs its owning connection to exist (FK), and the user id needs to be real
        // before the dependent rows reference it.
        context.SaveChanges();
        var connection = new WatchHistoryProviderConnection
        {
            Id = Guid.NewGuid(), AppUserId = user.Id, ProviderKey = "trakt",
            Status = WatchHistoryConnectionStatus.Connected, ConnectedAt = now,
            SecretKey = "trakt.connection.x.tokens",
        };
        context.WatchHistoryConnections.Add(connection);

        _episode1Id = SeedEpisode(context, catalog, series, season, 1, ingest, connection, user.Id, now);
        _episode2Id = SeedEpisode(context, catalog, series, season, 2, ingest: null, connection: null, user.Id, now);

        context.SaveChanges();
    }

    private Guid SeedEpisode(
        MediaServerDbContext context, Catalog catalog, MediaItem series, MediaItem season, int number,
        IngestItem? ingest, WatchHistoryProviderConnection? connection, int userId, DateTimeOffset now)
    {
        var relativePath = $"Breaking Bad/Season 1/S01E0{number}.mkv";
        var absolutePath = AbsolutePath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        File.WriteAllText(absolutePath, "x");

        var episode = new MediaItem
        {
            Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString("N"), CatalogId = catalog.Id,
            Kind = MediaKind.Episode, Title = $"Episode {number}", ParentId = season.Id,
            SeriesId = series.Id, SeasonId = season.Id, ParentIndexNumber = 1, IndexNumber = number,
            LibraryPath = relativePath, AddedAt = now, UpdatedAt = now,
        };
        context.MediaItems.Add(episode);
        context.MediaSources.Add(new MediaSource
        {
            Id = Guid.NewGuid(), MediaItemId = episode.Id, Container = "matroska", Path = relativePath,
            SizeBytes = 1, DurationTicks = 1, CreatedAt = now,
        });

        if (ingest is not null)
        {
            context.SourceFiles.Add(new SourceFile
            {
                Id = Guid.NewGuid(), IngestItemId = ingest.Id, RelativePath = relativePath,
                SizeBytes = 1, MediaItemId = episode.Id,
            });
            context.UserItemData.Add(new UserItemData
            {
                Id = Guid.NewGuid(), AppUserId = userId, MediaItemId = episode.Id, Played = true,
                PlayCount = 1, LastPlayedDate = now,
            });
            context.PlaybackHistoryEntries.Add(new PlaybackHistoryEntry
            {
                Id = Guid.NewGuid(), AppUserId = userId, MediaItemId = episode.Id, CreatedAt = now,
                WatchedAt = now, Origin = PlaybackHistoryOrigin.LocalPlayback, PlaySessionId = "session-1",
            });
            context.WatchHistoryOutboxEvents.Add(new WatchHistoryOutboxEvent
            {
                Id = Guid.NewGuid(), ConnectionId = connection!.Id, AppUserId = userId,
                MediaItemId = episode.Id, Operation = WatchHistoryOutboxOperation.AddExactWatch,
                IdentitySnapshot = "{}", IdempotencyKey = $"key-{number}", CreatedAt = now,
                NextAttemptAt = now,
            });
        }

        return episode.Id;
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
