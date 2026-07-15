using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using MediaServer.Api.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Api.Tests.Pipeline;

public sealed class IngestExtrasTests
{
    private static readonly MetadataCandidate FmaSeries =
        new(new ProviderRef("tmdb", "31911"), "Fullmetal Alchemist Brotherhood", 2009, 1.0);

    [Fact]
    public async Task Attaching_an_extra_publishes_it_under_the_series()
    {
        using var harness = new PipelineTestHarness();
        harness.MetadataProvider.OnSearch = query => query.Episode is not null ? [FmaSeries] : [];

        var (ingestId, catalogId, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Series, "FMA Brotherhood S01",
            "FMA/Fullmetal Alchemist Brotherhood S01E01.mkv",
            additionalSourceRelativePaths: ["FMA/Fullmetal Alchemist Brotherhood (Creditless ED 1).mkv"]);

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using (var scope = harness.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var parked = await database.IngestItems.SingleAsync(item => item.Id == ingestId);
            Assert.Equal(IngestStatus.NeedsReview, parked.Status);

            var extraFileId = await database.SourceFiles
                .Where(file => file.AssignmentStatus == SourceFileAssignmentStatus.NeedsReview)
                .Select(file => file.Id)
                .SingleAsync();

            var service = scope.ServiceProvider.GetRequiredService<IngestService>();
            Assert.Equal(AssignExtrasOutcome.Assigned, await service.AssignExtrasAsync(ingestId,
                new AssignExtrasRequest([extraFileId], "tmdb", "31911", "Fullmetal Alchemist Brotherhood", 2009, null),
                CancellationToken.None));
        }

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var verifyScope = harness.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var ingest = await verifyDb.IngestItems.SingleAsync(item => item.Id == ingestId);
        Assert.Equal(IngestStatus.Done, ingest.Status);

        // The extra is a published, playable non-episode video parented to the series, with no provider
        // identity of its own.
        var series = await verifyDb.MediaItems.SingleAsync(item => item.Kind == MediaKind.Series);
        var extra = await verifyDb.MediaItems.SingleAsync(item => item.Kind == MediaKind.Video);
        Assert.Equal("Creditless Ending 1", extra.Title);
        Assert.Equal(series.Id, extra.SeriesId);
        Assert.Equal(series.Id, extra.ParentId);
        Assert.Null(extra.SeasonId);
        Assert.Null(extra.IdentityProvider);
        Assert.Null(extra.IdentityProviderId);
        Assert.NotNull(extra.PublicId);

        // Organized into the show's extras/ folder, moved on disk, and probed into a playable source.
        Assert.Equal("Fullmetal Alchemist Brotherhood (2009)/extras/Creditless Ending 1.mkv", extra.LibraryPath);
        var catalog = await verifyDb.Catalogs.SingleAsync(item => item.Id == catalogId);
        var absolute = Path.Combine(catalog.Root, extra.LibraryPath!.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(absolute));
        Assert.True(await verifyDb.MediaSources.AnyAsync(source => source.MediaItemId == extra.Id));

        // The episode itself published normally alongside the extra.
        Assert.True(await verifyDb.MediaItems.AnyAsync(item => item.Kind == MediaKind.Episode && item.PublicId != null));
    }

    [Fact]
    public async Task Attaching_an_extra_with_a_season_parents_it_under_that_season()
    {
        using var harness = new PipelineTestHarness();
        harness.MetadataProvider.OnSearch = _ => [];

        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Anime, "FMA Extras", "FMA/Fullmetal Alchemist Brotherhood NCOP1.mkv");
        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using (var scope = harness.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var fileId = await database.SourceFiles.Select(file => file.Id).SingleAsync();
            var service = scope.ServiceProvider.GetRequiredService<IngestService>();
            Assert.Equal(AssignExtrasOutcome.Assigned, await service.AssignExtrasAsync(ingestId,
                new AssignExtrasRequest([fileId], "tmdb", "31911", "Fullmetal Alchemist Brotherhood", 2009, Season: 1),
                CancellationToken.None));
        }

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var verifyScope = harness.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        Assert.Equal(IngestStatus.Done, (await verifyDb.IngestItems.SingleAsync(item => item.Id == ingestId)).Status);

        var season = await verifyDb.MediaItems.SingleAsync(item => item.Kind == MediaKind.Season);
        Assert.Equal(1, season.IndexNumber);

        var extra = await verifyDb.MediaItems.SingleAsync(item => item.Kind == MediaKind.Video);
        Assert.Equal("Creditless Opening 1", extra.Title);
        Assert.Equal(season.Id, extra.SeasonId);
        Assert.Equal(season.Id, extra.ParentId);
        Assert.NotNull(extra.SeriesId);

