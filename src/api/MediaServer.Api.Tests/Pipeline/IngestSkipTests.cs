using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using MediaServer.Api.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Api.Tests.Pipeline;

public sealed class IngestSkipTests
{
    [Fact]
    public async Task Skipping_an_unmatchable_extra_unblocks_the_rest_of_the_pack()
    {
        using var harness = new PipelineTestHarness();
        // Episode files resolve strongly; the extra (no episode number in its name) gets no candidate at
        // all — the provider has no entry for a creditless OP/ED.
        harness.MetadataProvider.OnSearch = query => query.Episode is not null
            ? [new MetadataCandidate(new ProviderRef("tmdb", "31911"), "Fullmetal Alchemist: Brotherhood", 2009, 1.0)]
            : [];

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

            var extraId = await database.SourceFiles
                .Where(file => file.AssignmentStatus == SourceFileAssignmentStatus.NeedsReview)
                .Select(file => file.Id)
                .SingleAsync();

            var service = scope.ServiceProvider.GetRequiredService<IngestService>();
            Assert.True(await service.SkipAsync(ingestId, new SkipRequest([extraId]), CancellationToken.None));
        }

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var verifyScope = harness.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var ingest = await verifyDb.IngestItems.SingleAsync(item => item.Id == ingestId);
        Assert.Equal(IngestStatus.Done, ingest.Status);

        // The episode published; the skipped extra stayed unmapped and left the library untouched.
        Assert.True(await verifyDb.MediaItems.AnyAsync(item => item.Kind == MediaKind.Episode && item.PublicId != null));
        var skipped = await verifyDb.SourceFiles.SingleAsync(file => file.AssignmentStatus == SourceFileAssignmentStatus.Skipped);
        Assert.Null(skipped.MediaItemId);

        // The extra was cleaned up with the .incoming/ staging folder once the episode moved out.
        var catalog = await verifyDb.Catalogs.SingleAsync(item => item.Id == catalogId);
        var skippedAbsolute = Path.Combine(catalog.Root, skipped.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.False(File.Exists(skippedAbsolute));
    }

    [Fact]
    public async Task Skipping_every_file_completes_the_ingest_without_publishing()
    {
        using var harness = new PipelineTestHarness();
        harness.MetadataProvider.OnSearch = _ => [];

        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Series, "FMA Extras", "FMA/Fullmetal Alchemist Brotherhood (Creditless OP 1).mkv");
        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using (var scope = harness.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var fileId = await database.SourceFiles.Select(file => file.Id).SingleAsync();
            var service = scope.ServiceProvider.GetRequiredService<IngestService>();
            Assert.True(await service.SkipAsync(ingestId, new SkipRequest([fileId]), CancellationToken.None));
        }

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var verifyScope = harness.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var ingest = await verifyDb.IngestItems.SingleAsync(item => item.Id == ingestId);
        Assert.Equal(IngestStatus.Done, ingest.Status);
        Assert.Null(ingest.MediaItemId);
        Assert.False(await verifyDb.MediaItems.AnyAsync());
    }

    [Fact]
    public async Task Skipping_a_confirmed_file_is_rejected()
    {
        using var harness = new PipelineTestHarness();
        harness.MetadataProvider.OnSearch = query =>
            [new MetadataCandidate(new ProviderRef("tmdb", "27205"), query.Title, query.Year, 1.0)];

        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Inception.2010.1080p", "Inception.2010.1080p/movie.mkv");
        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var scope = harness.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var fileId = await database.SourceFiles.Select(file => file.Id).SingleAsync();

        var service = scope.ServiceProvider.GetRequiredService<IngestService>();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SkipAsync(ingestId, new SkipRequest([fileId]), CancellationToken.None));
    }

    [Fact]
    public async Task Skipping_an_unknown_file_id_is_rejected()
    {
        using var harness = new PipelineTestHarness();
        harness.MetadataProvider.OnSearch = _ => [];

        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Series, "FMA Extras", "FMA/Fullmetal Alchemist Brotherhood (Creditless OP 1).mkv");
        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IngestService>();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SkipAsync(ingestId, new SkipRequest([Guid.NewGuid()]), CancellationToken.None));
    }
}
