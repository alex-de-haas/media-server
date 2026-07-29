using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Sidecars;

/// <summary>
/// Deleting one sidecar — an external track sitting beside a library file. Presented like deleting an
/// unwanted version, but its own operation: a sidecar is a stream on a source, not a source, so there is no
/// version to drop. The explicit choice between dropping the entry and erasing the file is the point.
/// </summary>
public sealed class SidecarDeletionTests : IDisposable
{
    private readonly JellyfinDatabase _db = new();
    private readonly MediaServerDbContext _context;
    private readonly string _root;
    private readonly string _sidecarRelative = "The Rock (1996)/The Rock (1996).rus.mka";
    private readonly string _videoRelative = "The Rock (1996)/The Rock (1996).mkv";

    private Guid _streamId;
    private Guid _sourceId;
    private Guid _movieId;

    public SidecarDeletionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ms-sidecar-del-" + Guid.NewGuid().ToString("N"));
        CatalogPaths.For(_root).EnsureCreated();
        Seed();
        _context = _db.Create();
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

    private LibraryDeleteService Service() =>
        new(_context, new LibraryFileEraser(new CatalogPathSandbox(), NullLogger<LibraryFileEraser>.Instance));

    private string SidecarAbsolute => Path.Combine(_root, _sidecarRelative.Replace('/', Path.DirectorySeparatorChar));

