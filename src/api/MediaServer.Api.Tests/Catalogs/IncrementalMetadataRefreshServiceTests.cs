using MediaServer.Api.Catalogs;
using MediaServer.Api.Collections;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Metadata;
using MediaServer.Api.People;
using MediaServer.Api.Pipeline;
using MediaServer.Api.Probe;
using MediaServer.Api.Tests.Jellyfin;
using MediaServer.Api.Tests.Pipeline;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Catalogs;

/// <summary>
/// The nightly refresh that follows the provider's change list instead of re-enriching the library. Most
/// of what matters here is the marker: it is what decides whether a night is a retry, a step forward, or
/// a gap nobody will ever look at again.
/// </summary>
public sealed class IncrementalMetadataRefreshServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly TestTimeProvider _time = new(DateTimeOffset.Parse("2026-08-31T03:00:00Z"));
    private readonly FakeMetadataProvider _metadata = new();
    private readonly FakeChangeFeed _feed = new();

    public IncrementalMetadataRefreshServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();

        _database.Catalogs.Add(new Catalog
        {
            Id = CatalogId, Name = "Movies", Type = CatalogType.Movie, Root = "/m",
            CreatedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow(),
        });
        _database.SaveChanges();
    }

    private static readonly Guid CatalogId = Guid.NewGuid();

    private IncrementalMetadataRefreshService Service() => new(
        _database,
        _feed,
        new LibraryMaintenanceService(
            _database,
            new CatalogPathSandbox(),
            new FakeMediaProbe(),
            new EnrichService(_database, _metadata, new MediaServerSettings { SupportedLanguages = ["en-US"] },
                new PersonSyncService(_database), new CollectionSyncService(_database)),
            NullLogger<LibraryMaintenanceService>.Instance),
        _time,
        NullLogger<IncrementalMetadataRefreshService>.Instance);

    [Fact]
    public async Task The_first_night_starts_watching_rather_than_reaching_backwards()
    {
        // There is nothing to catch up on — the library was enriched as it was imported — and a window
        // invented here would refresh titles on no evidence at all.
        SeedMovie("27205");

        var report = await Service().RunAsync(CancellationToken.None);

        Assert.True(report.Skipped);
        Assert.Empty(_feed.Queries);
        Assert.Equal(_time.GetUtcNow(), await MarkerAsync());
    }

    [Fact]
    public async Task It_refreshes_only_the_library_titles_the_provider_changed()
    {
        SeedMovie("27205");
        SeedMovie("155");
        await SetMarkerAsync(_time.GetUtcNow().AddDays(-1));
        _feed.Changed[MediaKind.Movie] = ["27205", "999999"]; // one of ours, one held by nobody here

        var report = await Service().RunAsync(CancellationToken.None);

        Assert.False(report.Skipped);
        Assert.Equal(1, report.Changed);
        Assert.Equal(1, report.Refreshed);
        Assert.Contains("27205", _metadata.Fetched);
        Assert.DoesNotContain("155", _metadata.Fetched);
        Assert.Equal(_time.GetUtcNow(), await MarkerAsync());
    }

    [Fact]
    public async Task A_provider_that_cannot_answer_leaves_the_marker_for_the_next_night_to_retry()
    {
        // Stepping the marker over a window nobody read would turn an outage into a permanent hole.
        SeedMovie("27205");
        var marker = _time.GetUtcNow().AddDays(-1);
        await SetMarkerAsync(marker);
        _feed.Fails = true;

        var report = await Service().RunAsync(CancellationToken.None);

        Assert.True(report.Skipped);
        Assert.Equal(marker, await MarkerAsync());
    }

    [Fact]
    public async Task A_gap_beyond_the_providers_window_is_clamped_rather_than_turned_into_a_full_refresh()
    {
        // The app was off for a month. The provider cannot answer for most of that, and re-enriching the
        // whole library is the expensive pass this exists to avoid — so it asks for what is still there.
        SeedMovie("27205");
        await SetMarkerAsync(_time.GetUtcNow().AddDays(-30));

        await Service().RunAsync(CancellationToken.None);

        var query = Assert.Single(_feed.Queries, entry => entry.Kind == MediaKind.Movie);
        Assert.Equal(_time.GetUtcNow() - _feed.MaxWindow, query.Since);
        Assert.Equal(_time.GetUtcNow(), query.Until);
        Assert.Equal(_time.GetUtcNow(), await MarkerAsync());
    }

    [Fact]
    public async Task A_removed_title_is_not_refreshed()
    {
        // Provider calls spent on a ghost buy nothing: it has no page, no art to serve and no file.
        var ghost = SeedMovie("27205");
        await _database.MediaItems.Where(item => item.Id == ghost)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.PublicId, (string?)null)
                .SetProperty(item => item.RemovedAt, _time.GetUtcNow()));
        await SetMarkerAsync(_time.GetUtcNow().AddDays(-1));
        _feed.Changed[MediaKind.Movie] = ["27205"];

        var report = await Service().RunAsync(CancellationToken.None);

        Assert.Equal(0, report.Changed);
        Assert.Empty(_metadata.Fetched);
    }

    private Guid SeedMovie(string providerId)
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            PublicId = Guid.NewGuid().ToString("N"),
            CatalogId = CatalogId,
            Kind = MediaKind.Movie,
            Title = $"Movie {providerId}",
            IdentityProvider = "tmdb",
            IdentityProviderId = providerId,
            AddedAt = _time.GetUtcNow(),
            UpdatedAt = _time.GetUtcNow(),
        };
        _database.MediaItems.Add(item);
        _database.SaveChanges();
        return item.Id;
    }

    private async Task SetMarkerAsync(DateTimeOffset instant)
    {
        _database.AppSettings.Add(new AppSettings
        {
            Id = AppSettings.SingletonId,
            MetadataChangesSyncedThrough = instant,
            UpdatedAt = instant,
        });
        await _database.SaveChangesAsync();
    }

    private async Task<DateTimeOffset?> MarkerAsync()
    {
        await using var verify = new MediaServerDbContext(
            new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        return (await verify.AppSettings.SingleAsync()).MetadataChangesSyncedThrough;
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }

    private sealed class FakeChangeFeed : IMetadataChangeFeed
    {
        public Dictionary<MediaKind, IReadOnlyCollection<string>> Changed { get; } = [];

        public List<(MediaKind Kind, DateTimeOffset Since, DateTimeOffset Until)> Queries { get; } = [];

        public bool Fails { get; set; }

        public string Key => "tmdb";

        public TimeSpan MaxWindow => TimeSpan.FromDays(14);

        public Task<IReadOnlyCollection<string>?> GetChangedAsync(
            MediaKind kind, DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken)
        {
            Queries.Add((kind, since, until));
            return Task.FromResult(Fails ? null : Changed.GetValueOrDefault(kind, []));
        }
    }
}
