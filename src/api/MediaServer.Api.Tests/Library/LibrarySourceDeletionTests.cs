using System.Text.Json;
using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Library;

public sealed class LibrarySourceDeletionTests : IDisposable
{
    private readonly JellyfinDatabase _db = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ms-source-delete-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _itemId = Guid.NewGuid();
    private readonly Guid _sourceId = Guid.NewGuid();

    public LibrarySourceDeletionTests()
    {
        CatalogPaths.For(_root).EnsureCreated();
    }

    [Theory]
    [InlineData(MediaKind.Movie, false)]
    [InlineData(MediaKind.Movie, true)]
    [InlineData(MediaKind.Episode, false)]
    [InlineData(MediaKind.Episode, true)]
    public async Task Last_version_refusal_preserves_files_streams_history_and_ownership(MediaKind kind, bool deleteFile)
    {
        await SeedAsync(kind);
        await using var context = _db.Create();

        await Assert.ThrowsAsync<LastMediaSourceException>(() => Service(context).DeleteSourceAsync(_sourceId, deleteFile, default));

        await using var verify = _db.Create();
        var item = await verify.MediaItems.SingleAsync();
        Assert.NotNull(item.PublicId);
        Assert.Null(item.RemovedAt);
        Assert.Equal(_sourceId, item.DefaultSourceId);
        Assert.Equal(_sourceId, (await verify.MediaSources.SingleAsync()).Id);
        Assert.Equal(2, await verify.MediaStreams.CountAsync());
        Assert.Equal(_itemId, (await verify.SourceFiles.SingleAsync()).MediaItemId);
        Assert.True((await verify.UserItemData.SingleAsync()).IsFavorite);
        Assert.Single(await verify.PlaybackHistoryEntries.ToListAsync());
        Assert.Equal("video", await File.ReadAllTextAsync(Path.Combine(_root, "Title/movie.mkv")));
        Assert.Equal("subtitle", await File.ReadAllTextAsync(Path.Combine(_root, "Title/movie.srt")));
    }

    [Theory]
    [InlineData(MediaKind.Movie, false)]
    [InlineData(MediaKind.Movie, true)]
    [InlineData(MediaKind.Episode, false)]
    [InlineData(MediaKind.Episode, true)]
    public async Task Removing_one_of_two_versions_preserves_the_item_and_other_version(MediaKind kind, bool deleteFile)
    {
        await SeedAsync(kind);
        var other = await AddSourceAsync();
        await using var context = _db.Create();

        Assert.True(await Service(context).DeleteSourceAsync(_sourceId, deleteFile, default));

        await using var verify = _db.Create();
        Assert.Equal(other, (await verify.MediaSources.SingleAsync()).Id);
        var item = await verify.MediaItems.SingleAsync();
        Assert.Null(item.DefaultSourceId);
        Assert.NotNull(item.PublicId);
        Assert.Single(await verify.UserItemData.ToListAsync());
        Assert.Single(await verify.PlaybackHistoryEntries.ToListAsync());
        Assert.Equal(!deleteFile, File.Exists(Path.Combine(_root, "Title/movie.mkv")));
        Assert.Equal(!deleteFile, File.Exists(Path.Combine(_root, "Title/movie.srt")));
        Assert.True(File.Exists(Path.Combine(_root, "Title/other.mkv")));
    }

    [Fact]
    public async Task A_disc_counts_as_another_source_but_cannot_itself_be_removed_last()
    {
        await SeedAsync(MediaKind.Movie);
        var disc = await AddSourceAsync(MediaSourceKind.Bluray);
        await using var context = _db.Create();

        Assert.True(await Service(context).DeleteSourceAsync(_sourceId, true, default));
        await Assert.ThrowsAsync<LastMediaSourceException>(() => Service(context).DeleteSourceAsync(disc, true, default));

        await using var verify = _db.Create();
        Assert.Equal(disc, (await verify.MediaSources.SingleAsync()).Id);
        Assert.True(Directory.Exists(Path.Combine(_root, "Title/disc/BDMV")));
        Assert.NotNull((await verify.MediaItems.SingleAsync()).PublicId);
    }

    [Fact]
    public async Task Concurrent_version_removals_leave_one_source_and_its_file()
    {
        await SeedAsync(MediaKind.Movie);
        var other = await AddSourceAsync();
        // Hold the same gate so both independent calls are waiting before either starts deleting.
        var gate = await LibraryFileMutation.EnterAsync(default);
        var first = RemoveAsync(_sourceId);
        var second = RemoveAsync(other);
        gate.Dispose();
        var outcomes = await Task.WhenAll(first, second);

        Assert.Single(outcomes, succeeded => succeeded);
        await using var verify = _db.Create();
        var survivor = await verify.MediaSources.SingleAsync();
        Assert.True(File.Exists(Path.Combine(_root, survivor.Path)));

        async Task<bool> RemoveAsync(Guid id)
        {
            await using var context = _db.Create();
            try { return await Service(context).DeleteSourceAsync(id, true, default); }
            catch (LastMediaSourceException) { return false; }
        }
    }

