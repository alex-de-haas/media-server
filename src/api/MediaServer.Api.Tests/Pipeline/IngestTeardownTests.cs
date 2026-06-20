using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using MediaServer.Api.Torrents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Api.Tests.Pipeline;

/// <summary>
/// Covers the "download is ephemeral" teardown: once an item publishes, its download backing (files/ seed
/// copy + Download/SourceFile rows) is reclaimed unless the torrent is still seeding. Nothing is torn down
/// before Done, and the published library content always survives.
/// </summary>
public sealed class IngestTeardownTests
{
    private static void StrongMatch(PipelineTestHarness harness) =>
        harness.MetadataProvider.OnSearch = query => [new MetadataCandidate(new ProviderRef("tmdb", "27205"), query.Title, query.Year, 1.0)];

    private static string SeedCopyPath(Catalog catalog, string sourceRelativePath) =>
        Path.Combine(CatalogPaths.For(catalog.Root).FilesDir, sourceRelativePath.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task Publish_without_seeding_tears_down_the_download_backing()
    {
        using var harness = new PipelineTestHarness();
        StrongMatch(harness);
        var (ingestId, catalogId, downloadId) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Inception.2010.1080p", "Inception.2010.1080p/movie.mkv");

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var scope = harness.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();

        var ingest = await database.IngestItems.SingleAsync(item => item.Id == ingestId);
        Assert.Equal(IngestStatus.Done, ingest.Status);
        Assert.Null(ingest.DownloadId); // detached from the now-removed download
        Assert.NotNull(ingest.MediaItemId);

        Assert.False(await database.Downloads.AnyAsync(download => download.Id == downloadId));
        Assert.False(await database.SourceFiles.AnyAsync(file => file.DownloadId == downloadId));

        var catalog = await database.Catalogs.SingleAsync(item => item.Id == catalogId);
        Assert.False(File.Exists(SeedCopyPath(catalog, "Inception.2010.1080p/movie.mkv"))); // files/ reclaimed

        var movie = await database.MediaItems.SingleAsync(item => item.Kind == MediaKind.Movie);
        var libraryAbsolute = Path.Combine(catalog.Root, movie.LibraryPath!.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(libraryAbsolute)); // library/ hardlink preserved
    }

    [Fact]
    public async Task Publish_with_seeding_keeps_the_download_backing()
    {
        using var harness = new PipelineTestHarness();
        StrongMatch(harness);
        var (ingestId, catalogId, downloadId) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Inception.2010.1080p", "Inception.2010.1080p/movie.mkv", keepSeeding: true);

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var scope = harness.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();

        var ingest = await database.IngestItems.SingleAsync(item => item.Id == ingestId);
        Assert.Equal(IngestStatus.Done, ingest.Status);
        Assert.Equal(downloadId, ingest.DownloadId); // still attached

        var download = await database.Downloads.SingleAsync(item => item.Id == downloadId);
        Assert.Equal(DownloadState.Seeding, download.State);
        Assert.True(await database.SourceFiles.AnyAsync(file => file.DownloadId == downloadId));

        var catalog = await database.Catalogs.SingleAsync(item => item.Id == catalogId);
        Assert.True(File.Exists(SeedCopyPath(catalog, "Inception.2010.1080p/movie.mkv"))); // files/ retained for seeding
    }

    [Fact]
    public async Task Review_item_retains_its_download_backing()
    {
        using var harness = new PipelineTestHarness();
        harness.MetadataProvider.OnSearch = _ => [new MetadataCandidate(new ProviderRef("tmdb", "1"), "Unrelated", 1999, 0.2)];
        var (ingestId, catalogId, downloadId) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Obscure.2021", "Obscure.2021/movie.mkv");

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var scope = harness.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();

        var ingest = await database.IngestItems.SingleAsync(item => item.Id == ingestId);
        Assert.Equal(IngestStatus.NeedsReview, ingest.Status); // parked before Organize
        Assert.Equal(downloadId, ingest.DownloadId);
        Assert.True(await database.Downloads.AnyAsync(download => download.Id == downloadId));
        Assert.True(await database.SourceFiles.AnyAsync(file => file.DownloadId == downloadId));

        var catalog = await database.Catalogs.SingleAsync(item => item.Id == catalogId);
        Assert.True(File.Exists(SeedCopyPath(catalog, "Obscure.2021/movie.mkv"))); // seed copy kept for organize
    }

    [Fact]
    public async Task Teardown_keeps_the_library_and_is_idempotent()
    {
        using var harness = new PipelineTestHarness();
        StrongMatch(harness);
        var (ingestId, _, downloadId) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Inception.2010", "Inception.2010/movie.mkv");
        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var scope = harness.CreateScope();
        var cleanup = scope.ServiceProvider.GetRequiredService<DownloadCleanupService>();
        // The publish already tore the backing down; a second call must be a safe no-op.
        await cleanup.TeardownAsync(downloadId, CancellationToken.None);

        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        Assert.True(await database.MediaItems.AnyAsync(item => item.Kind == MediaKind.Movie)); // library intact
        Assert.False(await database.Downloads.AnyAsync(download => download.Id == downloadId));
    }
}
