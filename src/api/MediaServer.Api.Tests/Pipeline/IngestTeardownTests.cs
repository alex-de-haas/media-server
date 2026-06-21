using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Api.Tests.Pipeline;

/// <summary>
/// Covers the download→identify hand-off: a completed, non-seeding download is dropped before identify, its
/// file kept in <c>.incoming/</c> (owned by the ingest) and then moved into the canonical layout on
/// publish. A torrent kept seeding parks the ingest at the download stage until seeding stops.
/// </summary>
public sealed class IngestTeardownTests
{
    private static void StrongMatch(PipelineTestHarness harness) =>
        harness.MetadataProvider.OnSearch = query => [new MetadataCandidate(new ProviderRef("tmdb", "27205"), query.Title, query.Year, 1.0)];

    private static string StagingPath(Catalog catalog, Guid downloadId, string sourceRelativePath) =>
        Path.Combine(catalog.Root, CatalogPaths.IncomingRelative(downloadId).Replace('/', Path.DirectorySeparatorChar),
            sourceRelativePath.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task Publish_drops_the_download_at_handoff_and_moves_the_file()
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
        Assert.Null(ingest.DownloadId); // dropped at the hand-off
        Assert.NotNull(ingest.MediaItemId);

        // Download row gone; the source file survives, owned by the ingest, at its canonical path.
        Assert.False(await database.Downloads.AnyAsync(download => download.Id == downloadId));
        var sourceFile = await database.SourceFiles.SingleAsync(file => file.IngestItemId == ingestId);
        Assert.Null(sourceFile.DownloadId);

        var catalog = await database.Catalogs.SingleAsync(item => item.Id == catalogId);
        Assert.False(File.Exists(StagingPath(catalog, downloadId, "Inception.2010.1080p/movie.mkv"))); // staging cleared

        var movie = await database.MediaItems.SingleAsync(item => item.Kind == MediaKind.Movie);
        var canonical = Path.Combine(catalog.Root, movie.LibraryPath!.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(canonical)); // moved into the canonical layout
        Assert.Equal(movie.LibraryPath, sourceFile.RelativePath);
    }

    [Fact]
    public async Task Keep_seeding_parks_the_ingest_at_the_download_stage()
    {
        using var harness = new PipelineTestHarness();
        StrongMatch(harness);
        var (ingestId, catalogId, downloadId) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Inception.2010.1080p", "Inception.2010.1080p/movie.mkv", keepSeeding: true);

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var scope = harness.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();

        var ingest = await database.IngestItems.SingleAsync(item => item.Id == ingestId);
        Assert.NotEqual(IngestStatus.Done, ingest.Status); // parked, not published
        Assert.Equal(IngestStage.Download, ingest.Stage);
        Assert.Equal(downloadId, ingest.DownloadId); // download retained while seeding

        var download = await database.Downloads.SingleAsync(item => item.Id == downloadId);
        Assert.Equal(DownloadState.Seeding, download.State);

        var catalog = await database.Catalogs.SingleAsync(item => item.Id == catalogId);
        Assert.True(File.Exists(StagingPath(catalog, downloadId, "Inception.2010.1080p/movie.mkv"))); // still seeding from .incoming
        Assert.False(await database.MediaItems.AnyAsync(item => item.Kind == MediaKind.Movie)); // not in the library yet
    }

    [Fact]
    public async Task Stopping_seeding_advances_a_parked_ingest_to_publish()
    {
        using var harness = new PipelineTestHarness();
        StrongMatch(harness);
        var (ingestId, _, downloadId) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Inception.2010.1080p", "Inception.2010.1080p/movie.mkv", keepSeeding: true);

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None); // parks at download (seeding)

        // Operator stops seeding: flip the state and re-drive.
        using (var scope = harness.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var download = await database.Downloads.SingleAsync(item => item.Id == downloadId);
            download.State = DownloadState.StoppedSeeding;
            await database.SaveChangesAsync();
        }

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var verify = harness.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var ingest = await verifyDb.IngestItems.SingleAsync(item => item.Id == ingestId);
        Assert.Equal(IngestStatus.Done, ingest.Status);
        Assert.Null(ingest.DownloadId);
        Assert.False(await verifyDb.Downloads.AnyAsync(download => download.Id == downloadId));
        Assert.True(await verifyDb.MediaItems.AnyAsync(item => item.Kind == MediaKind.Movie));
    }

    [Fact]
    public async Task Low_confidence_routes_to_review_after_the_handoff()
    {
        using var harness = new PipelineTestHarness();
        harness.MetadataProvider.OnSearch = _ => [new MetadataCandidate(new ProviderRef("tmdb", "1"), "Unrelated", 1999, 0.2)];
        var (ingestId, catalogId, downloadId) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Obscure.2021", "Obscure.2021/movie.mkv");

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var scope = harness.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();

        var ingest = await database.IngestItems.SingleAsync(item => item.Id == ingestId);
        Assert.Equal(IngestStatus.NeedsReview, ingest.Status); // parked at identify, after the hand-off
        Assert.Null(ingest.DownloadId);
        Assert.False(await database.Downloads.AnyAsync(download => download.Id == downloadId));

        // The file is retained in .incoming/, owned by the ingest, awaiting the operator's match.
        var catalog = await database.Catalogs.SingleAsync(item => item.Id == catalogId);
        Assert.True(File.Exists(StagingPath(catalog, downloadId, "Obscure.2021/movie.mkv")));
        Assert.True(await database.SourceFiles.AnyAsync(file => file.IngestItemId == ingestId));
    }

    [Fact]
    public async Task Phantom_completion_with_missing_files_fails_without_dropping_the_download()
    {
        using var harness = new PipelineTestHarness();
        StrongMatch(harness);
        var (ingestId, catalogId, downloadId) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Inception.2010.1080p", "Inception.2010.1080p/movie.mkv");

        // Simulate stale resume data: the download reports complete but nothing is on the save path.
        using (var scope = harness.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var catalog = await database.Catalogs.SingleAsync(item => item.Id == catalogId);
            Directory.Delete(Path.Combine(catalog.Root, CatalogPaths.IncomingDirName), recursive: true);
        }

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var verify = harness.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var ingest = await verifyDb.IngestItems.SingleAsync(item => item.Id == ingestId);
        Assert.Equal(IngestStatus.Failed, ingest.Status);            // not handed off
        Assert.Equal(downloadId, ingest.DownloadId);                 // download kept for clean removal
        Assert.True(await verifyDb.Downloads.AnyAsync(download => download.Id == downloadId));
    }
}
