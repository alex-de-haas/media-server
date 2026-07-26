using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using MediaServer.Api.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Api.Tests.Pipeline;

/// <summary>
/// A work lives in exactly one catalog. Ingesting a title that another catalog already publishes parks
/// the item for review instead of creating a second <see cref="MediaItem"/>, and the review's Retarget
/// action re-homes the whole ingest — staging and all — into the catalog that owns the title, where it
/// publishes as another version.
/// </summary>
public sealed class CrossCatalogGateTests
{
    private static void StrongMatch(PipelineTestHarness harness, string id = "27205") =>
        harness.MetadataProvider.OnSearch = query => [new MetadataCandidate(new ProviderRef("tmdb", id), query.Title, query.Year, 1.0)];

    [Fact]
    public async Task A_movie_already_in_another_catalog_parks_for_review_instead_of_duplicating()
    {
        using var harness = new PipelineTestHarness();
        StrongMatch(harness);

        var first = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Inception.2010.1080p", "Inception.2010.1080p/movie.mkv");
        await harness.Orchestrator.DriveAsync(first.IngestId, CancellationToken.None);

        // The same movie arrives into a different catalog.
        var second = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Inception.2010.2160p", "Inception.2010.2160p/movie.mkv");
        await harness.Orchestrator.DriveAsync(second.IngestId, CancellationToken.None);

        using var scope = harness.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();

        var parked = await database.IngestItems.SingleAsync(item => item.Id == second.IngestId);
        Assert.Equal(IngestStatus.NeedsReview, parked.Status);
        Assert.Equal(first.CatalogId, parked.ConflictCatalogId);
        Assert.Contains("already in catalog", parked.LastError);
        // Exactly one movie exists — the gate is what stops the second catalog from minting a twin.
        Assert.Equal(1, await database.MediaItems.CountAsync(item => item.Kind == MediaKind.Movie));
    }

    [Fact]
    public async Task A_second_copy_in_the_same_catalog_still_merges_as_a_version()
    {
        using var harness = new PipelineTestHarness();
        StrongMatch(harness);

        var first = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Inception.2010.1080p", "Inception.2010.1080p/movie.mkv");
        await harness.Orchestrator.DriveAsync(first.IngestId, CancellationToken.None);
        var second = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Inception.2010.720p", "Inception.2010.720p/movie.mkv", first.CatalogId);
        await harness.Orchestrator.DriveAsync(second.IngestId, CancellationToken.None);

        using var scope = harness.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        // The gate is about *other* catalogs: within one catalog the ordinary version merge is untouched.
        var ingest = await database.IngestItems.SingleAsync(item => item.Id == second.IngestId);
        Assert.Equal(IngestStatus.Done, ingest.Status);
        Assert.Null(ingest.ConflictCatalogId);
        var movie = await database.MediaItems.SingleAsync(item => item.Kind == MediaKind.Movie);
        Assert.Equal(2, await database.MediaSources.CountAsync(source => source.MediaItemId == movie.Id));
    }

    [Fact]
    public async Task A_series_already_in_another_catalog_parks_for_review()
    {
        using var harness = new PipelineTestHarness();
        StrongMatch(harness, id: "1396");

        var first = await harness.SeedCompletedDownloadAsync(
            CatalogType.Series, "Breaking.Bad.S01E01.1080p", "Breaking.Bad.S01E01.1080p/Breaking.Bad.S01E01.mkv");
        await harness.Orchestrator.DriveAsync(first.IngestId, CancellationToken.None);

        // A different episode of the same show, into another catalog: the conflict is at the series level.
        var second = await harness.SeedCompletedDownloadAsync(
            CatalogType.Series, "Breaking.Bad.S01E02.2160p", "Breaking.Bad.S01E02.2160p/Breaking.Bad.S01E02.mkv");
        await harness.Orchestrator.DriveAsync(second.IngestId, CancellationToken.None);

        using var scope = harness.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var parked = await database.IngestItems.SingleAsync(item => item.Id == second.IngestId);
        Assert.Equal(IngestStatus.NeedsReview, parked.Status);
        Assert.Equal(first.CatalogId, parked.ConflictCatalogId);
        Assert.Equal(1, await database.MediaItems.CountAsync(item => item.Kind == MediaKind.Series));
    }

    [Fact]
    public async Task Retarget_rehomes_the_ingest_and_publishes_it_as_another_version()
    {
        using var harness = new PipelineTestHarness();
        StrongMatch(harness);

        var first = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Inception.2010.1080p", "Inception.2010.1080p/movie.mkv");
        await harness.Orchestrator.DriveAsync(first.IngestId, CancellationToken.None);
        var second = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Inception.2010.2160p", "Inception.2010.2160p/movie.mkv");
        await harness.Orchestrator.DriveAsync(second.IngestId, CancellationToken.None);

        using (var scope = harness.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IngestService>();
            Assert.Equal(RetargetOutcome.Retargeted, await service.RetargetAsync(second.IngestId, CancellationToken.None));
        }

        await harness.Orchestrator.DriveAsync(second.IngestId, CancellationToken.None);

        using var verify = harness.CreateScope();
        var database = verify.ServiceProvider.GetRequiredService<MediaServerDbContext>();

        var ingest = await database.IngestItems.SingleAsync(item => item.Id == second.IngestId);
        Assert.Equal(IngestStatus.Done, ingest.Status);
        Assert.Equal(first.CatalogId, ingest.CatalogId); // re-homed
        Assert.Null(ingest.ConflictCatalogId);

        // Still one movie, now holding both files as versions — exactly what the same-catalog path yields.
        var movie = await database.MediaItems.SingleAsync(item => item.Kind == MediaKind.Movie);
        Assert.Equal(first.CatalogId, movie.CatalogId);
        var sources = await database.MediaSources.Where(source => source.MediaItemId == movie.Id).ToListAsync();
        Assert.Equal(2, sources.Count);
        Assert.Equal(2, sources.Select(source => source.Path).Distinct().Count());

        // Both library files live under the retarget destination's root.
        var catalog = await database.Catalogs.SingleAsync(item => item.Id == first.CatalogId);
        foreach (var source in sources)
        {
            Assert.True(File.Exists(Path.Combine(catalog.Root, source.Path.Replace('/', Path.DirectorySeparatorChar))));
        }
    }

    [Fact]
    public async Task Retarget_refuses_an_item_that_is_not_parked_over_a_conflict()
    {
        using var harness = new PipelineTestHarness();
        StrongMatch(harness);

        var only = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Inception.2010.1080p", "Inception.2010.1080p/movie.mkv");
        await harness.Orchestrator.DriveAsync(only.IngestId, CancellationToken.None);

        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IngestService>();
        Assert.Equal(RetargetOutcome.NoConflict, await service.RetargetAsync(only.IngestId, CancellationToken.None));
        Assert.Equal(RetargetOutcome.NotFound, await service.RetargetAsync(Guid.NewGuid(), CancellationToken.None));
    }
}
