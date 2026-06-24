using System.Runtime.CompilerServices;
using MediaServer.Api.Catalogs;
using MediaServer.Api.Collections;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Jobs;
using MediaServer.Api.Metadata;
using MediaServer.Api.People;
using MediaServer.Api.Pipeline;
using MediaServer.Api.Realtime;
using MediaServer.Api.Tests.Pipeline;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Api.Tests.Catalogs;

public sealed class CatalogMetadataRefreshServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly RecordingNotifier _notifier = new();
    private readonly RecordingQueue _queue = new();

    public CatalogMetadataRefreshServiceTests()
    {
        // One shared in-memory database across every scope the service spins up per item.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<MediaServerDbContext>(options => options.UseSqlite(_connection));
        services.AddSingleton(new MediaServerSettings { SupportedLanguages = ["en-US"] });
        services.AddSingleton<IMetadataProvider, FakeMetadataProvider>();
        services.AddSingleton<IRealtimeNotifier>(_notifier);
        services.AddSingleton<ICatalogRefreshQueue>(_queue);
        services.AddScoped<PersonSyncService>();
        services.AddScoped<CollectionSyncService>();
        services.AddScoped<EnrichService>();
        services.AddScoped<JobService>();
        services.AddScoped<CatalogMetadataRefreshService>();
        services.AddScoped<CatalogRefreshCoordinator>();
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<MediaServerDbContext>().Database.Migrate();
    }

    [Fact]
    public async Task Run_refreshes_every_identified_item_and_reports_progress()
    {
        var catalogId = SeedCatalog(identified: 3, unidentified: 1);

        using var scope = _provider.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobService>();
        var service = scope.ServiceProvider.GetRequiredService<CatalogMetadataRefreshService>();

        var job = await jobs.StartAsync(CatalogMetadataRefreshService.JobType, "catalog", catalogId, CancellationToken.None);
        var report = await service.RunAsync(catalogId, job, CancellationToken.None);

        Assert.Equal(3, report.Total);
        Assert.Equal(3, report.Refreshed);
        Assert.Equal(0, report.Failed);
        Assert.Equal(100, job.Progress);
        Assert.Contains(RealtimeEvents.JobProgress, _notifier.Events);

        // Only the identified items were enriched (one metadata record each for the single language).
        await using var fresh = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        Assert.Equal(3, await fresh.MetadataRecords.CountAsync());
    }

    [Fact]
    public async Task Run_returns_empty_for_unknown_catalog()
    {
        using var scope = _provider.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobService>();
        var service = scope.ServiceProvider.GetRequiredService<CatalogMetadataRefreshService>();

        var job = await jobs.StartAsync(CatalogMetadataRefreshService.JobType, "catalog", Guid.NewGuid(), CancellationToken.None);
        var report = await service.RunAsync(Guid.NewGuid(), job, CancellationToken.None);

        Assert.Equal(0, report.Total);
    }

    [Fact]
    public async Task Coordinator_starts_a_job_then_rejects_a_concurrent_request()
    {
        var catalogId = SeedCatalog(identified: 1, unidentified: 0);

        using var scope = _provider.CreateScope();
        var coordinator = scope.ServiceProvider.GetRequiredService<CatalogRefreshCoordinator>();

        var first = await coordinator.RequestAsync(catalogId, CancellationToken.None);
        var second = await coordinator.RequestAsync(catalogId, CancellationToken.None);

        Assert.Equal(CatalogRefreshRequestStatus.Started, first.Status);
        Assert.NotNull(first.JobId);
        Assert.Single(_queue.Enqueued);
        Assert.Equal(catalogId, _queue.Enqueued[0].CatalogId);

        // A run is already in flight (a Running job row), so the second request is refused.
        Assert.Equal(CatalogRefreshRequestStatus.AlreadyRunning, second.Status);
    }

    [Fact]
    public async Task Coordinator_rejects_unknown_catalog()
    {
        using var scope = _provider.CreateScope();
        var coordinator = scope.ServiceProvider.GetRequiredService<CatalogRefreshCoordinator>();

        var result = await coordinator.RequestAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(CatalogRefreshRequestStatus.NotFound, result.Status);
        Assert.Empty(_queue.Enqueued);
    }

    private Guid SeedCatalog(int identified, int unidentified)
    {
        using var scope = _provider.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();

        var catalog = new Catalog
        {
            Id = Guid.NewGuid(),
            Name = "Movies",
            Type = CatalogType.Movie,
            Root = Path.Combine(Path.GetTempPath(), "ms-refresh-" + Guid.NewGuid().ToString("N")),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        database.Catalogs.Add(catalog);

        for (var index = 0; index < identified + unidentified; index++)
        {
            var isIdentified = index < identified;
            database.MediaItems.Add(new MediaItem
            {
                Id = Guid.NewGuid(),
                CatalogId = catalog.Id,
                Kind = MediaKind.Movie,
                Title = $"Movie {index}",
                LibraryPath = $"library/Movie {index}/movie.mkv",
                IdentityProvider = isIdentified ? "tmdb" : null,
                IdentityProviderId = isIdentified ? (100 + index).ToString() : null,
                AddedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        database.SaveChanges();
        return catalog.Id;
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    /// <summary>Captures the realtime event names broadcast during a run.</summary>
    private sealed class RecordingNotifier : IRealtimeNotifier
    {
        public List<string> Events { get; } = [];
        public Task DownloadProgressAsync(DownloadProgress progress, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DownloadStateChangedAsync(DownloadStateChanged change, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task IngestStageChangedAsync(IngestStageChanged change, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task VpnStatusChangedAsync(VpnStatusChanged status, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task JobChangedAsync(string eventName, JobEvent job, CancellationToken cancellationToken = default)
        {
            Events.Add(eventName);
            return Task.CompletedTask;
        }
    }

    /// <summary>Records what the coordinator enqueues without running a worker.</summary>
    private sealed class RecordingQueue : ICatalogRefreshQueue
    {
        public List<CatalogRefreshRequest> Enqueued { get; } = [];
        public void Enqueue(CatalogRefreshRequest request) => Enqueued.Add(request);

        public async IAsyncEnumerable<CatalogRefreshRequest> DequeueAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
