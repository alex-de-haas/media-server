using MediaServer.Api.Metadata;
using MediaServer.Api.Catalogs;
using MediaServer.Api.Collections;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.IO;
using MediaServer.Api.Library;
using MediaServer.Api.People;
using MediaServer.Api.Pipeline;
using MediaServer.Api.Probe;
using MediaServer.Api.Tests.Pipeline;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Library;

public sealed class LibraryMaintenanceServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ms-maint-" + Guid.NewGuid().ToString("N"));
    private readonly FakeMetadataProvider _metadata = new();
    private readonly RecordingCore _core = new();

    /// <summary>The catalog the most recent <see cref="SeedCatalog"/> made — what a catalog-scoped
    /// backfill is pointed at.</summary>
    private Guid _catalogId;

    public LibraryMaintenanceServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();
        Directory.CreateDirectory(_root);
    }

    private LibraryMaintenanceService Service(FakeMediaProbe? probe = null) => new(
        _database,
        new CatalogPathSandbox(),
        probe ?? new FakeMediaProbe(),
        new EnrichService(_database, _metadata, new MediaServerSettings { SupportedLanguages = ["en-US"] }, new PersonSyncService(_database), new CollectionSyncService(_database), new MetadataTagSync(_database, NullLogger<MetadataTagSync>.Instance)),
        NullLogger<LibraryMaintenanceService>.Instance);

    private Catalog SeedCatalog(string? root = null)
    {
        var catalog = new Catalog
        {
            Id = Guid.NewGuid(),
            Name = "Movies",
            Type = CatalogType.Movie,
            Root = root ?? _root,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _database.Catalogs.Add(catalog);
        _database.SaveChanges();
        _catalogId = catalog.Id;
        return catalog;
    }

    private Guid SeedItemWithSource(Catalog catalog, string relativePath, bool identified = true)
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            CatalogId = catalog.Id,
            Kind = MediaKind.Movie,
            Title = "A Movie",
            LibraryPath = relativePath,
            IdentityProvider = identified ? "tmdb" : null,
            IdentityProviderId = identified ? "123" : null,
            AddedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _database.MediaItems.Add(item);
        _database.MediaSources.Add(new MediaSource
        {
            Id = Guid.NewGuid(),
            MediaItemId = item.Id,
            Container = "mkv",
            Path = relativePath,
            SizeBytes = 1024,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        _database.SaveChanges();
        return item.Id;
    }

    [Fact]
    public async Task Refresh_reenriches_an_identified_item()
    {
        var catalog = SeedCatalog();
        var itemId = SeedItemWithSource(catalog, Path.Combine("library", "A", "a.mkv"));

        var refreshed = await Service().RefreshMetadataAsync(itemId, CancellationToken.None);

        Assert.True(refreshed);
        await using var fresh = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        Assert.True(await fresh.MetadataRecords.AnyAsync(r => r.MediaItemId == itemId));
    }

    [Fact]
    public async Task Refresh_returns_false_for_unidentified_item_and_unknown_id()
    {
        var catalog = SeedCatalog();
        var unidentified = SeedItemWithSource(catalog, Path.Combine("library", "U", "u.mkv"), identified: false);

        Assert.False(await Service().RefreshMetadataAsync(unidentified, CancellationToken.None));
        Assert.False(await Service().RefreshMetadataAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task RefreshMedia_reprobes_sources_and_replaces_streams()
    {
        var catalog = SeedCatalog();
        var relative = Path.Combine("library", "A", "a.mkv");
        var absolute = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await File.WriteAllBytesAsync(absolute, new byte[16]);
        var itemId = SeedItemWithSource(catalog, relative);

        var refreshed = await Service().RefreshMediaAsync(itemId, CancellationToken.None);

        Assert.True(refreshed);
        await using var fresh = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        var source = await fresh.MediaSources.Include(s => s.Streams).SingleAsync(s => s.MediaItemId == itemId);
        // FakeMediaProbe returns a 2-stream matroska result, replacing the seeded (stream-less) source.
        Assert.Equal("matroska", source.Container);
        Assert.Equal(2, source.Streams.Count);
        Assert.Contains(source.Streams, s => s.StreamType == StreamType.Audio);
    }

    [Fact]
    public async Task Backfill_fills_in_the_dolby_vision_record_for_rows_labelled_before_it_was_stored()
    {
        // An engine row that says Dolby Vision without a profile: the one place the pass reaches past
        // provenance, because the label alone cannot tell a disc's profile 7 from a playable 8.1.
        var catalog = SeedCatalog();
        var relative = Path.Combine("library", "S", "s.mkv");
        var absolute = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await File.WriteAllBytesAsync(absolute, new byte[16]);
        var itemId = SeedItemWithSource(catalog, relative);
        var source = await _database.MediaSources.SingleAsync(s => s.MediaItemId == itemId);
        _database.MediaStreams.Add(new MediaStream
        {
            Id = Guid.NewGuid(), MediaSourceId = source.Id, StreamType = StreamType.Video, Index = 0,
            Codec = "hevc", HdrFormat = "Dolby Vision",
        });
        await _database.SaveChangesAsync();

        var probe = new FakeMediaProbe
        {
            OnProbe = _ => new ProbeResult("mkv", TimeSpan.FromMinutes(120).Ticks, 8_000_000, 1_000_000,
            [
                new ProbedStream(StreamType.Video, 0, "hevc", "Main 10", null, 3840, 2160, 23.976, 10, "Dolby Vision", null, null, null, true, false, null,
                    new DolbyVisionDetail(7, 6, 6, ElPresent: true)),
            ]),
        };

        var report = await Service(probe).BackfillHeaderProbedAsync(_catalogId, CancellationToken.None);

        Assert.Equal(1, report.ItemsRefreshed);
        await using var fresh = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        var video = await fresh.MediaStreams.SingleAsync(stream => stream.MediaSourceId == source.Id);
        Assert.Equal(7, video.DvProfile);
        Assert.Equal(6, video.DvLevel);
        Assert.Equal(6, video.DvBlSignalCompatibilityId);
        Assert.True(video.DvElPresent);
    }

    [Fact]
    public async Task Backfill_leaves_an_engine_row_alone_when_it_has_nothing_to_gain()
    {
        // HDR10 from the engine, or Dolby Vision with its record already stored: neither is re-probed, so the
        // pass stays bounded to what could not be known when the row was written.
        var catalog = SeedCatalog();
        var itemId = SeedItemWithSource(catalog, Path.Combine("library", "T", "t.mkv"));
        var source = await _database.MediaSources.SingleAsync(s => s.MediaItemId == itemId);
        _database.MediaStreams.Add(new MediaStream
        {
            Id = Guid.NewGuid(), MediaSourceId = source.Id, StreamType = StreamType.Video, Index = 0,
            Codec = "hevc", HdrFormat = "Dolby Vision", DvProfile = 8, DvLevel = 6, DvBlSignalCompatibilityId = 1, DvElPresent = false,
        });
        await _database.SaveChangesAsync();

        var report = await Service().BackfillHeaderProbedAsync(_catalogId, CancellationToken.None);

        Assert.Equal(0, report.ItemsRefreshed);
    }

    [Fact]
    public async Task RefreshMedia_skips_sources_missing_on_disk_but_still_succeeds()
    {
        var catalog = SeedCatalog();
        // No file written to disk, so the source can't be re-probed and its streams stay as seeded (none).
        var itemId = SeedItemWithSource(catalog, Path.Combine("library", "Gone", "gone.mkv"));

        var refreshed = await Service().RefreshMediaAsync(itemId, CancellationToken.None);

        Assert.True(refreshed);
        await using var fresh = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        var source = await fresh.MediaSources.Include(s => s.Streams).SingleAsync(s => s.MediaItemId == itemId);
        Assert.Empty(source.Streams);
    }

    [Fact]
    public async Task RefreshMedia_returns_false_for_unknown_item()
    {
        Assert.False(await Service().RefreshMediaAsync(Guid.NewGuid(), CancellationToken.None));
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class RecordingCore : IHostyCoreClient
    {
        public List<string?> Notifications { get; } = [];
        public bool IsEnabled => true;
        public Task<CoreBackupResult?> CreateBackupAsync(string? note, CancellationToken cancellationToken) => Task.FromResult<CoreBackupResult?>(null);
        public Task<bool> PublishNotificationAsync(CoreNotificationLevel level, string title, string? body, string? link, string? dedupeKey, string target = HostyCoreClient.BroadcastTarget, CancellationToken cancellationToken = default)
        {
            Notifications.Add(dedupeKey);
            return Task.FromResult(true);
        }
        public Task<IReadOnlyList<CoreDirectoryUser>?> ListDirectoryUsersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CoreDirectoryUser>?>([]);
        public int CountFor(string dedupeKey) => Notifications.Count(n => n == dedupeKey);
    }

    [Fact]
    public async Task RefreshMedia_spares_sidecar_streams()
    {
        // Probing the video says nothing about files sitting beside it. Sweeping external rows here would
        // delete entries whose files are still on disk, making the tracks vanish with no way to merge or
        // remove them — and the backfill runs on exactly the items most likely to have them.
        var catalog = SeedCatalog();
        var relative = Path.Combine("library", "A", "a.mkv");
        var absolute = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await File.WriteAllBytesAsync(absolute, new byte[16]);
        var itemId = SeedItemWithSource(catalog, relative);

        var sourceId = (await _database.MediaSources.SingleAsync(source => source.MediaItemId == itemId)).Id;
        _database.MediaStreams.Add(new MediaStream
        {
            Id = Guid.NewGuid(), MediaSourceId = sourceId, StreamType = StreamType.Audio,
            Index = 1000, Language = "rus", IsExternal = true, ExternalPath = "library/A/a.rus.mka",
        });
        await _database.SaveChangesAsync();

        Assert.True(await Service().RefreshMediaAsync(itemId, CancellationToken.None));

        await using var fresh = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        var streams = await fresh.MediaStreams.Where(stream => stream.MediaSourceId == sourceId).ToListAsync();
        var external = Assert.Single(streams.Where(stream => stream.IsExternal));
        Assert.Equal("rus", external.Language);
        // The embedded set was still replaced by the fresh probe.
        Assert.Equal(2, streams.Count(stream => !stream.IsExternal));
    }

    [Fact]
    public async Task Backfill_reaches_a_sidecar_that_has_its_codec_but_no_bitrate()
    {
        // Bitrate arrived after codec did, so a row placed in between carries a codec and would never be
        // revisited if a missing codec were the only marker. The item-level refresh deliberately never
        // touches external rows, which makes this the one path that can reach it.
        var sidecar = await SeedSidecarWithSpecsButNoBitrateAsync();
        var probe = ProbeAnsweringForSidecars(
            new ProbedStream(StreamType.Audio, 0, "ac3", null, "rus", null, null, null, null, null, 6, 48000, 640_000, true, false, null));

        var report = await Service(probe).BackfillHeaderProbedAsync(_catalogId, CancellationToken.None);

        Assert.Equal(1, report.SidecarsFilled);
        await using var fresh = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        Assert.Equal(640_000, (await fresh.MediaStreams.SingleAsync(stream => stream.Id == sidecar.Id)).Bitrate);
    }

    [Fact]
    public async Task Backfill_keeps_specs_the_probe_answering_now_cannot_better()
    {
        // Re-probing a row for its missing bitrate can land on the header reader, which answers less than
        // the engine that filled the row in. Writing its nulls over what is there would lose information
        // this run never had.
        var sidecar = await SeedSidecarWithSpecsButNoBitrateAsync();
        var probe = ProbeAnsweringForSidecars(
            new ProbedStream(StreamType.Audio, 0, "ac3", null, "rus", null, null, null, null, null, null, null, null, true, false, null));

        await Service(probe).BackfillHeaderProbedAsync(_catalogId, CancellationToken.None);

        await using var fresh = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        var kept = await fresh.MediaStreams.SingleAsync(stream => stream.Id == sidecar.Id);
        Assert.Equal(6, kept.Channels);
        Assert.Equal(48000, kept.SampleRate);
        Assert.Null(kept.Bitrate);
    }

    [Fact]
    public async Task Backfill_leaves_a_subtitle_sidecar_alone_once_it_has_a_codec()
    {
        // A subtitle track has no bitrate to find, so selecting it on a null one would re-probe it on every
        // run forever for an answer that never comes.
        var sidecar = await SeedSidecarWithSpecsButNoBitrateAsync();
        await _database.MediaStreams.Where(stream => stream.Id == sidecar.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(stream => stream.StreamType, StreamType.Subtitle)
                .SetProperty(stream => stream.Codec, "subrip")
                .SetProperty(stream => stream.Channels, (int?)null)
                .SetProperty(stream => stream.SampleRate, (int?)null));
        var probe = ProbeAnsweringForSidecars(
            new ProbedStream(StreamType.Subtitle, 0, "subrip", null, "rus", null, null, null, null, null, null, null, null, true, false, null));

        var report = await Service(probe).BackfillHeaderProbedAsync(_catalogId, CancellationToken.None);

        Assert.Equal(0, report.SidecarsFilled);
    }

    /// <summary>A sidecar row as the version before this one left it: codec and layout recorded, bitrate
    /// null because the column did not exist yet.</summary>
    private async Task<MediaStream> SeedSidecarWithSpecsButNoBitrateAsync()
    {
        var sidecar = await SeedSidecarWithoutSpecsAsync();
        await _database.MediaStreams.Where(stream => stream.Id == sidecar.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(stream => stream.Codec, "ac3")
                .SetProperty(stream => stream.Channels, 6)
                .SetProperty(stream => stream.SampleRate, 48000));
        return sidecar;
    }

    /// <summary>Seeds a movie with one sidecar beside it, both present on disk, and answers null for the
    /// sidecar's own specs — the state of every row placed before they were recorded.</summary>
    private async Task<MediaStream> SeedSidecarWithoutSpecsAsync()
    {
        var catalog = SeedCatalog();
        var relative = Path.Combine("library", "A", "a.mkv");
        var absolute = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await File.WriteAllBytesAsync(absolute, new byte[16]);
        await File.WriteAllBytesAsync(Path.Combine(_root, "library", "A", "a.rus.mka"), new byte[16]);
        var itemId = SeedItemWithSource(catalog, relative);

        var sourceId = (await _database.MediaSources.SingleAsync(source => source.MediaItemId == itemId)).Id;
        var sidecar = new MediaStream
        {
            Id = Guid.NewGuid(), MediaSourceId = sourceId, StreamType = StreamType.Audio,
            Index = 1000, Language = "rus", Title = "Гаврилов",
            IsExternal = true, ExternalPath = "library/A/a.rus.mka",
        };
        _database.MediaStreams.Add(sidecar);
        await _database.SaveChangesAsync();
        return sidecar;
    }

    private static FakeMediaProbe ProbeAnsweringForSidecars(ProbedStream? sidecarTrack) => new()
    {
        OnProbe = path => path.EndsWith(".mka", StringComparison.Ordinal)
            ? new ProbeResult("matroska", TimeSpan.FromMinutes(120).Ticks, 320_000, 50_000_000,
                sidecarTrack is null ? [] : [sidecarTrack])
            : new ProbeResult("matroska", TimeSpan.FromMinutes(120).Ticks, 8_000_000, 1_000_000,
                [new ProbedStream(StreamType.Video, 0, "h264", "High", null, 1920, 1080, 23.976, 8, null, null, null, null, true, false, null)]),
    };

    [Fact]
    public async Task Backfill_fills_in_a_sidecars_missing_specs()
    {
        // The rows placed before specs were recorded. RefreshMediaAsync cannot do this — it deliberately
        // never touches external rows — so the backfill probes the sidecar's own file.
        var sidecar = await SeedSidecarWithoutSpecsAsync();
        var probe = ProbeAnsweringForSidecars(
            new ProbedStream(StreamType.Audio, 0, "ac3", null, "rus", null, null, null, null, null, 6, 48000, 640_000, true, false, null));

        var report = await Service(probe).BackfillHeaderProbedAsync(_catalogId, CancellationToken.None);

        Assert.Equal(1, report.SidecarsFilled);
        await using var fresh = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        var filled = await fresh.MediaStreams.SingleAsync(stream => stream.Id == sidecar.Id);
        Assert.Equal("ac3", filled.Codec);
        Assert.Equal(6, filled.Channels);
        Assert.Equal(48000, filled.SampleRate);
        Assert.Equal(640_000, filled.Bitrate);
    }

    [Fact]
    public async Task Backfill_leaves_a_sidecars_label_alone()
    {
        // Language and title are a labelling decision the sidecar stage made across a whole cohort, weighing
        // tags against paths. Re-reading one file here would overwrite it with strictly less information —
        // and this probe answers a different language on purpose to prove it does not.
        var sidecar = await SeedSidecarWithoutSpecsAsync();
        var probe = ProbeAnsweringForSidecars(
            new ProbedStream(StreamType.Audio, 0, "ac3", null, "eng", null, null, null, null, null, 6, 48000, null, true, false, "Something else"));

        await Service(probe).BackfillHeaderProbedAsync(_catalogId, CancellationToken.None);

        await using var fresh = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        var filled = await fresh.MediaStreams.SingleAsync(stream => stream.Id == sidecar.Id);
        Assert.Equal("rus", filled.Language);
        Assert.Equal("Гаврилов", filled.Title);
    }

    [Fact]
    public async Task Backfill_leaves_a_sidecar_it_cannot_read_for_the_next_run()
    {
        // An elementary stream read without the engine answers nothing. Recording that as "no codec" would
        // mark the row done and never look at it again once the engine is attached.
        var sidecar = await SeedSidecarWithoutSpecsAsync();

        var report = await Service(ProbeAnsweringForSidecars(null)).BackfillHeaderProbedAsync(_catalogId, CancellationToken.None);

        Assert.Equal(0, report.SidecarsFilled);
        await using var fresh = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        Assert.Null((await fresh.MediaStreams.SingleAsync(stream => stream.Id == sidecar.Id)).Codec);
    }
}
