using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Api.Tests.Pipeline;

/// <summary>
/// Grouped manual match: one review request carrying several provider identities (a franchise pack whose
/// files belong to different movies), resolved atomically with a single re-drive.
/// </summary>
public sealed class IngestGroupedMatchTests
{
    private static MatchRequest Grouped(params MatchGroupRequest[] groups) =>
        new(MediaKind.Movie, "", "", "", null, [], [.. groups]);

    private static async Task<(Guid IngestId, List<SourceFile> Files)> SeedParkedPackAsync(
        PipelineTestHarness harness, params string[] fileNames)
    {
        // No usable candidates: every video parks NeedsReview, like an unparseable franchise pack.
        harness.MetadataProvider.OnSearch = _ => [];
        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Some.Pack.1988-2007", $"Some.Pack.1988-2007/{fileNames[0]}",
            additionalSourceRelativePaths: [.. fileNames.Skip(1).Select(name => $"Some.Pack.1988-2007/{name}")]);
        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var scope = harness.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        Assert.Equal(IngestStatus.NeedsReview, (await database.IngestItems.SingleAsync(item => item.Id == ingestId)).Status);
        var files = await database.SourceFiles.Where(file => file.IngestItemId == ingestId).OrderBy(file => file.TorrentFileIndex).ToListAsync();
        return (ingestId, files);
    }

    [Fact]
    public async Task Grouped_match_resolves_a_franchise_pack_to_separate_published_movies()
    {
        using var harness = new PipelineTestHarness();
        var (ingestId, files) = await SeedParkedPackAsync(harness, "Movie.One.mkv", "Movie.Two.mkv");

        using (var scope = harness.CreateScope())
        {
            var ingestService = scope.ServiceProvider.GetRequiredService<IngestService>();
            var outcome = await ingestService.MatchAsync(ingestId, Grouped(
                new MatchGroupRequest(MediaKind.Movie, "tmdb", "101", "Movie One", 1988, [new MatchFileRequest(files[0].Id, null, null)]),
                new MatchGroupRequest(MediaKind.Movie, "tmdb", "102", "Movie Two", 1990, [new MatchFileRequest(files[1].Id, null, null)])),
                CancellationToken.None);
            Assert.Equal(MatchOutcome.Matched, outcome);
        }

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var verifyScope = harness.CreateScope();
        var database = verifyScope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        Assert.Equal(IngestStatus.Done, (await database.IngestItems.SingleAsync(item => item.Id == ingestId)).Status);

        // Two separate published movies, each organized to its own library path from its own group identity.
        var movies = await database.MediaItems.Where(item => item.Kind == MediaKind.Movie).OrderBy(item => item.Title).ToListAsync();
        Assert.Equal(2, movies.Count);
        Assert.All(movies, movie => Assert.False(string.IsNullOrEmpty(movie.PublicId)));
        Assert.Equal(["Movie One", "Movie Two"], movies.Select(movie => movie.Title));
        Assert.Equal(["101", "102"], movies.Select(movie => movie.IdentityProviderId));
        Assert.NotEqual(movies[0].LibraryPath, movies[1].LibraryPath);

        var mapped = await database.SourceFiles.Where(file => file.IngestItemId == ingestId).OrderBy(file => file.TorrentFileIndex).ToListAsync();
        Assert.Equal(movies[0].Id, mapped[0].MediaItemId);
        Assert.Equal(movies[1].Id, mapped[1].MediaItemId);
    }

    [Fact]
    public async Task Grouped_match_assigns_an_audio_track_to_its_groups_movie()
    {
        using var harness = new PipelineTestHarness();
        var (ingestId, files) = await SeedParkedPackAsync(harness, "Movie.One.mkv", "Movie.Two.mkv", "Movie.One.Rus.mka");

        using (var scope = harness.CreateScope())
        {
            var ingestService = scope.ServiceProvider.GetRequiredService<IngestService>();
            var outcome = await ingestService.MatchAsync(ingestId, Grouped(
                new MatchGroupRequest(MediaKind.Movie, "tmdb", "101", "Movie One", 1988,
                    [new MatchFileRequest(files[0].Id, null, null), new MatchFileRequest(files[2].Id, null, null)]),
                new MatchGroupRequest(MediaKind.Movie, "tmdb", "102", "Movie Two", 1990, [new MatchFileRequest(files[1].Id, null, null)])),
                CancellationToken.None);
            Assert.Equal(MatchOutcome.Matched, outcome);

            // The audio track carries its group's movie — the mux stage merges by shared MediaItemId.
            var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var audio = await database.SourceFiles.SingleAsync(file => file.Id == files[2].Id);
            var movieOne = await database.MediaItems.SingleAsync(item => item.IdentityProviderId == "101");
            Assert.Equal(movieOne.Id, audio.MediaItemId);
        }

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var verifyScope = harness.CreateScope();
        var database2 = verifyScope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        Assert.Equal(IngestStatus.Done, (await database2.IngestItems.SingleAsync(item => item.Id == ingestId)).Status);

        // Exactly one mux ran — into movie one's video, not movie two's.
        var plan = Assert.Single(harness.AudioMuxer.Plans);
        Assert.Contains("Movie.One", plan.VideoAbsolutePath);
        Assert.Equal(SourceFileAssignmentStatus.Merged, (await database2.SourceFiles.SingleAsync(file => file.Id == files[2].Id)).AssignmentStatus);
    }

    [Fact]
    public async Task Match_rejects_a_file_repeated_across_groups_without_re_driving()
    {
        using var harness = new PipelineTestHarness();
        var (ingestId, files) = await SeedParkedPackAsync(harness, "Movie.One.mkv");

        using var scope = harness.CreateScope();
        var ingestService = scope.ServiceProvider.GetRequiredService<IngestService>();
        var outcome = await ingestService.MatchAsync(ingestId, Grouped(
            new MatchGroupRequest(MediaKind.Movie, "tmdb", "101", "Movie One", 1988, [new MatchFileRequest(files[0].Id, null, null)]),
            new MatchGroupRequest(MediaKind.Movie, "tmdb", "102", "Movie Two", 1990, [new MatchFileRequest(files[0].Id, null, null)])),
            CancellationToken.None);
        Assert.Equal(MatchOutcome.FileNotFound, outcome);

        // The parked item must not have been flipped back to Pending by the rejected request.
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        Assert.Equal(IngestStatus.NeedsReview, (await database.IngestItems.SingleAsync(item => item.Id == ingestId)).Status);
    }

    [Fact]
    public async Task Groups_sharing_one_identity_reuse_a_single_movie()
    {
        using var harness = new PipelineTestHarness();
        var (ingestId, files) = await SeedParkedPackAsync(harness, "Movie.One.mkv", "Movie.One.Directors.Cut.mkv");

        using (var scope = harness.CreateScope())
        {
            // Two groups pinning the same provider identity (an operator quirk, not an error) must not
            // create two MediaItems for one identity — the resolver cannot see unflushed siblings.
            var ingestService = scope.ServiceProvider.GetRequiredService<IngestService>();
            var outcome = await ingestService.MatchAsync(ingestId, Grouped(
                new MatchGroupRequest(MediaKind.Movie, "tmdb", "101", "Movie One", 1988, [new MatchFileRequest(files[0].Id, null, null)]),
                new MatchGroupRequest(MediaKind.Movie, "tmdb", "101", "Movie One", 1988, [new MatchFileRequest(files[1].Id, null, null)])),
                CancellationToken.None);
            Assert.Equal(MatchOutcome.Matched, outcome);
        }

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var verifyScope = harness.CreateScope();
        var database = verifyScope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var movie = await database.MediaItems.SingleAsync(item => item.Kind == MediaKind.Movie);
        Assert.Equal(2, await database.MediaSources.CountAsync(source => source.MediaItemId == movie.Id));
    }
}