    [Theory]
    [InlineData(0, StatusCodes.Status404NotFound)]
    [InlineData(1, StatusCodes.Status409Conflict)]
    [InlineData(2, StatusCodes.Status204NoContent)]
    public async Task Endpoint_distinguishes_missing_last_and_removable_sources(int count, int expectedStatus)
    {
        if (count > 0) await SeedAsync(MediaKind.Movie);
        if (count > 1) await AddSourceAsync();
        await using var context = _db.Create();
        var guard = new LibraryMoveGuard(context, new LibraryMoveQueue());

        var result = await LibraryEndpoints.DeleteSourceAsync(_sourceId, false, Service(context), guard, default);

        Assert.Equal(expectedStatus, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        if (count == 1)
        {
            var body = JsonSerializer.SerializeToElement(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
            Assert.Equal("last_media_source", body.GetProperty("error").GetString());
            Assert.Contains("movie or episode", body.GetProperty("detail").GetString());
        }
    }

    private async Task SeedAsync(MediaKind kind)
    {
        await using var context = _db.Create();
        var now = DateTimeOffset.UtcNow;
        var catalog = new Catalog { Id = Guid.NewGuid(), Name = "Library", Root = _root, Type = kind == MediaKind.Movie ? CatalogType.Movie : CatalogType.Series, CreatedAt = now, UpdatedAt = now };
        var user = new AppUser { HostUserId = "source-delete-user", Email = "viewer@example.com", Role = AppUserRole.User, CreatedAt = now, LastSeenAt = now };
        var ingest = new IngestItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Stage = IngestStage.Publish, Status = IngestStatus.Done, CreatedAt = now, UpdatedAt = now };
        context.Catalogs.Add(catalog);
        context.AppUsers.Add(user);
        context.IngestItems.Add(ingest);
        context.MediaItems.Add(new MediaItem { Id = _itemId, PublicId = "title", Title = "Title", Kind = kind, CatalogId = catalog.Id, AddedAt = now, UpdatedAt = now });
        await context.SaveChangesAsync();
        var file = new SourceFile { Id = Guid.NewGuid(), IngestItemId = ingest.Id, MediaItemId = _itemId, RelativePath = "Title/movie.mkv", SizeBytes = 5, CreatedAt = now, UpdatedAt = now };
        context.SourceFiles.Add(file);
        context.MediaSources.Add(new MediaSource { Id = _sourceId, MediaItemId = _itemId, SourceFileId = file.Id, Container = "mkv", Path = file.RelativePath, CreatedAt = now });
        context.MediaStreams.AddRange(
            new MediaStream { Id = Guid.NewGuid(), MediaSourceId = _sourceId, StreamType = StreamType.Video, Index = 0 },
            new MediaStream { Id = Guid.NewGuid(), MediaSourceId = _sourceId, StreamType = StreamType.Subtitle, Index = 1, IsExternal = true, ExternalPath = "Title/movie.srt" });
        context.UserItemData.Add(new UserItemData { Id = Guid.NewGuid(), AppUserId = user.Id, MediaItemId = _itemId, IsFavorite = true });
        context.PlaybackHistoryEntries.Add(new PlaybackHistoryEntry { Id = Guid.NewGuid(), AppUserId = user.Id, MediaItemId = _itemId, CreatedAt = now, WatchedAt = now, Origin = PlaybackHistoryOrigin.LocalPlayback });
        await context.SaveChangesAsync();
        await context.MediaItems.Where(item => item.Id == _itemId).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.DefaultSourceId, _sourceId));
        Directory.CreateDirectory(Path.Combine(_root, "Title"));
        await File.WriteAllTextAsync(Path.Combine(_root, "Title/movie.mkv"), "video");
        await File.WriteAllTextAsync(Path.Combine(_root, "Title/movie.srt"), "subtitle");
    }

    private async Task<Guid> AddSourceAsync(MediaSourceKind kind = MediaSourceKind.File)
    {
        var id = Guid.NewGuid();
        await using var context = _db.Create();
        context.MediaSources.Add(new MediaSource { Id = id, MediaItemId = _itemId, Kind = kind, Container = kind == MediaSourceKind.Bluray ? "bluray" : "mkv", Path = kind == MediaSourceKind.Bluray ? "Title/disc" : "Title/other.mkv", CreatedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();
        if (kind == MediaSourceKind.Bluray) Directory.CreateDirectory(Path.Combine(_root, "Title/disc/BDMV"));
        else await File.WriteAllTextAsync(Path.Combine(_root, "Title/other.mkv"), "other");
        return id;
    }

    private static LibraryDeleteService Service(MediaServerDbContext context) =>
        new(context, new LibraryFileEraser(new CatalogPathSandbox(), NullLogger<LibraryFileEraser>.Instance));

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