        // Season-scoped or not, the file itself lives in the show-level extras/ folder.
        Assert.Equal("Fullmetal Alchemist Brotherhood (2009)/extras/Creditless Opening 1.mkv", extra.LibraryPath);
    }

    [Fact]
    public async Task Same_titled_extras_in_one_batch_become_distinct_items()
    {
        using var harness = new PipelineTestHarness();
        harness.MetadataProvider.OnSearch = _ => [];

        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Anime, "FMA NC Pack", "FMA/NCOP-A.mkv",
            additionalSourceRelativePaths: ["FMA/NCOP-B.mkv"]);
        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using (var scope = harness.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var fileIds = await database.SourceFiles.Select(file => file.Id).ToListAsync();
            var service = scope.ServiceProvider.GetRequiredService<IngestService>();
            Assert.Equal(AssignExtrasOutcome.Assigned, await service.AssignExtrasAsync(ingestId,
                new AssignExtrasRequest(fileIds, "tmdb", "31911", "Fullmetal Alchemist Brotherhood", 2009, null),
                CancellationToken.None));
        }

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var verifyScope = harness.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var titles = await verifyDb.MediaItems
            .Where(item => item.Kind == MediaKind.Video)
            .Select(item => item.Title)
            .OrderBy(title => title)
            .ToListAsync();
        Assert.Equal(["Creditless Opening", "Creditless Opening 2"], titles);
    }

    [Fact]
    public async Task Classified_extras_park_for_review_with_a_hint_and_no_provider_search()
    {
        using var harness = new PipelineTestHarness();
        var searches = 0;
        harness.MetadataProvider.OnSearch = _ =>
        {
            searches++;
            return [];
        };

        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Anime, "FMA Extras", "FMA/Fullmetal Alchemist Brotherhood NCOP1.mkv");
        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        Assert.Equal(0, searches);

        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IngestService>();
        var response = await service.GetAsync(ingestId, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(IngestStatus.NeedsReview.ToString(), response.Status);
        Assert.Contains("extra", response.LastError, StringComparison.OrdinalIgnoreCase);

        // The review UI's pre-suggestion rides on the response's classification fields.
        var file = Assert.Single(response.SourceFiles);
        Assert.Equal(nameof(ExtraKind.CreditlessOpening), file.ExtraKind);
        Assert.Equal("Creditless Opening 1", file.ExtraTitle);
        Assert.False(file.ExtraSuggestSkip);
    }

    [Fact]
    public async Task Attaching_a_file_of_an_identified_item_is_rejected()
    {
        using var harness = new PipelineTestHarness();
        harness.MetadataProvider.OnSearch = query => query.Episode is not null ? [FmaSeries] : [];

        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Series, "FMA S01", "FMA/Fullmetal Alchemist Brotherhood S01E01.mkv");
        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var scope = harness.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var fileId = await database.SourceFiles.Select(file => file.Id).SingleAsync();

        var service = scope.ServiceProvider.GetRequiredService<IngestService>();
        Assert.Equal(AssignExtrasOutcome.AlreadyOrganized, await service.AssignExtrasAsync(
            ingestId, new AssignExtrasRequest([fileId], "tmdb", "31911", "Fullmetal Alchemist Brotherhood", 2009, null),
            CancellationToken.None));
    }

    [Fact]
    public async Task Attaching_an_auto_matched_file_while_the_item_is_in_review_overrides_the_mapping()
    {
        using var harness = new PipelineTestHarness();
        // "S01E00" auto-matches as an episode even though it's really an OVA the operator wants as an
        // extra; the creditless ED parks the batch in review, leaving the mis-match still overridable.
        harness.MetadataProvider.OnSearch = query => query.Episode is not null ? [FmaSeries] : [];

        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Series, "FMA Brotherhood S01",
            "FMA/Fullmetal Alchemist Brotherhood S01E00.mkv",
            additionalSourceRelativePaths: ["FMA/Fullmetal Alchemist Brotherhood (Creditless ED 1).mkv"]);
        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using (var scope = harness.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            Assert.Equal(IngestStatus.NeedsReview, (await database.IngestItems.SingleAsync(item => item.Id == ingestId)).Status);
            var confirmed = await database.SourceFiles.SingleAsync(file => file.AssignmentStatus == SourceFileAssignmentStatus.Confirmed);
            var autoMatchedItemId = confirmed.MediaItemId;

            var service = scope.ServiceProvider.GetRequiredService<IngestService>();
            Assert.Equal(AssignExtrasOutcome.Assigned, await service.AssignExtrasAsync(
                ingestId, new AssignExtrasRequest([confirmed.Id], "tmdb", "31911", "Fullmetal Alchemist Brotherhood", 2009, null),
                CancellationToken.None));

            // The file now points at a Video extra instead of the auto-matched episode.
            var reassigned = await database.SourceFiles.SingleAsync(file => file.Id == confirmed.Id);
            Assert.NotEqual(autoMatchedItemId, reassigned.MediaItemId);
            var extra = await database.MediaItems.SingleAsync(item => item.Id == reassigned.MediaItemId);
            Assert.Equal(MediaKind.Video, extra.Kind);
        }
    }

    [Fact]
    public async Task Attaching_extras_in_a_movie_catalog_is_rejected()
    {
        using var harness = new PipelineTestHarness();
        harness.MetadataProvider.OnSearch = _ => [];

        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Some Movie", "Some Movie/behind-the-scenes.mkv");
        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var scope = harness.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var fileId = await database.SourceFiles.Select(file => file.Id).SingleAsync();

        var service = scope.ServiceProvider.GetRequiredService<IngestService>();
        Assert.Equal(AssignExtrasOutcome.MovieCatalog, await service.AssignExtrasAsync(
            ingestId, new AssignExtrasRequest([fileId], "tmdb", "31911", "Fullmetal Alchemist Brotherhood", 2009, null),
            CancellationToken.None));
    }
}
