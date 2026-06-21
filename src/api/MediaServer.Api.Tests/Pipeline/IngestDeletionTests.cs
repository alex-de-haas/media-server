using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Api.Tests.Pipeline;

/// <summary>Covers download-less (scan-style) ingest processing and the manual ingest-delete valve.</summary>
public sealed class IngestDeletionTests
{
    [Fact]
    public async Task Download_less_item_is_processed_via_identify_not_failed()
    {
        using var harness = new PipelineTestHarness();
        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Movie.mkv", "Movie.mkv");

        // A download-less ingest is valid now (the scan entry point, or a download already handed off):
        // drop the download FK and the item must still flow through the pipeline rather than fail.
        using (var scope = harness.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var item = await db.IngestItems.FirstAsync(i => i.Id == ingestId);
            item.DownloadId = null;
            await db.SaveChangesAsync();
        }

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var verify = harness.CreateScope();
        var driven = await verify.ServiceProvider.GetRequiredService<MediaServerDbContext>().IngestItems.AsNoTracking().FirstAsync(i => i.Id == ingestId);
        // The fake provider returns no confident match, so identify parks it for review — not Failed.
        Assert.Equal(IngestStatus.NeedsReview, driven.Status);
    }

    [Fact]
    public async Task DeleteAsync_of_an_in_flight_item_also_removes_its_download_and_staging()
    {
        using var harness = new PipelineTestHarness();
        var (ingestId, catalogId, downloadId) = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Movie.mkv", "Movie.mkv");

        using (var scope = harness.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IngestService>();
            Assert.True(await service.DeleteAsync(ingestId, CancellationToken.None));
        }

        using var verify = harness.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        // An in-flight ingest delegates to download removal: the ingest, its source files, the download, and
        // the .incoming/ staging are all gone (so a re-add re-downloads cleanly).
        Assert.False(await db.IngestItems.AnyAsync(i => i.Id == ingestId));
        Assert.False(await db.Downloads.AnyAsync(d => d.Id == downloadId));
        Assert.False(await db.SourceFiles.AnyAsync(f => f.IngestItemId == ingestId));
        var catalog = await db.Catalogs.SingleAsync(c => c.Id == catalogId);
        Assert.False(Directory.Exists(Path.Combine(catalog.Root, CatalogPaths.IncomingDirName, downloadId.ToString("N"))));
    }

    [Fact]
    public async Task DeleteAsync_of_a_download_less_item_erases_its_incoming_staging()
    {
        using var harness = new PipelineTestHarness();
        var (ingestId, catalogId, downloadId) = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Movie.mkv", "Movie.mkv");

        // Simulate a post-hand-off item: drop the download FK (the file stays in .incoming/, owned by the ingest).
        using (var scope = harness.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var item = await db.IngestItems.FirstAsync(i => i.Id == ingestId);
            item.DownloadId = null;
            await db.SaveChangesAsync();
        }

        var staging = Path.Combine(
            (await StagingCatalogRootAsync(harness, catalogId)),
            CatalogPaths.IncomingRelative(downloadId).Replace('/', Path.DirectorySeparatorChar), "Movie.mkv");
        Assert.True(File.Exists(staging)); // seeded on disk

        using (var scope = harness.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IngestService>();
            Assert.True(await service.DeleteAsync(ingestId, CancellationToken.None));
        }

        using var verify = harness.CreateScope();
        var db2 = verify.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        Assert.False(await db2.IngestItems.AnyAsync(i => i.Id == ingestId));
        Assert.False(File.Exists(staging)); // .incoming staging erased
    }

    private static async Task<string> StagingCatalogRootAsync(PipelineTestHarness harness, Guid catalogId)
    {
        using var scope = harness.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        return (await db.Catalogs.SingleAsync(c => c.Id == catalogId)).Root;
    }

    [Fact]
    public async Task DeleteAsync_returns_false_for_unknown_item()
    {
        using var harness = new PipelineTestHarness();
        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IngestService>();
        Assert.False(await service.DeleteAsync(Guid.NewGuid(), CancellationToken.None));
    }
}
