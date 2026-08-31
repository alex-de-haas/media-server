using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Library;

/// <summary>
/// Coverage for deleting parts of a series: one episode, a whole season, the container pruning that
/// follows, and the tombstone-vs-purge decision — an item some user watched or favorited survives
/// deletion as an unpublished tombstone unless <c>deleteUserData</c> forces the full purge. Episode 1
/// is seeded with user signal (watched + a play), episode 2 without, so each test exercises both
/// paths. Each test seeds its own catalog with a real temp root so file erasure is exercised for real.
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
    private int _userId;

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
        var result = await Service().DeleteEpisodeAsync(_episode1Id, deleteFiles: false, deleteUserData: false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.SeasonRemoved);
        Assert.False(result.SeriesRemoved);

        await using var verify = _db.Create();
        // Episode 1 was watched, so the delete leaves a tombstone: unpublished, sourceless, but present.
        var ghost = await verify.MediaItems.SingleAsync(item => item.Id == _episode1Id);
        Assert.Null(ghost.PublicId);
        Assert.NotNull(ghost.RemovedAt);
        Assert.Null(ghost.LibraryPath);
        Assert.False(await verify.MediaSources.AnyAsync(source => source.MediaItemId == _episode1Id));
        Assert.NotNull((await verify.MediaItems.SingleAsync(item => item.Id == _episode2Id)).PublicId);
        Assert.NotNull((await verify.MediaItems.SingleAsync(item => item.Id == _seasonId)).PublicId);
        Assert.NotNull((await verify.MediaItems.SingleAsync(item => item.Id == _seriesId)).PublicId);
    }

    [Fact]
    public async Task A_watched_episode_survives_deletion_with_its_user_data_and_history()
    {
        // The ingest keeps its file row (a rescan can re-adopt it); the per-user state and the plays
        // stay on the tombstone, and the outbox row keeps its frozen identity as before. Transient
        // playback sessions are dropped — a ghost cannot be played.
        await using (var seed = _db.Create())
        {
            seed.PlaybackSessions.Add(new PlaybackSession
            {
                Id = Guid.NewGuid(), AppUserId = _userId, MediaItemId = _episode1Id,
                SessionKey = "client-1", StartedAt = DateTimeOffset.UtcNow, LastReportAt = DateTimeOffset.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var result = await Service().DeleteEpisodeAsync(_episode1Id, deleteFiles: false, deleteUserData: false, CancellationToken.None);
        Assert.NotNull(result);

        await using var verify = _db.Create();
        var sourceFile = await verify.SourceFiles.SingleAsync();
        Assert.Null(sourceFile.MediaItemId);
        Assert.True(await verify.UserItemData.AnyAsync(data => data.MediaItemId == _episode1Id));
        Assert.True(await verify.PlaybackHistoryEntries.AnyAsync(entry => entry.MediaItemId == _episode1Id));
        Assert.True(await verify.WatchHistoryOutboxEvents.AnyAsync(item => item.MediaItemId == _episode1Id));
        Assert.False(await verify.PlaybackSessions.AnyAsync(session => session.MediaItemId == _episode1Id));
    }

    [Fact]
    public async Task Delete_user_data_forces_the_full_purge_of_a_watched_episode()
    {
        var result = await Service().DeleteEpisodeAsync(_episode1Id, deleteFiles: false, deleteUserData: true, CancellationToken.None);
        Assert.NotNull(result);

        await using var verify = _db.Create();
        Assert.False(await verify.MediaItems.AnyAsync(item => item.Id == _episode1Id));
        Assert.False(await verify.UserItemData.AnyAsync(data => data.MediaItemId == _episode1Id));
        // History follows the item by DB cascade; the outbox row has no FK and keeps its frozen identity.
        Assert.False(await verify.PlaybackHistoryEntries.AnyAsync(entry => entry.MediaItemId == _episode1Id));
        Assert.True(await verify.WatchHistoryOutboxEvents.AnyAsync(item => item.MediaItemId == _episode1Id));
    }

    [Fact]
    public async Task An_untouched_episode_is_purged_not_tombstoned()
    {
        var result = await Service().DeleteEpisodeAsync(_episode2Id, deleteFiles: false, deleteUserData: false, CancellationToken.None);
        Assert.NotNull(result);

        await using var verify = _db.Create();
        Assert.False(await verify.MediaItems.AnyAsync(item => item.Id == _episode2Id));
    }

    [Fact]
    public async Task A_tombstone_keeps_its_metadata_and_artwork()
    {
        await using (var seed = _db.Create())
        {
            seed.MetadataRecords.Add(new MetadataRecord
            {
                Id = Guid.NewGuid(), MediaItemId = _episode1Id, Provider = "tmdb", Language = "en",
                Title = "Pilot", FetchedAt = DateTimeOffset.UtcNow,
            });
            seed.ImageAssets.Add(new ImageAsset
            {
                Id = Guid.NewGuid(), MediaItemId = _episode1Id, ImageType = ImageType.Primary,
                Provider = "tmdb", RemotePath = "/pilot.jpg", Tag = "tag-1",
            });
            await seed.SaveChangesAsync();
        }

        await Service().DeleteEpisodeAsync(_episode1Id, deleteFiles: false, deleteUserData: false, CancellationToken.None);

        await using var verify = _db.Create();
        Assert.True(await verify.MetadataRecords.AnyAsync(record => record.MediaItemId == _episode1Id));
        Assert.True(await verify.ImageAssets.AnyAsync(image => image.MediaItemId == _episode1Id));
    }

    [Fact]
    public async Task Deleting_an_episode_with_files_erases_only_that_episodes_file()
    {
        var kept = AbsolutePath("Breaking Bad/Season 1/S01E02.mkv");
        var erased = AbsolutePath("Breaking Bad/Season 1/S01E01.mkv");

        var result = await Service().DeleteEpisodeAsync(_episode1Id, deleteFiles: true, deleteUserData: false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(File.Exists(erased)); // a tombstone loses its files like any delete
        Assert.True(File.Exists(kept)); // the season folder stays: a sibling still lives there
    }

    [Fact]
    public async Task Deleting_an_episode_without_files_leaves_the_file_for_a_rescan()
    {
        var path = AbsolutePath("Breaking Bad/Season 1/S01E01.mkv");

        await Service().DeleteEpisodeAsync(_episode1Id, deleteFiles: false, deleteUserData: false, CancellationToken.None);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Deleting_the_last_episode_tombstones_the_emptied_containers()
    {
        await Service().DeleteEpisodeAsync(_episode1Id, deleteFiles: false, deleteUserData: false, CancellationToken.None);
        var result = await Service().DeleteEpisodeAsync(_episode2Id, deleteFiles: false, deleteUserData: false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.SeasonRemoved);
        Assert.True(result.SeriesRemoved);

        await using var verify = _db.Create();
        // Nothing is published any more, but the watched episode's ancestors survive as tombstones so
        // the ghost keeps a valid parent chain: exactly episode 1, its season, and its series remain.
        Assert.False(await verify.MediaItems.AnyAsync(item => item.PublicId != null));
        var remaining = await verify.MediaItems.Select(item => item.Id).ToListAsync();
        Assert.Equal(3, remaining.Count);
        Assert.Contains(_episode1Id, remaining);
        Assert.Contains(_seasonId, remaining);
        Assert.Contains(_seriesId, remaining);
        Assert.True(await verify.MediaItems.AllAsync(item => item.RemovedAt != null));
    }

    [Fact]
    public async Task Purging_the_last_episode_prunes_the_containers_entirely()
    {
        await Service().DeleteEpisodeAsync(_episode1Id, deleteFiles: false, deleteUserData: true, CancellationToken.None);
        var result = await Service().DeleteEpisodeAsync(_episode2Id, deleteFiles: false, deleteUserData: true, CancellationToken.None);

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

        await Service().DeleteEpisodeAsync(_episode1Id, deleteFiles: false, deleteUserData: false, CancellationToken.None);
        var result = await Service().DeleteEpisodeAsync(_episode2Id, deleteFiles: false, deleteUserData: false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.SeasonRemoved);
        Assert.False(result.SeriesRemoved);

        await using var verify = _db.Create();
        Assert.NotNull((await verify.MediaItems.SingleAsync(item => item.Id == extraId)).PublicId);
        Assert.NotNull((await verify.MediaItems.SingleAsync(item => item.Id == _seasonId)).PublicId);
        Assert.NotNull((await verify.MediaItems.SingleAsync(item => item.Id == _seriesId)).PublicId);
    }

    [Fact]
    public async Task A_series_level_extra_keeps_the_series_after_its_season_is_tombstoned()
    {
        var extraId = await AddExtraAsync(seasonScoped: false);

        await Service().DeleteEpisodeAsync(_episode1Id, deleteFiles: false, deleteUserData: false, CancellationToken.None);
        var result = await Service().DeleteEpisodeAsync(_episode2Id, deleteFiles: false, deleteUserData: false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.SeasonRemoved);
        Assert.False(result.SeriesRemoved);

        await using var verify = _db.Create();
        // The season left the library (ghost episode 1 keeps its row alive, unpublished) while the
        // series stays published for its extra.
        var season = await verify.MediaItems.SingleAsync(item => item.Id == _seasonId);
        Assert.Null(season.PublicId);
        Assert.NotNull(season.RemovedAt);
        Assert.NotNull((await verify.MediaItems.SingleAsync(item => item.Id == extraId)).PublicId);
        Assert.NotNull((await verify.MediaItems.SingleAsync(item => item.Id == _seriesId)).PublicId);
    }

    [Fact]
    public async Task Deleting_a_season_takes_its_episodes_its_extras_and_the_series_it_emptied()
    {
        var extraId = await AddExtraAsync(seasonScoped: true);

        var result = await Service().DeleteSeasonAsync(_seasonId, deleteFiles: true, deleteUserData: false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.SeasonRemoved);
        Assert.True(result.SeriesRemoved);
        Assert.False(File.Exists(AbsolutePath("Breaking Bad/Season 1/S01E01.mkv")));
        Assert.False(File.Exists(AbsolutePath("Breaking Bad/Season 1/S01E02.mkv")));

        await using var verify = _db.Create();
        // The untouched episode and the extra are purged; the watched episode and its ancestor chain
        // remain as tombstones. No sources survive on anything.
        Assert.False(await verify.MediaItems.AnyAsync(item => item.Id == extraId || item.Id == _episode2Id));
        Assert.False(await verify.MediaItems.AnyAsync(item => item.PublicId != null));
        Assert.True(await verify.MediaItems.AnyAsync(item => item.Id == _episode1Id && item.RemovedAt != null));
        Assert.True(await verify.MediaItems.AnyAsync(item => item.Id == _seasonId && item.RemovedAt != null));
        Assert.True(await verify.MediaItems.AnyAsync(item => item.Id == _seriesId && item.RemovedAt != null));
        Assert.False(await verify.MediaSources.AnyAsync());
    }

    [Fact]
    public async Task The_episode_route_refuses_anything_that_is_not_a_published_episode()
    {
        var service = Service();

        Assert.Null(await service.DeleteEpisodeAsync(_seriesId, deleteFiles: false, deleteUserData: false, CancellationToken.None));
        Assert.Null(await service.DeleteEpisodeAsync(_seasonId, deleteFiles: false, deleteUserData: false, CancellationToken.None));
        Assert.Null(await service.DeleteEpisodeAsync(Guid.NewGuid(), deleteFiles: false, deleteUserData: false, CancellationToken.None));
        Assert.Null(await service.DeleteSeasonAsync(_episode1Id, deleteFiles: false, deleteUserData: false, CancellationToken.None));
        Assert.Null(await service.DeleteSeasonAsync(_seriesId, deleteFiles: false, deleteUserData: false, CancellationToken.None));
    }

    [Fact]
    public async Task An_unpublished_episode_is_not_deletable()
    {
        await using (var seed = _db.Create())
        {
            await seed.MediaItems.Where(item => item.Id == _episode1Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.PublicId, (string?)null));
        }

        Assert.Null(await Service().DeleteEpisodeAsync(_episode1Id, deleteFiles: false, deleteUserData: false, CancellationToken.None));
    }

    [Fact]
    public async Task A_favorited_movie_survives_deletion_as_a_tombstone()
    {
        // Favorite alone is signal — no play required. This is the case the calendar cannot show.
        var movieId = await AddMovieAsync(favorite: true);

        var deleted = await Service().DeleteAsync(movieId, deleteFiles: false, deleteUserData: false, CancellationToken.None);

        Assert.True(deleted);
        await using var verify = _db.Create();
        var ghost = await verify.MediaItems.SingleAsync(item => item.Id == movieId);
        Assert.Null(ghost.PublicId);
        Assert.NotNull(ghost.RemovedAt);
        Assert.Null(ghost.DefaultSourceId);
        Assert.True(await verify.UserItemData.AnyAsync(data => data.MediaItemId == movieId && data.IsFavorite));
    }

    [Fact]
    public async Task A_rated_movie_survives_deletion_as_a_tombstone()
    {
        // A rating is signal on its own — never favorited, never played through this instance, but the
        // viewer stated a verdict, and a delete that discarded it would lose something unrecoverable.
        var movieId = await AddMovieAsync(favorite: false, rating: 5);

        var deleted = await Service().DeleteAsync(movieId, deleteFiles: false, deleteUserData: false, CancellationToken.None);

        Assert.True(deleted);
        await using var verify = _db.Create();
        Assert.NotNull((await verify.MediaItems.SingleAsync(item => item.Id == movieId)).RemovedAt);
        Assert.Equal(5, (await verify.UserItemData.SingleAsync(data => data.MediaItemId == movieId)).Rating);
    }

    [Fact]
    public async Task An_untouched_movie_is_purged_on_delete()
    {
        var movieId = await AddMovieAsync(favorite: false);

        var deleted = await Service().DeleteAsync(movieId, deleteFiles: false, deleteUserData: false, CancellationToken.None);

        Assert.True(deleted);
        await using var verify = _db.Create();
        Assert.False(await verify.MediaItems.AnyAsync(item => item.Id == movieId));
    }

    [Fact]
    public async Task A_watched_movie_survives_deletion_on_its_history_alone()
    {
        // What keeps a played title alive is the entry, not the counter: the aggregates are a projection
        // of exactly this row, and every path that marks something watched writes one.
        var movieId = await AddMovieAsync(favorite: false);
        await AddHistoryAsync(movieId);

        Assert.True(await Service().DeleteAsync(movieId, deleteFiles: false, deleteUserData: false, CancellationToken.None));

        await using var verify = _db.Create();
        Assert.NotNull((await verify.MediaItems.SingleAsync(item => item.Id == movieId)).RemovedAt);
        Assert.True(await verify.PlaybackHistoryEntries.AnyAsync(entry => entry.MediaItemId == movieId));
    }

    [Fact]
    public async Task Aggregate_counters_alone_do_not_keep_a_movie_alive()
    {
        // A row whose counters drifted from the history they came from — a play whose entry the user
        // deleted from the calendar, or an import that wrote aggregates and no entries. Nothing here is
        // a statement about the film, and nothing here can be cleared through the UI, so counting it
        // would make the ghost immortal.
        var movieId = await AddMovieAsync(favorite: false);
        await using (var seed = _db.Create())
        {
            seed.UserItemData.Add(new UserItemData
            {
                Id = Guid.NewGuid(), AppUserId = _userId, MediaItemId = movieId,
                Played = true, PlayCount = 3, PlaybackPositionTicks = 42,
            });
            await seed.SaveChangesAsync();
        }

        Assert.True(await Service().DeleteAsync(movieId, deleteFiles: false, deleteUserData: false, CancellationToken.None));

        await using var verify = _db.Create();
        Assert.False(await verify.MediaItems.AnyAsync(item => item.Id == movieId));
    }

    [Fact]
    public async Task An_abandoned_half_watch_does_not_keep_a_movie_alive()
    {
        var movieId = await AddMovieAsync(favorite: false);
        await using (var seed = _db.Create())
        {
            seed.UserItemData.Add(new UserItemData
            {
                Id = Guid.NewGuid(), AppUserId = _userId, MediaItemId = movieId, PlaybackPositionTicks = 90_000_000,
            });
            await seed.SaveChangesAsync();
        }

        Assert.True(await Service().DeleteAsync(movieId, deleteFiles: false, deleteUserData: false, CancellationToken.None));

        await using var verify = _db.Create();
        Assert.False(await verify.MediaItems.AnyAsync(item => item.Id == movieId));
    }

    private async Task AddHistoryAsync(Guid mediaItemId)
    {
        await using var seed = _db.Create();
        seed.PlaybackHistoryEntries.Add(new PlaybackHistoryEntry
        {
            Id = Guid.NewGuid(),
            AppUserId = _userId,
            MediaItemId = mediaItemId,
            CreatedAt = DateTimeOffset.UtcNow,
            WatchedAt = DateTimeOffset.UtcNow,
            Origin = PlaybackHistoryOrigin.LocalPlayback,
        });
        await seed.SaveChangesAsync();
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

    private async Task<Guid> AddMovieAsync(bool favorite) => await AddMovieAsync(favorite, rating: null);

    private async Task<Guid> AddMovieAsync(bool favorite, int? rating)
    {
        var now = DateTimeOffset.UtcNow;
        await using var seed = _db.Create();
        var source = new MediaSource
        {
            Id = Guid.NewGuid(), MediaItemId = Guid.NewGuid(), Container = "matroska",
            Path = "Inception (2010)/Inception.mkv", SizeBytes = 1, DurationTicks = 1, CreatedAt = now,
        };
        var movie = new MediaItem
        {
            Id = source.MediaItemId, PublicId = Guid.NewGuid().ToString("N"), CatalogId = _catalogId,
            Kind = MediaKind.Movie, Title = "Inception", Year = 2010,
            IdentityProvider = "tmdb", IdentityProviderId = "27205",
            LibraryPath = "Inception (2010)", DefaultSourceId = source.Id, AddedAt = now, UpdatedAt = now,
        };
        seed.MediaItems.Add(movie);
        seed.MediaSources.Add(source);
        if (favorite || rating is not null)
        {
            seed.UserItemData.Add(new UserItemData
            {
                Id = Guid.NewGuid(), AppUserId = _userId, MediaItemId = movie.Id, IsFavorite = favorite,
                Rating = rating,
            });
        }

        await seed.SaveChangesAsync();
        return movie.Id;
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
        _userId = user.Id;
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
