using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using MediaServer.Api.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Api.Tests.Pipeline;

/// <summary>
/// Matching a pack's files to their movies <b>before the download finishes</b>. A torrent's file list is
/// known as soon as its metadata arrives, so the operator can confirm each video's identity while the
/// transfer is still running; identify then has nothing left to guess and the pack never stops at review.
/// </summary>
public sealed class IngestPreDownloadMatchTests
{
    private static MatchRequest Grouped(params MatchGroupRequest[] groups) =>
        new(MediaKind.Movie, "", "", "", null, [], [.. groups]);

    /// <summary>
    /// A movie pack whose torrent is still transferring: the source files (and therefore their names) are
    /// already known, the download is not done. Seeded via the completed-download helper and then walked
    /// back to <see cref="DownloadState.Downloading"/>, which is what the coordinator would have persisted.
    /// </summary>
    private static async Task<(Guid IngestId, List<SourceFile> Files)> SeedDownloadingPackAsync(
        PipelineTestHarness harness, params string[] fileNames)
    {
        var (ingestId, _, downloadId) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Some.Pack.1988-2007", $"Some.Pack.1988-2007/{fileNames[0]}",
            additionalSourceRelativePaths: [.. fileNames.Skip(1).Select(name => $"Some.Pack.1988-2007/{name}")]);

        using var scope = harness.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var download = await database.Downloads.SingleAsync(item => item.Id == downloadId);
        download.State = DownloadState.Downloading;
        await database.SaveChangesAsync();