    private string VideoAbsolute => Path.Combine(_root, _videoRelative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task Dropping_the_entry_leaves_the_file_on_disk()
    {
        var deleted = await Service().DeleteExternalStreamAsync(_streamId, deleteFile: false, CancellationToken.None);

        Assert.True(deleted);
        Assert.Empty(await _context.MediaStreams.Where(stream => stream.Id == _streamId).ToListAsync());
        Assert.True(File.Exists(SidecarAbsolute), "without the erase flag the file must survive");
    }

    [Fact]
    public async Task Erasing_takes_the_file_too()
    {
        var deleted = await Service().DeleteExternalStreamAsync(_streamId, deleteFile: true, CancellationToken.None);

        Assert.True(deleted);
        Assert.False(File.Exists(SidecarAbsolute));
    }

    [Fact]
    public async Task The_video_and_its_own_streams_are_untouched()
    {
        await Service().DeleteExternalStreamAsync(_streamId, deleteFile: true, CancellationToken.None);

        // Removing a sidecar must not disturb the version it hung off.
        Assert.NotNull(await _context.MediaSources.FirstOrDefaultAsync(source => source.Id == _sourceId));
        Assert.Single(await _context.MediaStreams.Where(stream => stream.MediaSourceId == _sourceId).ToListAsync());
    }

    [Fact]
    public async Task The_staged_row_goes_back_to_unassigned_rather_than_pointing_at_nothing()
    {
        await Service().DeleteExternalStreamAsync(_streamId, deleteFile: true, CancellationToken.None);

        var file = await _context.SourceFiles.SingleAsync(candidate => candidate.RelativePath == _sidecarRelative);
        Assert.Null(file.MediaItemId);
        Assert.Equal(SourceFileAssignmentStatus.Unassigned, file.AssignmentStatus);
    }

    [Fact]
    public async Task An_embedded_stream_is_not_deletable_this_way()
    {
        // Only sidecars are files of their own; an embedded track lives inside the video and has nothing to
        // remove independently.
        var embedded = await _context.MediaStreams.FirstAsync(stream => !stream.IsExternal);

        Assert.False(await Service().DeleteExternalStreamAsync(embedded.Id, deleteFile: true, CancellationToken.None));
    }

    [Fact]
    public async Task An_identically_placed_sidecar_of_another_item_is_left_alone()
    {
        // A relative path is only unique within its catalog, so matching on the path alone would detach a
        // sidecar of a different library whose file is still there. Another catalog means another ingest,
        // which is also what the (IngestItemId, RelativePath) uniqueness allows.
        var now = DateTimeOffset.UtcNow;
        var otherCatalog = new Catalog
        {
            Id = Guid.NewGuid(), Name = "Other movies", Type = CatalogType.Movie,
            Root = Path.Combine(Path.GetTempPath(), "ms-other-" + Guid.NewGuid().ToString("N")),
            CreatedAt = now, UpdatedAt = now,
        };
        var otherMovie = new MediaItem
        {
            Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString("N"), CatalogId = otherCatalog.Id,
            Kind = MediaKind.Movie, Title = "The Rock", Year = 1996, AddedAt = now, UpdatedAt = now,
        };
        var otherIngest = new IngestItem
        {
            Id = Guid.NewGuid(), CatalogId = otherCatalog.Id, Stage = IngestStage.Publish,
            Status = IngestStatus.Done, CreatedAt = now, UpdatedAt = now,
        };
        var other = otherMovie.Id;
        _context.Catalogs.Add(otherCatalog);
        _context.MediaItems.Add(otherMovie);
        _context.IngestItems.Add(otherIngest);
        await _context.SaveChangesAsync();

        _context.SourceFiles.Add(new SourceFile
        {
            Id = Guid.NewGuid(), IngestItemId = otherIngest.Id, MediaItemId = other,
            RelativePath = _sidecarRelative, SizeBytes = 512,
            AssignmentStatus = SourceFileAssignmentStatus.Sidecar, CreatedAt = now, UpdatedAt = now,
        });
        await _context.SaveChangesAsync();

        await Service().DeleteExternalStreamAsync(_streamId, deleteFile: false, CancellationToken.None);

        var untouched = await _context.SourceFiles.SingleAsync(file => file.MediaItemId == other);
        Assert.Equal(SourceFileAssignmentStatus.Sidecar, untouched.AssignmentStatus);
    }

    [Fact]
    public async Task An_unknown_stream_is_reported_rather_than_silently_ignored() =>
        Assert.False(await Service().DeleteExternalStreamAsync(Guid.NewGuid(), deleteFile: false, CancellationToken.None));

    [Fact]
    public async Task Deleting_the_movie_with_its_files_takes_the_sidecars_beside_it()
    {
        // A sidecar has no item of its own: it exists as a stream of a source, so once the movie is gone
        // nothing in the library refers to it. Leaving it behind stranded a dub next to a deleted film — in
        // a folder that could not be pruned either, since it was no longer empty.
        Assert.True(await Service().DeleteAsync(_movieId, deleteFiles: true, deleteUserData: false, CancellationToken.None));

        Assert.False(File.Exists(VideoAbsolute));
        Assert.False(File.Exists(SidecarAbsolute));
        Assert.False(Directory.Exists(Path.GetDirectoryName(VideoAbsolute)), "the emptied folder is pruned");
    }

    [Fact]
    public async Task Deleting_the_movie_without_its_files_leaves_the_sidecars_alone()
    {
        Assert.True(await Service().DeleteAsync(_movieId, deleteFiles: false, deleteUserData: false, CancellationToken.None));

        Assert.True(File.Exists(VideoAbsolute));
        Assert.True(File.Exists(SidecarAbsolute), "without the erase flag a rescan must still find everything");
    }

    [Fact]
    public async Task Deleting_the_version_with_its_file_takes_its_sidecars_too()
    {
        // Same reasoning one level down: the sidecars hang off this source, and its streams go with it.
        Assert.True(await Service().DeleteSourceAsync(_sourceId, deleteFile: true, CancellationToken.None));

        Assert.False(File.Exists(VideoAbsolute));
        Assert.False(File.Exists(SidecarAbsolute));
    }

    [Fact]
    public async Task Deleting_the_version_returns_its_sidecars_staged_rows_to_unassigned()
    {
        await Service().DeleteSourceAsync(_sourceId, deleteFile: true, CancellationToken.None);

        var file = await _context.SourceFiles.SingleAsync(candidate => candidate.RelativePath == _sidecarRelative);
        Assert.Null(file.MediaItemId);
        Assert.Equal(SourceFileAssignmentStatus.Unassigned, file.AssignmentStatus);
    }

    private void Seed()
    {
        var now = DateTimeOffset.UtcNow;
        using var context = _db.Create();

        var catalog = new Catalog
        {
            Id = Guid.NewGuid(), Name = "Movies", Type = CatalogType.Movie, Root = _root,
            CreatedAt = now, UpdatedAt = now,
        };
        context.Catalogs.Add(catalog);

        var movie = new MediaItem
        {
            Id = _movieId = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString("N"), CatalogId = catalog.Id,
            Kind = MediaKind.Movie, Title = "The Rock", Year = 1996, AddedAt = now, UpdatedAt = now,
        };
        context.MediaItems.Add(movie);

        var source = new MediaSource
        {
            Id = Guid.NewGuid(), MediaItemId = movie.Id, Container = "mkv",
            Path = _videoRelative, SizeBytes = 1024, CreatedAt = now,
        };
        _sourceId = source.Id;
        context.MediaSources.Add(source);

        context.MediaStreams.AddRange(
            new MediaStream
            {
                Id = Guid.NewGuid(), MediaSourceId = source.Id, StreamType = StreamType.Video,
                Index = 0, Codec = "h264",
            },
            new MediaStream
            {
                Id = _streamId = Guid.NewGuid(), MediaSourceId = source.Id, StreamType = StreamType.Audio,
                Index = 1000, Language = "rus", Title = "Дубляж",
                IsExternal = true, ExternalPath = _sidecarRelative,
            });

        var ingest = new IngestItem
        {
            Id = Guid.NewGuid(), CatalogId = catalog.Id, Stage = IngestStage.Publish,
            Status = IngestStatus.Done, CreatedAt = now, UpdatedAt = now,
        };
        context.IngestItems.Add(ingest);
        context.SaveChanges();

        context.SourceFiles.Add(new SourceFile
        {
            Id = Guid.NewGuid(), IngestItemId = ingest.Id, MediaItemId = movie.Id,
            RelativePath = _sidecarRelative, SizeBytes = 512,
            AssignmentStatus = SourceFileAssignmentStatus.Sidecar, CreatedAt = now, UpdatedAt = now,
        });
        context.SaveChanges();

        Directory.CreateDirectory(Path.GetDirectoryName(SidecarAbsolute)!);
        File.WriteAllBytes(SidecarAbsolute, new byte[512]);
        File.WriteAllBytes(VideoAbsolute, new byte[1024]);
    }
}
