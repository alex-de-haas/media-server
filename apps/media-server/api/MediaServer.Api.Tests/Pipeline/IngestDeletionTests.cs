using MediaServer.Api.Data;
using MediaServer.Api.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Api.Tests.Pipeline;

/// <summary>Covers the orphan guard (download removed mid-pipeline) and the manual ingest-delete valve.</summary>
public sealed class IngestDeletionTests
{
    [Fact]
    public async Task Orphaned_item_with_no_download_is_failed_not_looped()
    {
        using var harness = new PipelineTestHarness();
        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Movie.mkv", "Movie.mkv");

        // Simulate the download having been removed: its FK is set null on the ingest item.
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
        Assert.Equal(IngestStatus.Failed, driven.Status);
        Assert.Contains("removed", driven.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteAsync_removes_only_the_ingest_row()
    {
        using var harness = new PipelineTestHarness();
        var (ingestId, _, downloadId) = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Movie.mkv", "Movie.mkv");

        using (var scope = harness.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IngestService>();
            Assert.True(await service.DeleteAsync(ingestId, CancellationToken.None));
        }

        using var verify = harness.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        Assert.False(await db.IngestItems.AnyAsync(i => i.Id == ingestId));
        // The download and its source files are left intact — manual ingest-delete is row-scoped.
        Assert.True(await db.Downloads.AnyAsync(d => d.Id == downloadId));
        Assert.True(await db.SourceFiles.AnyAsync(f => f.DownloadId == downloadId));
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