        var files = await database.SourceFiles
            .Where(file => file.IngestItemId == ingestId).OrderBy(file => file.TorrentFileIndex).ToListAsync();
        return (ingestId, files);
    }

    private static async Task CompleteDownloadAsync(PipelineTestHarness harness, Guid ingestId)
    {
        using var scope = harness.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var item = await database.IngestItems.SingleAsync(candidate => candidate.Id == ingestId);
        var download = await database.Downloads.SingleAsync(candidate => candidate.Id == item.DownloadId);
        download.State = DownloadState.Completed;
        await database.SaveChangesAsync();
    }

    [Fact]
    public async Task Files_matched_while_the_torrent_is_still_downloading_publish_without_a_review_stop()
    {
        using var harness = new PipelineTestHarness();
        // Any provider search would be a bug: every file is confirmed before identify ever runs.
        harness.MetadataProvider.OnSearch = _ => throw new InvalidOperationException("Identify must not search for pre-matched files.");

        var (ingestId, files) = await SeedDownloadingPackAsync(harness, "Movie.One.mkv", "Movie.Two.mkv");

        // A drive while the transfer runs parks at the download stage — it must not fail or review.
        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using (var scope = harness.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var parked = await database.IngestItems.SingleAsync(item => item.Id == ingestId);
            Assert.Equal(IngestStage.Download, parked.Stage);
            // A deferred stage stays Pending with a retry time — it is not a review or a failure.
            Assert.Equal(IngestStatus.Pending, parked.Status);
            Assert.NotNull(parked.NextAttemptAt);

            // The operator resolves the pack from the file list, mid-download.
            var ingestService = scope.ServiceProvider.GetRequiredService<IngestService>();
            Assert.Equal(MatchOutcome.Matched, await ingestService.MatchAsync(ingestId, Grouped(
                new MatchGroupRequest(MediaKind.Movie, "tmdb", "562", "Die Hard", 1988, [new MatchFileRequest(files[0].Id, null, null)]),
                new MatchGroupRequest(MediaKind.Movie, "tmdb", "1573", "Die Hard 2", 1990, [new MatchFileRequest(files[1].Id, null, null)])),
                CancellationToken.None));
        }

        using (var scope = harness.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var confirmed = await database.SourceFiles.Where(file => file.IngestItemId == ingestId).ToListAsync();
            Assert.All(confirmed, file => Assert.Equal(SourceFileAssignmentStatus.Confirmed, file.AssignmentStatus));

            // Matching early creates the movies but publishes nothing: an unpublished item carries no
            // PublicId and is invisible to every library read until the pipeline gets there.
            Assert.Equal(2, await database.MediaItems.CountAsync(item => item.Kind == MediaKind.Movie));
            Assert.False(await database.MediaItems.AnyAsync(item => item.PublicId != null));
        }

        // Re-driving before completion still just waits for the transfer.
        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);
        await CompleteDownloadAsync(harness, ingestId);
        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var verifyScope = harness.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var ingest = await verifyDb.IngestItems.SingleAsync(item => item.Id == ingestId);
        Assert.Equal(IngestStatus.Done, ingest.Status);

        var movies = await verifyDb.MediaItems.Where(item => item.Kind == MediaKind.Movie).OrderBy(item => item.Year).ToListAsync();
        Assert.Equal(["Die Hard", "Die Hard 2"], movies.Select(movie => movie.Title));
        Assert.All(movies, movie => Assert.False(string.IsNullOrEmpty(movie.PublicId)));
        Assert.All(movies, movie => Assert.NotNull(movie.LibraryPath));
    }

    [Fact]
    public async Task Partially_pre_matched_pack_auto_identifies_only_the_rest()
    {
        using var harness = new PipelineTestHarness();
        harness.MetadataProvider.OnSearch = query => [new MetadataCandidate(new ProviderRef("tmdb", "1572"), query.Title, query.Year, 1.0)];

        var (ingestId, files) = await SeedDownloadingPackAsync(harness, "Movie.One.mkv", "Die.Hard.4.0.2007.mkv");

        using (var scope = harness.CreateScope())
        {
            var ingestService = scope.ServiceProvider.GetRequiredService<IngestService>();
            Assert.Equal(MatchOutcome.Matched, await ingestService.MatchAsync(ingestId, Grouped(
                new MatchGroupRequest(MediaKind.Movie, "tmdb", "562", "Die Hard", 1988, [new MatchFileRequest(files[0].Id, null, null)])),
                CancellationToken.None));
        }

        await CompleteDownloadAsync(harness, ingestId);
        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var scope2 = harness.CreateScope();
        var database = scope2.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        Assert.Equal(IngestStatus.Done, (await database.IngestItems.SingleAsync(item => item.Id == ingestId)).Status);

        // The pre-matched file kept the operator's identity; the other one was identified from its name.
        var titles = await database.MediaItems.Where(item => item.Kind == MediaKind.Movie).Select(item => item.Title).ToListAsync();
        Assert.Contains("Die Hard", titles);
        Assert.Contains(titles, title => title.Contains("Die Hard 4", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, titles.Count);
    }

    [Fact]
    public async Task Deleting_a_pre_matched_download_leaves_no_published_leftovers()
    {
        using var harness = new PipelineTestHarness();
        harness.MetadataProvider.OnSearch = _ => [];

        var (ingestId, files) = await SeedDownloadingPackAsync(harness, "Movie.One.mkv", "Movie.Two.mkv");

        using (var scope = harness.CreateScope())
        {
            var ingestService = scope.ServiceProvider.GetRequiredService<IngestService>();
            Assert.Equal(MatchOutcome.Matched, await ingestService.MatchAsync(ingestId, Grouped(
                new MatchGroupRequest(MediaKind.Movie, "tmdb", "562", "Die Hard", 1988, [new MatchFileRequest(files[0].Id, null, null)]),
                new MatchGroupRequest(MediaKind.Movie, "tmdb", "1573", "Die Hard 2", 1990, [new MatchFileRequest(files[1].Id, null, null)])),
                CancellationToken.None));

            // The operator changes their mind and removes the download before it finishes.
            Assert.True(await ingestService.DeleteAsync(ingestId, CancellationToken.None));
        }

        using var verifyScope = harness.CreateScope();
        var database = verifyScope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        Assert.False(await database.IngestItems.AnyAsync(item => item.Id == ingestId));
        Assert.False(await database.SourceFiles.AnyAsync(file => file.IngestItemId == ingestId));

        // The movies the early match created stay behind as unpublished shells: no PublicId, no sources,
        // so no library read, collection page, or client can see them — and a later download of the same
        // title reuses the row by identity instead of duplicating it.
        var leftovers = await database.MediaItems.Where(item => item.Kind == MediaKind.Movie).ToListAsync();
        Assert.All(leftovers, movie => Assert.Null(movie.PublicId));
        Assert.False(await database.MediaSources.AnyAsync());
    }
}
