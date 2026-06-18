using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Configuration;
using MediaServer.Api.Hosty;
using MediaServer.Api.Jellyfin;
using MediaServer.Api.Library;
using MediaServer.Api.Jellyfin.Streaming;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Api.Tests.Jellyfin;

public sealed class JellyfinStreamingTests : IDisposable
{
    private const int FileSize = 1000;

    private readonly JellyfinDatabase _db = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ms-jf-stream-" + Guid.NewGuid().ToString("N"));
    private readonly JellyfinStreamResolver _resolver;

    private string _moviePublicId = string.Empty;
    private string _secondSourceId = string.Empty;

    public JellyfinStreamingTests()
    {
        var settings = new MediaServerSettings { SupportedLanguages = ["en-US"] };
        var hosty = new HostyOptions { AppId = "com.haas.media-server", CoreOrigin = "http://localhost:3001", AppDataDir = _root };
        var server = new JellyfinServerContext(hosty, settings);
        var library = new JellyfinLibraryService(
            _db.Create(), new JellyfinItemMapper(server), new UserDataService(_db.Create(), TimeProvider.System), settings);
        _resolver = new JellyfinStreamResolver(library, new CatalogPathSandbox());
        Seed();
    }

    [Fact]
    public async Task Resolves_a_published_item_to_its_on_disk_file()
    {
        var resolved = await _resolver.ResolveAsync(_moviePublicId, null, CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.True(File.Exists(resolved!.AbsolutePath));
        Assert.Equal("video/x-matroska", resolved.ContentType);
        Assert.Equal(FileSize, resolved.Length);
    }

    [Fact]
    public async Task Honors_an_explicit_media_source_id()
    {
        var resolved = await _resolver.ResolveAsync(_moviePublicId, _secondSourceId, CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.EndsWith("alt.mkv", resolved!.AbsolutePath);
    }

    [Fact]
    public async Task Unknown_item_does_not_resolve()
    {
        Assert.Null(await _resolver.ResolveAsync("ffffffffffffffffffffffffffffffff", null, CancellationToken.None));
    }

    [Fact]
    public async Task Unsupported_container_is_not_streamable()
    {
        var (publicId, _) = SeedExtra("clip.exe");

        Assert.Null(await _resolver.ResolveAsync(publicId, null, CancellationToken.None));
    }

    [Fact]
    public async Task A_full_request_returns_the_whole_file_with_accept_ranges()
    {
        var response = await ExecuteAsync("GET", range: null);

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Equal("bytes", response.Headers.AcceptRanges);
        Assert.Equal(FileSize, response.BodyLength);
    }

    [Fact]
    public async Task A_range_request_returns_206_partial_content()
    {
        var response = await ExecuteAsync("GET", range: "bytes=0-9");

        Assert.Equal(StatusCodes.Status206PartialContent, response.Status);
        Assert.Equal($"bytes 0-9/{FileSize}", response.Headers.ContentRange);
        Assert.Equal(10, response.BodyLength);
    }

    [Fact]
    public async Task A_head_request_sends_headers_without_a_body()
    {
        var response = await ExecuteAsync("HEAD", range: null);

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Equal(FileSize, response.Headers.ContentLength);
        Assert.Equal(0, response.BodyLength);
    }

    [Fact]
    public async Task An_unsatisfiable_range_returns_416()
    {
        var response = await ExecuteAsync("GET", range: "bytes=100000-200000");

        Assert.Equal(StatusCodes.Status416RangeNotSatisfiable, response.Status);
    }

    private async Task<(int Status, IHeaderDictionary Headers, long BodyLength)> ExecuteAsync(string method, string? range)
    {
        var resolved = await _resolver.ResolveAsync(_moviePublicId, null, CancellationToken.None);
        var result = JellyfinStreamResults.File(resolved!);

        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        context.Request.Method = method;
        if (range is not null)
        {
            context.Request.Headers.Range = range;
        }

        var body = new MemoryStream();
        context.Response.Body = body;
        await result.ExecuteAsync(context);
        return (context.Response.StatusCode, context.Response.Headers, body.Length);
    }

    private void Seed()
    {
        var paths = CatalogPaths.For(Path.Combine(_root, "catalog"));
        paths.EnsureCreated();
        File.WriteAllBytes(Path.Combine(paths.LibraryDir, "movie.mkv"), new byte[FileSize]);
        File.WriteAllBytes(Path.Combine(paths.LibraryDir, "alt.mkv"), new byte[FileSize]);

        var now = DateTimeOffset.UtcNow;
        using var context = _db.Create();
        var catalog = new Catalog { Id = Guid.NewGuid(), Name = "Movies", Type = CatalogType.Movie, Root = paths.Root, CreatedAt = now, UpdatedAt = now };
        var movie = new MediaItem { Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString("N"), CatalogId = catalog.Id, Kind = MediaKind.Movie, Title = "Movie", Year = 2020, AddedAt = now, UpdatedAt = now };
        _moviePublicId = movie.PublicId!;

        var primary = new MediaSource { Id = Guid.NewGuid(), MediaItemId = movie.Id, Container = "matroska", Path = "library/movie.mkv", SizeBytes = FileSize, DurationTicks = TimeSpan.FromMinutes(90).Ticks, CreatedAt = now };
        var alternate = new MediaSource { Id = Guid.NewGuid(), MediaItemId = movie.Id, Container = "matroska", Path = "library/alt.mkv", SizeBytes = FileSize, DurationTicks = TimeSpan.FromMinutes(90).Ticks, CreatedAt = now };
        _secondSourceId = JellyfinIds.MediaSource(alternate.Id);

        context.Catalogs.Add(catalog);
        context.MediaItems.Add(movie);
        context.MediaSources.AddRange(primary, alternate);
        context.SaveChanges();
    }

    private (string PublicId, Guid SourceId) SeedExtra(string fileName)
    {
        var paths = CatalogPaths.For(Path.Combine(_root, "catalog"));
        File.WriteAllBytes(Path.Combine(paths.LibraryDir, fileName), new byte[FileSize]);

        var now = DateTimeOffset.UtcNow;
        using var context = _db.Create();
        var catalog = context.Catalogs.First();
        var movie = new MediaItem { Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString("N"), CatalogId = catalog.Id, Kind = MediaKind.Movie, Title = "Extra", AddedAt = now, UpdatedAt = now };
        var source = new MediaSource { Id = Guid.NewGuid(), MediaItemId = movie.Id, Container = "x", Path = $"library/{fileName}", SizeBytes = FileSize, DurationTicks = 0, CreatedAt = now };
        context.MediaItems.Add(movie);
        context.MediaSources.Add(source);
        context.SaveChanges();
        return (movie.PublicId!, source.Id);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
