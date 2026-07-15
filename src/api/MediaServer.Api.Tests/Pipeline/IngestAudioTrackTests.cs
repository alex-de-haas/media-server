using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using MediaServer.Api.Pipeline;
using MediaServer.Api.Probe;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Api.Tests.Pipeline;

/// <summary>
/// External audio tracks (a torrent's separate <c>.mka</c>/<c>.ac3</c> dubs): auto-matching to the batch's
/// videos in Identify, the review flow for unmatched tracks, and the mux stage that merges them into the
/// video before Organize.
/// </summary>
public sealed class IngestAudioTrackTests
{
    private static readonly MetadataCandidate FmaSeries =
        new(new ProviderRef("tmdb", "31911"), "Fullmetal Alchemist Brotherhood", 2009, 1.0);

    private static readonly MetadataCandidate SomeMovie =
        new(new ProviderRef("tmdb", "603"), "Some Movie", 2020, 1.0);

    /// <summary>An audio-only file with untagged streams, so the path-inferred language/title apply.</summary>
    private static ProbeResult UntaggedAudioProbe() =>
        new("mka", TimeSpan.FromMinutes(24).Ticks, 320_000, 50_000_000,
            [new ProbedStream(StreamType.Audio, 0, "ac3", null, null, null, null, null, null, null, 6, 48000, true, false, null)]);

    [Fact]
    public async Task Audio_tracks_match_their_episodes_and_are_muxed_into_the_videos()
    {
        using var harness = new PipelineTestHarness();
        harness.MetadataProvider.OnSearch = query => query.Episode is not null ? [FmaSeries] : [];
        var defaultProbe = harness.MediaProbe.OnProbe;
        harness.MediaProbe.OnProbe = path => path.EndsWith(".mka", StringComparison.Ordinal) ? UntaggedAudioProbe() : defaultProbe(path);

        var (ingestId, catalogId, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Series, "FMA Brotherhood S01",
            "FMA/Fullmetal Alchemist Brotherhood S01E01.mkv",
            additionalSourceRelativePaths:
            [
                "FMA/Fullmetal Alchemist Brotherhood S01E02.mkv",
                "FMA/Rus Sound/Fullmetal Alchemist Brotherhood S01E01.mka",
                "FMA/Rus Sound/Fullmetal Alchemist Brotherhood S01E02.mka",
            ]);

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var scope = harness.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        Assert.Equal(IngestStatus.Done, (await database.IngestItems.SingleAsync(item => item.Id == ingestId)).Status);

        // One mux per episode video, pairing each track with its own episode; the appended stream carries
        // the folder-inferred language and title because the track itself was untagged.
        Assert.Equal(2, harness.AudioMuxer.Plans.Count);
        foreach (var plan in harness.AudioMuxer.Plans)
        {
            var episodeToken = plan.VideoAbsolutePath.Contains("S01E01", StringComparison.Ordinal) ? "S01E01" : "S01E02";
            var input = Assert.Single(plan.AudioInputs);
            Assert.Contains(episodeToken, input.AbsolutePath, StringComparison.Ordinal);
            var stream = Assert.Single(input.Streams);
            Assert.Equal("rus", stream.Language);
            Assert.Equal("Rus Sound", stream.Title);
            Assert.Equal(1, plan.VideoAudioStreamCount); // The fake video probe reports one existing track.
        }

        // The consumed tracks are Merged (never organized/probed); the episodes publish normally and the
        // staging folder — including the freed .mka files — is swept.
        var files = await database.SourceFiles.ToListAsync();
        Assert.Equal(2, files.Count(file => file.AssignmentStatus == SourceFileAssignmentStatus.Merged));
        Assert.Equal(2, await database.MediaSources.CountAsync());

        var catalog = await database.Catalogs.SingleAsync(item => item.Id == catalogId);
        Assert.False(Directory.Exists(Path.Combine(catalog.Root, ".incoming")) &&
            Directory.EnumerateFileSystemEntries(Path.Combine(catalog.Root, ".incoming")).Any());
        foreach (var episode in await database.MediaItems.Where(item => item.Kind == MediaKind.Episode).ToListAsync())
        {
            Assert.EndsWith(".mkv", episode.LibraryPath);
            Assert.True(File.Exists(Path.Combine(catalog.Root, episode.LibraryPath!.Replace('/', Path.DirectorySeparatorChar))));
        }
    }

    [Fact]
    public async Task A_movie_batch_muxes_its_track_with_a_filename_inferred_language()
    {
        using var harness = new PipelineTestHarness();
        harness.MetadataProvider.OnSearch = _ => [SomeMovie];
        var defaultProbe = harness.MediaProbe.OnProbe;
        harness.MediaProbe.OnProbe = path => path.EndsWith(".ac3", StringComparison.Ordinal) ? UntaggedAudioProbe() : defaultProbe(path);

        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Some Movie 2020",
            "Some Movie 2020/Some Movie 2020.mkv",
            additionalSourceRelativePaths: ["Some Movie 2020/Some Movie 2020 rus.ac3"]);

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var scope = harness.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        Assert.Equal(IngestStatus.Done, (await database.IngestItems.SingleAsync(item => item.Id == ingestId)).Status);

