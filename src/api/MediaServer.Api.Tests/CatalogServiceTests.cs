using MediaServer.Api.Catalogs;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MediaServer.Api.Tests;

public sealed class CatalogServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly List<string> _commands = [];
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "ms-catalog-" + Guid.NewGuid().ToString("N"));

    public CatalogServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>()
            .UseSqlite(_connection)
            .LogTo(_commands.Add, [DbLoggerCategory.Database.Command.Name], LogLevel.Information)
            .Options);
        _database.Database.Migrate();
        Directory.CreateDirectory(_tempRoot);
    }

    private CatalogService CreateService(MediaServerSettings? settings = null)
    {
        var filesystem = new FilesystemInspector();
        return new CatalogService(_database, filesystem, settings ?? new MediaServerSettings());
    }

    private CreateCatalogRequest Request(string root) =>
        new("Movies", CatalogType.Movie, root, null, null, null, false, null);

    private static CreateCatalogRequest MountRequest(string label, string? relativePath) =>
        new("Movies", CatalogType.Movie, null, label, relativePath, null, false, null);

    [Fact]
    public async Task Creates_catalog_with_incoming_staging_dir()
    {
        var service = CreateService();

        var catalog = await service.CreateAsync(Request(_tempRoot), CancellationToken.None);

        Assert.Equal("Movies", catalog.Name);
        Assert.True(catalog.Online);
        Assert.True(catalog.FreeBytes > 0);
        Assert.True(Directory.Exists(Path.Combine(_tempRoot, ".incoming")));
    }

    [Fact]
    public async Task Rejects_duplicate_root()
    {
        var service = CreateService();
        await service.CreateAsync(Request(_tempRoot), CancellationToken.None);

        await Assert.ThrowsAsync<CatalogValidationException>(() => service.CreateAsync(Request(_tempRoot), CancellationToken.None));
    }

    [Fact]
    public async Task Creates_missing_root_when_parent_is_reachable()
    {
        var service = CreateService();
        var root = Path.Combine(_tempRoot, "movies");

        var catalog = await service.CreateAsync(Request(root), CancellationToken.None);

        Assert.True(catalog.Online);
        Assert.True(Directory.Exists(root));
        Assert.True(Directory.Exists(Path.Combine(root, ".incoming")));
    }

    [Fact]
    public async Task Rejects_root_whose_parent_is_unreachable()
    {
        var service = CreateService();
        var unreachable = Path.Combine(_tempRoot, "does-not-exist", "movies");

        await Assert.ThrowsAsync<CatalogValidationException>(() => service.CreateAsync(Request(unreachable), CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_root_outside_configured_mounts()
    {
        var settings = new MediaServerSettings { CatalogMountRoots = [new CatalogMount("allowed", "/mnt/allowed")] };
        var service = CreateService(settings);

        await Assert.ThrowsAsync<CatalogValidationException>(() => service.CreateAsync(Request(_tempRoot), CancellationToken.None));
    }

    [Fact]
    public async Task Creates_catalog_anchored_to_a_mount_label()
    {
        var settings = new MediaServerSettings { CatalogMountRoots = [new CatalogMount("media", _tempRoot)] };
        var service = CreateService(settings);

        var catalog = await service.CreateAsync(MountRequest("media", "movies"), CancellationToken.None);

        // The label + relative path are the durable identity; the root is where that mount is right now.
        Assert.Equal("media", catalog.MountLabel);
        Assert.Equal("movies", catalog.MountRelativePath);
        Assert.Equal(Path.Combine(_tempRoot, "movies"), catalog.Root);
        Assert.False(catalog.Unanchored);
        Assert.True(Directory.Exists(Path.Combine(_tempRoot, "movies", ".incoming")));
    }

    [Fact]
    public async Task Anchors_a_free_text_root_that_lands_inside_a_mount()
    {
        var settings = new MediaServerSettings { CatalogMountRoots = [new CatalogMount("media", _tempRoot)] };
        var service = CreateService(settings);

        var catalog = await service.CreateAsync(Request(Path.Combine(_tempRoot, "movies")), CancellationToken.None);

        Assert.Equal("media", catalog.MountLabel);
        Assert.Equal("movies", catalog.MountRelativePath);
    }

    [Fact]
    public async Task Creating_at_the_mount_root_records_an_empty_relative_path()
    {
        var settings = new MediaServerSettings { CatalogMountRoots = [new CatalogMount("media", _tempRoot)] };
        var service = CreateService(settings);

        var catalog = await service.CreateAsync(MountRequest("media", null), CancellationToken.None);

        Assert.Equal("media", catalog.MountLabel);
        Assert.Equal(string.Empty, catalog.MountRelativePath);
        Assert.Equal(_tempRoot, catalog.Root);
    }

    [Fact]
    public async Task Rejects_a_mount_label_this_runtime_does_not_provide()
    {
        var settings = new MediaServerSettings { CatalogMountRoots = [new CatalogMount("media", _tempRoot)] };
        var service = CreateService(settings);

        await Assert.ThrowsAsync<CatalogValidationException>(
            () => service.CreateAsync(MountRequest("elsewhere", "movies"), CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_a_second_catalog_at_the_same_place_within_a_mount()
    {
        var settings = new MediaServerSettings { CatalogMountRoots = [new CatalogMount("media", _tempRoot)] };
        var service = CreateService(settings);
        await service.CreateAsync(MountRequest("media", "movies"), CancellationToken.None);

        await Assert.ThrowsAsync<CatalogValidationException>(
            () => service.CreateAsync(MountRequest("media", "movies"), CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_a_second_catalog_that_reaches_the_same_directory_by_another_spelling()
    {
        var settings = new MediaServerSettings { CatalogMountRoots = [new CatalogMount("media", _tempRoot)] };
        var service = CreateService(settings);
        await service.CreateAsync(MountRequest("media", "movies"), CancellationToken.None);

        // Same directory, written differently — the stored location has to reduce to one value, or both
        // catalogs would scan and organize the same files.
        await Assert.ThrowsAsync<CatalogValidationException>(
            () => service.CreateAsync(MountRequest("media", "series/../movies"), CancellationToken.None));
        // Same mount, different casing of its label.
        await Assert.ThrowsAsync<CatalogValidationException>(
            () => service.CreateAsync(MountRequest("MEDIA", "movies"), CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_a_path_that_climbs_out_of_its_mount()
    {
        var settings = new MediaServerSettings { CatalogMountRoots = [new CatalogMount("media", _tempRoot)] };
        var service = CreateService(settings);

        await Assert.ThrowsAsync<CatalogValidationException>(
            () => service.CreateAsync(MountRequest("media", "../elsewhere"), CancellationToken.None));
    }

    [Fact]
    public async Task Stores_the_mounts_own_label_regardless_of_the_requested_casing()
    {
        var settings = new MediaServerSettings { CatalogMountRoots = [new CatalogMount("media", _tempRoot)] };
        var service = CreateService(settings);

        var catalog = await service.CreateAsync(MountRequest("MEDIA", "movies"), CancellationToken.None);

        Assert.Equal("media", catalog.MountLabel);
    }

    [Fact]
    public async Task Reports_a_stale_label_as_unanchored_even_when_the_root_still_sits_inside_a_mount()
    {
        var created = await CreateService(new MediaServerSettings { CatalogMountRoots = [new CatalogMount("media", _tempRoot)] })
            .CreateAsync(MountRequest("media", "movies"), CancellationToken.None);

        // The mount was renamed but kept its path. The root still looks at home, yet the stored label is
        // dead — it would resolve to nothing on the next runtime, so the operator has to be told now.
        var renamed = CreateService(new MediaServerSettings
        {
            CatalogMountRoots = [new CatalogMount("media-renamed", _tempRoot)],
        });

        var catalog = await renamed.GetAsync(created.Id, CancellationToken.None);

        Assert.NotNull(catalog);
        Assert.True(catalog.Unanchored);
    }

    [Fact]
    public async Task Reports_a_catalog_outside_every_mount_of_this_runtime_as_unanchored()
    {
        // Created under one runtime's paths…
        var created = await CreateService(new MediaServerSettings { CatalogMountRoots = [new CatalogMount("media", _tempRoot)] })
            .CreateAsync(MountRequest("media", "movies"), CancellationToken.None);

        // …then read back under a runtime whose "media" mount is somewhere else entirely.
        var elsewhere = CreateService(new MediaServerSettings
        {
            CatalogMountRoots = [new CatalogMount("other", Path.Combine(_tempRoot, "other-runtime"))],
        });

        var catalog = await elsewhere.GetAsync(created.Id, CancellationToken.None);

        // Unanchored is about the mount table, not the disk: reported even here, where the stored path
        // happens to still exist (in the real docker/dev switch it does not).
        Assert.NotNull(catalog);
        Assert.True(catalog.Unanchored);
    }

    [Fact]
    public async Task Does_not_report_unanchored_on_a_standalone_run()
    {
        var service = CreateService();

        var catalog = await service.CreateAsync(Request(_tempRoot), CancellationToken.None);

        // No mounts injected: a free-text root is the whole story, so there is nothing to be anchored to.
        Assert.Null(catalog.MountLabel);
        Assert.False(catalog.Unanchored);
    }

    [Fact]
    public async Task Delete_removes_metadata_but_keeps_physical_files()
    {
        var service = CreateService();
        var catalog = await service.CreateAsync(Request(_tempRoot), CancellationToken.None);

        // A published item whose library file exists on disk.
        var now = DateTimeOffset.UtcNow;
        var item = new MediaItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Kind = MediaKind.Movie, Title = "Inception", Year = 2010, AddedAt = now, UpdatedAt = now };
        var source = new MediaSource { Id = Guid.NewGuid(), MediaItemId = item.Id, Container = "mkv", Path = "Inception (2010)/Inception.mkv", SizeBytes = 5, DurationTicks = 0, CreatedAt = now };
        _database.AddRange(item, source);
        await _database.SaveChangesAsync();

        var libraryFile = Path.Combine(_tempRoot, "library", "Inception (2010)", "Inception.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(libraryFile)!);
        await File.WriteAllTextAsync(libraryFile, "movie");

        var deleted = await service.DeleteAsync(catalog.Id, CancellationToken.None);

        Assert.True(deleted);
        // DB metadata is gone (catalog + cascade-deleted media rows)…
        Assert.Empty(await _database.Catalogs.ToListAsync());
        Assert.Empty(await _database.MediaItems.ToListAsync());
        Assert.Empty(await _database.MediaSources.ToListAsync());
        // …but the physical files (and the catalog directory) are left untouched on disk.
        Assert.True(File.Exists(libraryFile));
        Assert.True(Directory.Exists(_tempRoot));
    }

    [Fact]
    public async Task Delete_does_not_scale_its_parameters_with_the_size_of_the_catalog()
    {
        var service = CreateService();
        var catalog = await service.CreateAsync(Request(_tempRoot), CancellationToken.None);

        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < 300; index++)
        {
            var movie = new MediaItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Kind = MediaKind.Movie, Title = $"Movie {index}", Year = 2000, AddedAt = now, UpdatedAt = now };
            _database.Add(movie);
            _database.Add(new MediaSource { Id = Guid.NewGuid(), MediaItemId = movie.Id, Container = "mkv", Path = $"Movie {index}/Movie {index}.mkv", SizeBytes = 5, DurationTicks = 0, CreatedAt = now });
        }

        await _database.SaveChangesAsync();
        _commands.Clear();

        var deleted = await service.DeleteAsync(catalog.Id, CancellationToken.None);

        Assert.True(deleted);
        Assert.Empty(await _database.Catalogs.ToListAsync());
        Assert.Empty(await _database.MediaItems.ToListAsync());
        Assert.Empty(await _database.MediaSources.ToListAsync());

        // A catalog is unbounded, so the ids must never leave the database: materializing them makes EF
        // expand Contains into one host parameter per id, which SQLite caps (and wastes a round trip at any
        // size). Assert the shape rather than the row count — the cap is high enough that a functional test
        // would not fail until tens of thousands of items.
        var sourceDelete = Assert.Single(_commands, entry => entry.Contains("DELETE FROM \"MediaSources\"", StringComparison.Ordinal));
        Assert.Contains("SELECT", sourceDelete, StringComparison.Ordinal);
        Assert.DoesNotContain("@ids", sourceDelete, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_clears_a_series_hierarchy_without_tripping_the_parent_foreign_key()
    {
        var service = CreateService();
        var catalog = await service.CreateAsync(Request(_tempRoot), CancellationToken.None);

        // Series → Season → Episode: MediaItem.ParentId is Restrict, so the rows must go leaves-first.
        var now = DateTimeOffset.UtcNow;
        var series = new MediaItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Kind = MediaKind.Series, Title = "Breaking Bad", AddedAt = now, UpdatedAt = now };
        var season = new MediaItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Kind = MediaKind.Season, Title = "Season 1", ParentId = series.Id, SeriesId = series.Id, IndexNumber = 1, AddedAt = now, UpdatedAt = now };
        var episode = new MediaItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Kind = MediaKind.Episode, Title = "Pilot", ParentId = season.Id, SeriesId = series.Id, ParentIndexNumber = 1, IndexNumber = 1, AddedAt = now, UpdatedAt = now };
        var source = new MediaSource { Id = Guid.NewGuid(), MediaItemId = episode.Id, Container = "mkv", Path = "Breaking Bad/S01/E01.mkv", SizeBytes = 5, DurationTicks = 0, CreatedAt = now };
        _database.AddRange(series, season, episode, source);
        await _database.SaveChangesAsync();

        var deleted = await service.DeleteAsync(catalog.Id, CancellationToken.None);

        Assert.True(deleted);
        Assert.Empty(await _database.Catalogs.ToListAsync());
        Assert.Empty(await _database.MediaItems.ToListAsync());
        Assert.Empty(await _database.MediaSources.ToListAsync());
    }

    [Fact]
    public async Task Delete_tombstones_a_favorited_movie_catalog_less()
    {
        var service = CreateService();
        var catalog = await service.CreateAsync(Request(_tempRoot), CancellationToken.None);

        var now = DateTimeOffset.UtcNow;
        var user = new AppUser { HostUserId = "host-1", Email = "user@example.com", Role = AppUserRole.User, CreatedAt = now, LastSeenAt = now };
        _database.AppUsers.Add(user);
        var loved = new MediaItem { Id = Guid.NewGuid(), PublicId = "pub-loved", CatalogId = catalog.Id, Kind = MediaKind.Movie, Title = "Inception", Year = 2010, AddedAt = now, UpdatedAt = now };
        var untouched = new MediaItem { Id = Guid.NewGuid(), PublicId = "pub-other", CatalogId = catalog.Id, Kind = MediaKind.Movie, Title = "Tenet", Year = 2020, AddedAt = now, UpdatedAt = now };
        _database.AddRange(loved, untouched);
        _database.MediaSources.Add(new MediaSource { Id = Guid.NewGuid(), MediaItemId = loved.Id, Container = "mkv", Path = "Inception (2010)/Inception.mkv", SizeBytes = 5, DurationTicks = 0, CreatedAt = now });
        _database.MetadataRecords.Add(new MetadataRecord { Id = Guid.NewGuid(), MediaItemId = loved.Id, Provider = "tmdb", Language = "en", Title = "Inception", FetchedAt = now });
        await _database.SaveChangesAsync();
        _database.UserItemData.Add(new UserItemData { Id = Guid.NewGuid(), AppUserId = user.Id, MediaItemId = loved.Id, IsFavorite = true });
        await _database.SaveChangesAsync();
        _database.ChangeTracker.Clear();

        var deleted = await service.DeleteAsync(catalog.Id, CancellationToken.None);

        Assert.True(deleted);
        Assert.Empty(await _database.Catalogs.ToListAsync());
        // The favorited movie survives its catalog: unpublished, catalog-less, sourceless — but with
        // its favorite and metadata intact. The untouched one is purged with the catalog as before.
        var ghost = await _database.MediaItems.SingleAsync();
        Assert.Equal(loved.Id, ghost.Id);
        Assert.Null(ghost.CatalogId);
        Assert.Null(ghost.PublicId);
        Assert.NotNull(ghost.RemovedAt);
        Assert.Empty(await _database.MediaSources.ToListAsync());
        Assert.True(await _database.UserItemData.AnyAsync(data => data.MediaItemId == loved.Id && data.IsFavorite));
        Assert.True(await _database.MetadataRecords.AnyAsync(record => record.MediaItemId == loved.Id));
    }

    [Fact]
    public async Task Delete_tombstones_the_ancestor_chain_of_a_watched_episode()
    {
        var service = CreateService();
        var catalog = await service.CreateAsync(Request(_tempRoot), CancellationToken.None);

        var now = DateTimeOffset.UtcNow;
        var user = new AppUser { HostUserId = "host-1", Email = "user@example.com", Role = AppUserRole.User, CreatedAt = now, LastSeenAt = now };
        _database.AppUsers.Add(user);
        var series = new MediaItem { Id = Guid.NewGuid(), PublicId = "pub-series", CatalogId = catalog.Id, Kind = MediaKind.Series, Title = "Breaking Bad", AddedAt = now, UpdatedAt = now };
        var season = new MediaItem { Id = Guid.NewGuid(), PublicId = "pub-season", CatalogId = catalog.Id, Kind = MediaKind.Season, Title = "Season 1", ParentId = series.Id, SeriesId = series.Id, IndexNumber = 1, AddedAt = now, UpdatedAt = now };
        var watched = new MediaItem { Id = Guid.NewGuid(), PublicId = "pub-e1", CatalogId = catalog.Id, Kind = MediaKind.Episode, Title = "Pilot", ParentId = season.Id, SeriesId = series.Id, SeasonId = season.Id, ParentIndexNumber = 1, IndexNumber = 1, AddedAt = now, UpdatedAt = now };
        var unwatched = new MediaItem { Id = Guid.NewGuid(), PublicId = "pub-e2", CatalogId = catalog.Id, Kind = MediaKind.Episode, Title = "Cat's in the Bag…", ParentId = season.Id, SeriesId = series.Id, SeasonId = season.Id, ParentIndexNumber = 1, IndexNumber = 2, AddedAt = now, UpdatedAt = now };
        _database.AddRange(series, season, watched, unwatched);
        await _database.SaveChangesAsync();
        _database.PlaybackHistoryEntries.Add(new PlaybackHistoryEntry
        {
            Id = Guid.NewGuid(), AppUserId = user.Id, MediaItemId = watched.Id, CreatedAt = now,
            WatchedAt = now, Origin = PlaybackHistoryOrigin.LocalPlayback,
        });
        await _database.SaveChangesAsync();
        _database.ChangeTracker.Clear();

        var deleted = await service.DeleteAsync(catalog.Id, CancellationToken.None);

        Assert.True(deleted);
        // The play on episode 1 keeps its whole ancestor chain as catalog-less tombstones; the
        // unwatched sibling goes with the catalog.
        var remaining = await _database.MediaItems.ToListAsync();
        Assert.Equal(3, remaining.Count);
        Assert.Contains(remaining, item => item.Id == series.Id);
        Assert.Contains(remaining, item => item.Id == season.Id);
        Assert.Contains(remaining, item => item.Id == watched.Id);
        Assert.All(remaining, item =>
        {
            Assert.Null(item.CatalogId);
            Assert.Null(item.PublicId);
            Assert.NotNull(item.RemovedAt);
        });
        Assert.True(await _database.PlaybackHistoryEntries.AnyAsync(entry => entry.MediaItemId == watched.Id));
    }

    [Fact]
    public async Task Delete_is_blocked_while_a_download_references_the_catalog()
    {
        var service = CreateService();
        var catalog = await service.CreateAsync(Request(_tempRoot), CancellationToken.None);

        var now = DateTimeOffset.UtcNow;
        _database.Downloads.Add(new Download
        {
            Id = Guid.NewGuid(),
            InfoHash = "hash-1",
            CatalogId = catalog.Id,
            State = DownloadState.Downloading,
            SavePath = _tempRoot,
            AddedAt = now,
        });
        await _database.SaveChangesAsync();

        await Assert.ThrowsAsync<CatalogInUseException>(() => service.DeleteAsync(catalog.Id, CancellationToken.None));

        // The catalog (and its directory) survive the rejected delete.
        Assert.Single(await _database.Catalogs.ToListAsync());
        Assert.True(Directory.Exists(_tempRoot));
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
