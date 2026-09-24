using MediaServer.Api.Data;
using MediaServer.Api.Pipeline;
using MediaServer.Api.Torrents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Api.Tests.Pipeline;

public sealed class TorrentConflictTests
{
    [Theory]
    [InlineData("pause", "cannot be paused")]
    [InlineData("resume", "cannot be resumed")]
    [InlineData("stop", "not complete")]
    [InlineData("policy", "being released")]
    [InlineData("torrent-delete", "Placement has started")]
    [InlineData("ingest-delete", "Placement has started")]
    public async Task Lifecycle_refusals_return_conflict_with_actionable_detail(string action, string detail)
    {
        using var harness = new PipelineTestHarness();
        var (ingestId, _, downloadId) = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "A", "A.mkv");
        using var scope = harness.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var download = await db.Downloads.SingleAsync();
        download.StopRequested = action is "pause" or "resume" or "policy";
        download.PlacementStarted = action.Contains("delete");
        download.CompletedAt = null;
        download.State = DownloadState.Downloading;
        await db.SaveChangesAsync();
        var torrents = scope.ServiceProvider.GetRequiredService<TorrentService>();
        var filter = new TorrentConflictFilter();
        var result = await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(new DefaultHttpContext()), async _ =>
        {
            return action switch
            {
                "pause" => await torrents.PauseAsync(downloadId, default),
                "resume" => await torrents.ResumeAsync(downloadId, default),
                "stop" => await torrents.StopSeedingAsync(downloadId, default),
                "policy" => await torrents.SetSeedingPolicyAsync(downloadId, true, default),
                "torrent-delete" => await scope.ServiceProvider.GetRequiredService<DownloadDeletionService>().DeleteAsync(downloadId, true, default),
                _ => await scope.ServiceProvider.GetRequiredService<IngestService>().DeleteAsync(ingestId, default),
            };
        });
        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal(409, problem.StatusCode);
        Assert.Contains(detail, problem.ProblemDetails.Detail);
    }
}
