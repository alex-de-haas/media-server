using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Jobs;
using MediaServer.Api.Realtime;
using MediaServer.Api.Tests.Pipeline;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.Catalogs;

/// <summary>
/// Admitting a catalog scan without waiting for it, and reporting what has been scanned.
/// </summary>
/// <remarks>
/// The scan route awaits the disk walk and answers when it finishes, which holds a request open for as
/// long as a large catalog takes — fine for a page watching a spinner, a timeout for anything else.
/// This is the started-not-awaited half, and the state it records is what lets an empty search result
/// say whether the library is empty or merely unread.
/// </remarks>
public sealed class CatalogScanCoordinatorTests : IDisposable
{
    private readonly JellyfinDatabase _db = new();
    private readonly MediaServerDbContext _context;
    private readonly CatalogScanQueue _queue = new();
    private readonly CatalogScanCoordinator _coordinator;

    public CatalogScanCoordinatorTests()
    {
        _context = _db.Create();
        _coordinator = new CatalogScanCoordinator(_context, new JobService(_context, new NullRealtimeNotifier()), _queue);
    }

    [Fact]
    public async Task An_unknown_catalog_is_refused_rather_than_queued()
    {
        var result = await _coordinator.RequestAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(CatalogScanRequestStatus.NotFound, result.Status);
        Assert.Null(result.JobId);
    }

    [Fact]
    public async Task A_second_request_is_refused_while_the_first_is_in_flight_and_admitted_after()
    {
        // The pair is the test: a coordinator that always refused would pass the middle assertion alone,
        // and one that never refused would pass the first.
        var catalogId = AddCatalog();

        Assert.Equal(CatalogScanRequestStatus.Started, (await _coordinator.RequestAsync(catalogId, default)).Status);
        Assert.Equal(CatalogScanRequestStatus.AlreadyRunning, (await _coordinator.RequestAsync(catalogId, default)).Status);

        // The worker releases the reservation on every path; this stands in for that.
        _queue.Release(catalogId);
        Assert.Equal(CatalogScanRequestStatus.Started, (await _coordinator.RequestAsync(catalogId, default)).Status);
    }

    [Fact]
    public async Task Queueing_every_catalog_skips_the_ones_already_running()
    {
        // Refusing the whole request because one catalog is busy would be the wrong answer to "scan the
        // library": a run already under way is the outcome the operator wanted.
        var busy = AddCatalog();
        AddCatalog();
        await _coordinator.RequestAsync(busy, default);

        Assert.Equal(1, await _coordinator.RequestAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_catalog_nothing_has_scanned_is_told_apart_from_one_that_finished()
    {
        // The distinction an empty search result rests on. Reported from the job rows, so it cannot
        // disagree with the scan that actually ran.
        var never = AddCatalog();
        var finished = AddCatalog();
        var completedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        AddJob(finished, JobStatus.Completed, completedAt);

        var state = await _coordinator.ListStateAsync(CancellationToken.None);

        Assert.True(state.Single(entry => entry.CatalogId == never).NeverScanned);
        var done = state.Single(entry => entry.CatalogId == finished);
        Assert.False(done.NeverScanned);
        Assert.Equal(completedAt, done.LastCompletedAt);
    }

    [Fact]
    public async Task A_failed_scan_does_not_count_as_having_been_scanned()
    {
        // Otherwise a catalog whose disk was unreadable reports a last-scanned time, and the empty
        // result it produces reads as "the library really is empty".
        var catalogId = AddCatalog();
        AddJob(catalogId, JobStatus.Failed, DateTimeOffset.UtcNow);

        var state = Assert.Single(await _coordinator.ListStateAsync(CancellationToken.None));

        Assert.True(state.NeverScanned);
        Assert.Null(state.LastCompletedAt);
    }

    [Fact]
    public async Task A_running_scan_is_reported_as_running_and_not_as_never_scanned()
    {
        var catalogId = AddCatalog();
        AddJob(catalogId, JobStatus.Running, completedAt: null);

        var state = Assert.Single(await _coordinator.ListStateAsync(CancellationToken.None));

        Assert.True(state.Scanning);
        Assert.False(state.NeverScanned);
        Assert.Single(await _coordinator.ListActiveAsync(CancellationToken.None));
    }

    private Guid AddCatalog()
    {
        var now = DateTimeOffset.UtcNow;
        var catalog = new Catalog
        {
            Id = Guid.NewGuid(),
            Name = $"Catalog {Guid.NewGuid():N}",
            Type = CatalogType.Movie,
            Root = $"/catalog/{Guid.NewGuid():N}",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Catalogs.Add(catalog);
        _context.SaveChanges();
        return catalog.Id;
    }

    private void AddJob(Guid catalogId, JobStatus status, DateTimeOffset? completedAt)
    {
        _context.Jobs.Add(new Job
        {
            Id = Guid.NewGuid(),
            Type = CatalogScanCoordinator.JobType,
            RelatedType = "catalog",
            RelatedId = catalogId,
            Status = status,
            CompletedAt = completedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
    }
}
