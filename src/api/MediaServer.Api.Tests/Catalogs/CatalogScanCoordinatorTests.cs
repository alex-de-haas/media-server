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
    public async Task A_request_is_refused_while_a_scan_holds_the_catalog_and_admitted_once_it_lets_go()
    {
        // The reservation is taken by the scan itself, not by this coordinator — that is what makes it
        // visible to the synchronous route and the nightly job as well. So the busy state is set up the
        // way a scan sets it, and the pair is the test: a coordinator that always refused would pass the
        // middle assertion alone, one that never refused would pass the others.
        var catalogId = AddCatalog();

        Assert.Equal(CatalogScanRequestStatus.Started, (await _coordinator.RequestAsync(catalogId, default)).Status);

        _queue.TryReserve(catalogId);
        Assert.Equal(CatalogScanRequestStatus.AlreadyRunning, (await _coordinator.RequestAsync(catalogId, default)).Status);

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
        _queue.TryReserve(busy);

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
        MarkScanned(finished, completedAt);

        var state = await _coordinator.ListStateAsync(CancellationToken.None);

        Assert.True(state.Single(entry => entry.CatalogId == never).NeverScanned);
        var done = state.Single(entry => entry.CatalogId == finished);
        Assert.False(done.NeverScanned);
        Assert.Equal(completedAt, done.LastCompletedAt);
    }

    [Fact]
    public async Task A_scan_that_opened_a_job_but_never_finished_does_not_count_as_having_scanned()
    {
        // The state is stamped by the scan when it completes, not by the job that started one. A catalog
        // whose disk was unreadable must not report a last-scanned time, or the empty result it produces
        // reads as "the library really is empty".
        var catalogId = AddCatalog();
        AddJob(catalogId, JobStatus.Failed, DateTimeOffset.UtcNow);

        var state = Assert.Single(await _coordinator.ListStateAsync(CancellationToken.None));

        Assert.True(state.NeverScanned);
        Assert.Null(state.LastCompletedAt);
    }

    [Fact]
    public async Task A_scan_that_opened_no_job_at_all_still_counts()
    {
        // The finding this replaced: reading scan state from job rows reported a catalog scanned nightly
        // for months as never scanned, because the nightly job and the synchronous route open none.
        var catalogId = AddCatalog();
        MarkScanned(catalogId, new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero));

        var state = Assert.Single(await _coordinator.ListStateAsync(CancellationToken.None));

        Assert.False(state.NeverScanned);
        Assert.Empty(await _coordinator.ListActiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_scan_holding_the_catalog_without_a_job_still_reads_as_scanning()
    {
        // Same gap in the other direction: the synchronous route reserves but opens no job, and
        // reporting it as idle would let get_server_status contradict what the disk is doing.
        var catalogId = AddCatalog();
        _queue.TryReserve(catalogId);

        Assert.True(Assert.Single(await _coordinator.ListStateAsync(CancellationToken.None)).Scanning);
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

    private void MarkScanned(Guid catalogId, DateTimeOffset at)
    {
        var catalog = _context.Catalogs.Single(entry => entry.Id == catalogId);
        catalog.LastScannedAt = at;
        _context.SaveChanges();
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