        var plan = Assert.Single(harness.AudioMuxer.Plans);
        var stream = Assert.Single(Assert.Single(plan.AudioInputs).Streams);
        Assert.Equal("rus", stream.Language);
        Assert.Null(stream.Title); // Same folder as the movie — no dub-folder name to use.

        Assert.Equal(1, await database.SourceFiles.CountAsync(file => file.AssignmentStatus == SourceFileAssignmentStatus.Merged));
    }

    [Fact]
    public async Task An_unmatchable_track_parks_for_review_and_merges_after_a_manual_match()
    {
        using var harness = new PipelineTestHarness();
        harness.MetadataProvider.OnSearch = query => query.Episode is not null ? [FmaSeries] : [];

        // "… 01.mka" has no SxxEyy, so the series parser can't place it — the batch parks.
        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Series, "FMA Brotherhood S01",
            "FMA/Fullmetal Alchemist Brotherhood S01E01.mkv",
            additionalSourceRelativePaths: ["FMA/Rus Sound/Fullmetal Alchemist Brotherhood 01.mka"]);

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        Guid audioFileId;
        using (var scope = harness.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var parked = await database.IngestItems.SingleAsync(item => item.Id == ingestId);
            Assert.Equal(IngestStatus.NeedsReview, parked.Status);
            Assert.Contains("audio track", parked.LastError, StringComparison.OrdinalIgnoreCase);

            var audio = await database.SourceFiles.SingleAsync(file => file.AssignmentStatus == SourceFileAssignmentStatus.NeedsReview);
            audioFileId = audio.Id;

            // The response flags the row as audio so the review UI offers Episode/Skip (never Extra).
            var service = scope.ServiceProvider.GetRequiredService<IngestService>();
            var response = await service.GetAsync(ingestId, CancellationToken.None);
            Assert.True(response!.SourceFiles.Single(file => file.Id == audioFileId).IsAudio);

            // Matching the track to its episode is the "merge into that video" action.
            Assert.Equal(MatchOutcome.Matched, await service.MatchAsync(ingestId, new MatchRequest(
                MediaKind.Episode, "tmdb", "31911", "Fullmetal Alchemist Brotherhood", 2009,
                [new MatchFileRequest(audioFileId, 1, 1)]), CancellationToken.None));
        }

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var verifyScope = harness.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        Assert.Equal(IngestStatus.Done, (await verifyDb.IngestItems.SingleAsync(item => item.Id == ingestId)).Status);
        Assert.Equal(SourceFileAssignmentStatus.Merged, (await verifyDb.SourceFiles.SingleAsync(file => file.Id == audioFileId)).AssignmentStatus);
        Assert.Single(harness.AudioMuxer.Plans);
    }

    [Fact]
    public async Task An_audio_track_cannot_be_kept_as_an_extra()
    {
        using var harness = new PipelineTestHarness();
        harness.MetadataProvider.OnSearch = query => query.Episode is not null ? [FmaSeries] : [];

        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Series, "FMA Brotherhood S01",
            "FMA/Fullmetal Alchemist Brotherhood S01E01.mkv",
            additionalSourceRelativePaths: ["FMA/Rus Sound/Fullmetal Alchemist Brotherhood 01.mka"]);
        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var scope = harness.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var audio = await database.SourceFiles.SingleAsync(file => file.AssignmentStatus == SourceFileAssignmentStatus.NeedsReview);

        var service = scope.ServiceProvider.GetRequiredService<IngestService>();
        Assert.Equal(AssignExtrasOutcome.AudioFile, await service.AssignExtrasAsync(ingestId,
            new AssignExtrasRequest([audio.Id], "tmdb", "31911", "Fullmetal Alchemist Brotherhood", 2009, null),
            CancellationToken.None));
    }

    [Fact]
    public async Task A_skipped_track_lets_the_batch_proceed_without_muxing()
    {
        using var harness = new PipelineTestHarness();
        harness.MetadataProvider.OnSearch = query => query.Episode is not null ? [FmaSeries] : [];

        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Series, "FMA Brotherhood S01",
            "FMA/Fullmetal Alchemist Brotherhood S01E01.mkv",
            additionalSourceRelativePaths: ["FMA/Rus Sound/Fullmetal Alchemist Brotherhood 01.mka"]);
        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using (var scope = harness.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var audio = await database.SourceFiles.SingleAsync(file => file.AssignmentStatus == SourceFileAssignmentStatus.NeedsReview);
            var service = scope.ServiceProvider.GetRequiredService<IngestService>();
            Assert.Equal(SkipOutcome.Skipped, await service.SkipAsync(ingestId, new SkipRequest([audio.Id]), CancellationToken.None));
        }

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var verifyScope = harness.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        Assert.Equal(IngestStatus.Done, (await verifyDb.IngestItems.SingleAsync(item => item.Id == ingestId)).Status);
        Assert.Empty(harness.AudioMuxer.Plans);
    }
}
