using MediaServer.Api.Catalogs;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.IO;
using MediaServer.Api.Jobs;
using MediaServer.Api.Metadata;
using MediaServer.Api.Organizer;
using MediaServer.Api.Pipeline;
using MediaServer.Api.Pipeline.Stages;
using MediaServer.Api.Probe;
using MediaServer.Api.Realtime;
using MediaServer.Api.Torrents;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Api.Tests.Pipeline;

/// <summary>
/// Composes the ingest pipeline against a real (in-memory SQLite) database and a real organizer, with
/// the external boundaries (torrent engine, metadata provider, ffprobe, realtime) faked. Mirrors the
/// production DI wiring closely enough to exercise the orchestrator end-to-end.
/// </summary>
public sealed class PipelineTestHarness : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public PipelineTestHarness()
    {
        Root = Path.Combine(Path.GetTempPath(), "ms-pipeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        MetadataProvider = new FakeMetadataProvider();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<MediaServerDbContext>(options => options.UseSqlite(_connection));

        services.AddSingleton(new MediaServerSettings { SupportedLanguages = ["en-US"] });
        services.AddSingleton<IFilesystemInspector, FilesystemInspector>();
        services.AddSingleton<ICatalogPathSandbox, CatalogPathSandbox>();
        services.AddSingleton<INameParser, NameParser>();
        services.AddSingleton<IMetadataProvider>(MetadataProvider);
        services.AddSingleton<IMediaProbe, FakeMediaProbe>();
        services.AddSingleton<ITorrentEngine, FakeTorrentEngine>();
        services.AddSingleton<IRealtimeNotifier, NullRealtimeNotifier>();
        services.AddSingleton<IPipelineQueue, PipelineQueue>();

        services.AddScoped<IOrganizer, OrganizerService>();
        services.AddScoped<IdentifyService>();
        services.AddScoped<EnrichService>();
        services.AddScoped<JobService>();
        services.AddScoped<DownloadDeletionService>();
        services.AddScoped<IngestService>();
        services.AddScoped<IPipelineStage, IntakeStage>();
        services.AddScoped<IPipelineStage, DownloadStage>();
        services.AddScoped<IPipelineStage, IdentifyStage>();
        services.AddScoped<IPipelineStage, OrganizeStage>();
        services.AddScoped<IPipelineStage, ProbeStage>();
        services.AddScoped<IPipelineStage, EnrichStage>();
        services.AddScoped<IPipelineStage, PublishStage>();
        services.AddSingleton<IngestOrchestrator>();

        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<MediaServerDbContext>().Database.Migrate();
    }

    public string Root { get; }

    public FakeMetadataProvider MetadataProvider { get; }

    public IngestOrchestrator Orchestrator => _provider.GetRequiredService<IngestOrchestrator>();

    public IServiceScope CreateScope() => _provider.CreateScope();

    public T GetService<T>() where T : notnull => _provider.GetRequiredService<T>();

    /// <summary>Sets up a catalog, a completed download whose file sits under <c>.incoming/&lt;id&gt;/</c>,
    /// the owning source file, and a pending ingest item. Returns the ingest item id. Pass
    /// <paramref name="additionalSourceRelativePaths"/> to stage extra files under the same download (a
    /// multi-file torrent, e.g. two cuts of one episode).</summary>
    public async Task<(Guid IngestId, Guid CatalogId, Guid DownloadId)> SeedCompletedDownloadAsync(
        CatalogType type, string torrentName, string sourceRelativePath, Guid? catalogId = null, bool keepSeeding = false,
        IReadOnlyList<string>? additionalSourceRelativePaths = null)
    {
        using var scope = _provider.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var now = DateTimeOffset.UtcNow;

        var catalog = catalogId is { } existingId
            ? await database.Catalogs.SingleAsync(item => item.Id == existingId)
            : new Catalog { Id = Guid.NewGuid(), Name = "Test", Type = type, Root = Path.Combine(Root, "catalog-" + Guid.NewGuid().ToString("N")), CreatedAt = now, UpdatedAt = now };

        var paths = CatalogPaths.For(catalog.Root);
        paths.EnsureCreated();

        var downloadId = Guid.NewGuid();
        var ingestId = Guid.NewGuid();

        // Write a fake media file under the download's .incoming/<downloadId>/ staging folder.
        var stagingRelative = $"{CatalogPaths.IncomingRelative(downloadId)}/{sourceRelativePath}";
        var absolute = Path.Combine(catalog.Root, stagingRelative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await File.WriteAllBytesAsync(absolute, new byte[1024]);

        var download = new Download
        {
            Id = downloadId,
            InfoHash = Guid.NewGuid().ToString("N"),
            Name = torrentName,
            CatalogId = catalog.Id,
            SourceType = TorrentSourceType.Magnet,
            // keepSeeding parks the ingest at the download stage; otherwise it hands off into identify.
            State = keepSeeding ? DownloadState.Seeding : DownloadState.Completed,
            KeepSeeding = keepSeeding,
            SavePath = paths.IncomingFor(downloadId),
            AddedAt = now,
            CompletedAt = now,
        };
        var sourceFile = new SourceFile
        {
            Id = Guid.NewGuid(),
            IngestItemId = ingestId,
            DownloadId = downloadId,
            RelativePath = stagingRelative,
            TorrentFileIndex = 0,
            SizeBytes = 1024,
            AssignmentStatus = SourceFileAssignmentStatus.Unassigned,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // Extra files in the same torrent (each staged under the same .incoming/<downloadId>/ folder).
        var additionalFiles = new List<SourceFile>();
        foreach (var (relative, index) in (additionalSourceRelativePaths ?? []).Select((path, index) => (path, index)))
        {
            var extraStagingRelative = $"{CatalogPaths.IncomingRelative(downloadId)}/{relative}";
            var extraAbsolute = Path.Combine(catalog.Root, extraStagingRelative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(extraAbsolute)!);
            await File.WriteAllBytesAsync(extraAbsolute, new byte[1024]);

            additionalFiles.Add(new SourceFile
            {
                Id = Guid.NewGuid(),
                IngestItemId = ingestId,
                DownloadId = downloadId,
                RelativePath = extraStagingRelative,
                TorrentFileIndex = index + 1,
                SizeBytes = 1024,
                AssignmentStatus = SourceFileAssignmentStatus.Unassigned,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        var ingest = new IngestItem
        {
            Id = ingestId,
            CatalogId = catalog.Id,
            DownloadId = downloadId,
            Stage = IngestStage.Intake,
            Status = IngestStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };

        if (catalogId is null)
        {
            database.Catalogs.Add(catalog);
        }

        database.Downloads.Add(download);
        database.SourceFiles.Add(sourceFile);
        database.SourceFiles.AddRange(additionalFiles);
        database.IngestItems.Add(ingest);
        await database.SaveChangesAsync();

        return (ingest.Id, catalog.Id, download.Id);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
