using System.Security.Cryptography;
using Imposter.Abstractions;
using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.IO;
using MediaServer.Api.Metadata;
using MediaServer.Api.Pipeline;
using MediaServer.Api.Organizer;
using MediaServer.Api.Torrents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

[assembly: GenerateImposter(typeof(IFilesystemInspector))]
[assembly: GenerateImposter(typeof(ITorrentEngine))]
[assembly: GenerateImposter(typeof(IPlacementOutput))]

namespace MediaServer.Api.Tests.Pipeline;

public sealed class RetainedDownloadTests
{
    private static void Match(PipelineTestHarness harness) => harness.MetadataProvider.OnSearch = query =>
        [new MetadataCandidate(new ProviderRef("tmdb", "27205"), query.Title, query.Year, 1)];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Capacity_warning_survives_redrive_and_recovers_by_retry_or_move(bool keepSeed)
    {
        long available = 0;
        var filesystem = IFilesystemInspector.Imposter();
        filesystem.GetAvailableFreeBytes(Arg<string>.Any()).Returns((string _) => available);
        using var harness = new PipelineTestHarness(services => services.AddSingleton(filesystem.Instance()));
        Match(harness);
        var (ingestId, _, downloadId) = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Inception.2010", "Inception.2010.mkv", keepSeeding: true);
        var probes = 0;
        var defaultProbe = harness.MediaProbe.OnProbe;
        harness.MediaProbe.OnProbe = path => { probes++; return defaultProbe(path); };
        await harness.Orchestrator.DriveAsync(ingestId, default);
        await harness.Orchestrator.DriveAsync(ingestId, default);
        using (var scope = harness.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var item = await db.IngestItems.SingleAsync();
            Assert.Equal(IngestStatus.AwaitingSpace, item.Status);
            Assert.Equal(IngestStage.Organize, item.Stage);
            Assert.Equal(0, item.AttemptCount);
            Assert.Equal(0, probes);
            Assert.Null(item.NextAttemptAt);
            Assert.True((await db.Downloads.SingleAsync()).KeepSeeding);
            Assert.Empty(await db.MediaSources.ToListAsync());
            if (keepSeed)
            {
                available = 1_000_000;
                await scope.ServiceProvider.GetRequiredService<IngestService>().RetryAsync(ingestId, default);
            }
            else
                await scope.ServiceProvider.GetRequiredService<TorrentService>().StopSeedingAsync(downloadId, default);
        }
        await harness.Orchestrator.DriveAsync(ingestId, default);
        using var verify = harness.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var final = await verifyDb.IngestItems.SingleAsync();
        Assert.True(final.Status == IngestStatus.Done, final.LastError ?? final.Status.ToString());
        Assert.Equal(keepSeed, await verifyDb.Downloads.AnyAsync());
        Assert.Single(await verifyDb.MediaSources.ToListAsync());
        Assert.Equal(1, probes);
    }

    [Fact]
    public async Task Published_seed_preserves_complete_original_tree_and_history_until_explicit_stop()
    {
        using var harness = new PipelineTestHarness();
        Match(harness);
        var (ingestId, catalogId, downloadId) = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Inception.2010", "Inception.2010.mkv", keepSeeding: true,
            additionalSourceRelativePaths: ["Inception.2010.eng.srt"]);
        string root;
        using (var scope = harness.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            root = (await db.Catalogs.SingleAsync()).Root;
            await File.WriteAllTextAsync(Path.Combine(CatalogPaths.For(root).IncomingFor(downloadId), "release.nfo"), "retained torrent extra");
        }
        using (var engineHandle = new FileStream(Path.Combine(CatalogPaths.For(root).IncomingFor(downloadId), "Inception.2010.mkv"),
                   FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            await harness.Orchestrator.DriveAsync(ingestId, default);
        }
        using (var scope = harness.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            Assert.Equal(IngestStatus.Done, (await db.IngestItems.SingleAsync()).Status);
            foreach (var file in await db.SourceFiles.ToListAsync())
            {
                Assert.NotNull(file.OriginalRelativePath);
                var original = Path.Combine(root, file.OriginalRelativePath!);
                var copy = Path.Combine(root, file.RelativePath);
                Assert.NotEqual(original, copy);
                Assert.Equal(SHA256.HashData(await File.ReadAllBytesAsync(original)), SHA256.HashData(await File.ReadAllBytesAsync(copy)));
            }
            var canonical = Path.Combine(root, (await db.MediaSources.SingleAsync()).Path);
            await File.WriteAllTextAsync(canonical, "independently editable library file");
            Assert.Equal(1024, new FileInfo(Path.Combine(CatalogPaths.For(root).IncomingFor(downloadId), "Inception.2010.mkv")).Length);
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<IngestService>().DeleteCompletedAsync(default));
            await Assert.ThrowsAsync<TorrentRequestException>(() => scope.ServiceProvider.GetRequiredService<IngestService>().DeleteAsync(ingestId, default));
            await scope.ServiceProvider.GetRequiredService<TorrentService>().StopSeedingAsync(downloadId, default);
            Assert.True(File.Exists(canonical));
        }
        using var verify = harness.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        Assert.False(Directory.Exists(CatalogPaths.For(root).IncomingFor(downloadId)));
        Assert.Empty(await verifyDb.Downloads.ToListAsync());
        var final = await verifyDb.IngestItems.SingleAsync();
        Assert.True(final.Status == IngestStatus.Done, final.LastError ?? final.Status.ToString());
        Assert.NotNull((await verifyDb.MediaItems.SingleAsync()).PublicId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failed_engine_release_preserves_source_and_owner_before_move_or_cancel(bool cancel)
    {
        var engine = ITorrentEngine.Imposter();
        engine.GetSnapshot(Arg<string>.Any()).Returns((string hash) => new TorrentSnapshot(hash, "Fixture", "Seeding", true, 100, 0, 0, 0, 0, 1024));
        engine.StopAsync(Arg<string>.Any(), Arg<CancellationToken>.Any()).Returns(Task.CompletedTask);
        engine.RemoveAsync(Arg<string>.Any(), Arg<bool>.Any(), Arg<CancellationToken>.Any())
            .Returns((string hash, bool files, CancellationToken ct) => Task.FromException(new IOException("Lost engine reply")));
        using var harness = new PipelineTestHarness(services => services.AddSingleton(engine.Instance()));
        Match(harness);
        var (ingestId, _, downloadId) = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Inception.2010", "Inception.2010.mkv");
        if (cancel)
        {
            using var scope = harness.CreateScope();
            await Assert.ThrowsAsync<TorrentRequestException>(() => scope.ServiceProvider.GetRequiredService<DownloadDeletionService>().DeleteAsync(downloadId, true, default));
        }
        else await harness.Orchestrator.DriveAsync(ingestId, default);
        using var verify = harness.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var download = await db.Downloads.SingleAsync();
        Assert.True(download.StopRequested);
        Assert.False(download.EngineReleased);
        Assert.NotNull(download.RetentionError);
        Assert.True(File.Exists(Path.Combine(download.SavePath, "Inception.2010.mkv")));
        Assert.Single(await db.IngestItems.ToListAsync());
        Assert.Empty(await db.MediaSources.ToListAsync());
    }

    [Fact]
    public async Task Policy_updates_stop_at_placement_and_unknown_roots_are_report_only()
    {
        using var harness = new PipelineTestHarness();
        Match(harness);
        var (ingestId, _, downloadId) = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Inception.2010", "Inception.2010.mkv");
        using (var scope = harness.CreateScope())
            await scope.ServiceProvider.GetRequiredService<TorrentService>().SetSeedingPolicyAsync(downloadId, true, default);
        await harness.Orchestrator.DriveAsync(ingestId, default);
        using var verify = harness.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var service = verify.ServiceProvider.GetRequiredService<TorrentService>();
        await Assert.ThrowsAsync<TorrentRequestException>(() => service.SetSeedingPolicyAsync(downloadId, false, default));
        var catalog = await db.Catalogs.SingleAsync();
        var unknown = Path.Combine(catalog.Root, ".incoming", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unknown);
        await File.WriteAllTextAsync(Path.Combine(unknown, "important.mkv"), "unknown owner");
        var temporary = new TemporaryDownloadService(db, verify.ServiceProvider.GetRequiredService<DownloadRetentionService>());
        var roots = await temporary.AnalyzeAsync(default);
        Assert.Equal(2, roots.Count);
        Assert.All(roots, root => Assert.False(root.CanClean));
        Assert.Single(roots, root => root.Id == null && root.State.Contains("Unknown"));
        var cleaned = await temporary.CleanAsync([downloadId], default);
        Assert.False(Assert.Single(cleaned).Cleaned);
        Assert.True(Directory.Exists(unknown));
    }
    [Theory]
    [InlineData(28)]
    [InlineData(112)]
    public async Task Disk_full_during_write_removes_partial_output_and_fallback_publishes(int code)
    {
        var output = IPlacementOutput.Imposter();
        output.Create(Arg<string>.Any()).Returns((string path) => new FailingOutput(path, code));
        using var harness = new PipelineTestHarness(services => services.AddSingleton(output.Instance()));
        Match(harness);
        var (ingestId, _, downloadId) = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Inception.2010", "Inception.2010.mkv", keepSeeding: true);
        await harness.Orchestrator.DriveAsync(ingestId, default);
        using (var scope = harness.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var item = await db.IngestItems.SingleAsync();
            Assert.Equal(IngestStatus.AwaitingSpace, item.Status);
            Assert.Empty(await db.MediaSources.ToListAsync());
            var root = (await db.Catalogs.SingleAsync()).Root;
            Assert.Empty(Directory.GetFiles(root, "*.partial", SearchOption.AllDirectories));
            Assert.Single(Directory.GetFiles(root, "*.mkv", SearchOption.AllDirectories));
            // Simulate interrupted partial-output cleanup before the explicit Move fallback.
            var reserved = await db.SourceFiles.SingleAsync();
            var partial = Path.Combine(root, reserved.PlacementPath!) + $".ingest-{reserved.Id:N}.partial";
            await File.WriteAllTextAsync(partial, "unfinished output");
            await scope.ServiceProvider.GetRequiredService<TorrentService>().StopSeedingAsync(downloadId, default);
        }
        await harness.Orchestrator.DriveAsync(ingestId, default);
        using var verify = harness.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var final = await verifyDb.IngestItems.SingleAsync();
        Assert.True(final.Status == IngestStatus.Done, final.LastError ?? final.Status.ToString());
        Assert.Single(await verifyDb.MediaSources.ToListAsync());
        Assert.Empty(await verifyDb.Downloads.ToListAsync());
        Assert.Empty(Directory.GetFiles((await verifyDb.Catalogs.SingleAsync()).Root, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Partial_success_then_move_does_not_duplicate_versions()
    {
        var calls = 0;
        var filesystem = IFilesystemInspector.Imposter();
        filesystem.GetAvailableFreeBytes(Arg<string>.Any()).Returns((string _) => ++calls >= 3 ? 0L : 1_000_000L);
        using var harness = new PipelineTestHarness(services => services.AddSingleton(filesystem.Instance()));
        Match(harness);
        var (ingestId, _, downloadId) = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Inception.2010", "Inception.2010.HDR.mkv", keepSeeding: true,
            additionalSourceRelativePaths: ["Inception.2010.SDR.mkv"]);
        await harness.Orchestrator.DriveAsync(ingestId, default);
        string placed;
        using (var scope = harness.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            Assert.Equal(IngestStatus.AwaitingSpace, (await db.IngestItems.SingleAsync()).Status);
            placed = (await db.SourceFiles.ToListAsync()).Single(x => !CatalogPaths.IsIncoming(x.RelativePath)).RelativePath;
            await scope.ServiceProvider.GetRequiredService<TorrentService>().StopSeedingAsync(downloadId, default);
        }
        await harness.Orchestrator.DriveAsync(ingestId, default);
        using var verify = harness.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var final = await verifyDb.IngestItems.SingleAsync();
        Assert.True(final.Status == IngestStatus.Done, final.LastError ?? final.Status.ToString());
        var sources = await verifyDb.MediaSources.ToListAsync();
        Assert.Equal(2, sources.Count);
        Assert.Contains(sources, x => x.Path == placed);
        var root = (await verifyDb.Catalogs.SingleAsync()).Root;
        Assert.Equal(2, Directory.GetFiles(root, "*.mkv", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task Lost_remove_reply_is_reconciled_without_readding_or_moving_early()
    {
        var removed = false;
        var engine = ITorrentEngine.Imposter();
        engine.GetSnapshot(Arg<string>.Any()).Returns((string hash) => removed ? null! : new TorrentSnapshot(hash, "Fixture", "Seeding", true, 100, 0, 0, 0, 0, 1024));
        engine.StopAsync(Arg<string>.Any(), Arg<CancellationToken>.Any()).Returns((string hash, CancellationToken ct) => removed
            ? Task.FromException(new HttpRequestException("Already removed", null, System.Net.HttpStatusCode.NotFound)) : Task.CompletedTask);
        engine.RemoveAsync(Arg<string>.Any(), Arg<bool>.Any(), Arg<CancellationToken>.Any()).Returns((string hash, bool files, CancellationToken ct) =>
        {
            Assert.False(files);
            if (removed) return Task.CompletedTask;
            removed = true;
            return Task.FromException(new IOException("Reply lost"));
        });
        using var harness = new PipelineTestHarness(services => services.AddSingleton(engine.Instance()));
        Match(harness);
        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Inception.2010", "Inception.2010.mkv");
        await harness.Orchestrator.DriveAsync(ingestId, default);
        using (var scope = harness.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            Assert.Empty(await db.MediaSources.ToListAsync());
            Assert.True((await db.Downloads.SingleAsync()).StopRequested);
            await scope.ServiceProvider.GetRequiredService<IngestService>().RetryAsync(ingestId, default);
        }
        await harness.Orchestrator.DriveAsync(ingestId, default);
        using var verify = harness.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var final = await verifyDb.IngestItems.SingleAsync();
        Assert.True(final.Status == IngestStatus.Done, final.LastError ?? final.Status.ToString());
        Assert.Empty(await verifyDb.Downloads.ToListAsync());
    }

    [Fact]
    public async Task Restart_waits_for_engine_recheck_and_metadata_does_not_duplicate_placed_files()
    {
        var complete = false;
        var engine = ITorrentEngine.Imposter();
        engine.GetSnapshot(Arg<string>.Any()).Returns((string hash) => new TorrentSnapshot(hash, "Fixture", "Hashing", complete, complete ? 100 : 0, 0, 0, 0, 0, 1024));
        using var harness = new PipelineTestHarness(services => services.AddSingleton(engine.Instance()));
        Match(harness);
        var (ingestId, _, downloadId) = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Inception.2010", "Inception.2010.mkv", keepSeeding: true);
        await harness.Orchestrator.DriveAsync(ingestId, default);
        using (var scope = harness.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            Assert.Equal(IngestStage.Download, (await db.IngestItems.SingleAsync()).Stage);
            Assert.Empty(await db.MediaSources.ToListAsync());
        }
        complete = true;
        await harness.Orchestrator.DriveAsync(ingestId, default);
        using var verify = harness.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var source = await verifyDb.SourceFiles.SingleAsync();
        var path = source.RelativePath;
        await new DownloadFileService(verifyDb).UpsertSourceFilesAsync(downloadId, [new TorrentFileInfo(0, "Inception.2010.mkv", 1024)], default);
        Assert.Single(await verifyDb.SourceFiles.ToListAsync());
        Assert.Equal(path, (await verifyDb.SourceFiles.SingleAsync()).RelativePath);
        Assert.False(CatalogPaths.IsIncoming(path));
    }

    [Fact]
    public async Task Cleanup_failure_retains_owner_and_explicit_retry_revalidates_paths()
    {
        using var harness = new PipelineTestHarness();
        Match(harness);
        var (ingestId, _, downloadId) = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Inception.2010", "Inception.2010.mkv", keepSeeding: true);
        await harness.Orchestrator.DriveAsync(ingestId, default);
        string owned;
        using (var scope = harness.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var download = await db.Downloads.SingleAsync();
            owned = download.SavePath;
            download.SavePath = Path.Combine(harness.Root, "unrelated");
            Directory.CreateDirectory(download.SavePath);
            await File.WriteAllTextAsync(Path.Combine(download.SavePath, "keep.txt"), "unrelated data");
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<TorrentService>().StopSeedingAsync(downloadId, default);
        }
        using (var scope = harness.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var download = await db.Downloads.SingleAsync();
            Assert.True(download.EngineReleased);
            Assert.Equal(1, download.CleanupAttempts);
            Assert.NotNull(download.RetentionError);
            Assert.True(Directory.Exists(owned));
            Assert.True(File.Exists(Path.Combine(harness.Root, "unrelated", "keep.txt")));
            download.SavePath = owned;
            await db.SaveChangesAsync();
            var service = new TemporaryDownloadService(db, scope.ServiceProvider.GetRequiredService<DownloadRetentionService>());
            Assert.True(Assert.Single(await service.AnalyzeAsync(default)).CanClean);
            Assert.True(Assert.Single(await service.CleanAsync([downloadId], default)).Cleaned);
            Assert.NotNull((await db.MediaItems.SingleAsync()).PublicId);
        }
        Assert.False(Directory.Exists(owned));
        Assert.True(File.Exists(Path.Combine(harness.Root, "unrelated", "keep.txt")));
    }

    [Fact]
    public async Task Cancellation_discards_private_output_and_retry_keeps_the_original()
    {
        using var cancelled = new CancellationTokenSource();
        var interrupt = true;
        var output = IPlacementOutput.Imposter();
        output.Create(Arg<string>.Any()).Returns((string path) =>
        {
            var stream = File.Create(path);
            if (interrupt)
            {
                stream.Write(new byte[16]);
                cancelled.Cancel();
            }
            return stream;
        });
        using var harness = new PipelineTestHarness(services => services.AddSingleton(output.Instance()));
        Match(harness);
        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Inception.2010", "Inception.2010.mkv", keepSeeding: true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Orchestrator.DriveAsync(ingestId, cancelled.Token));
        using (var scope = harness.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var root = (await db.Catalogs.SingleAsync()).Root;
            Assert.Empty(Directory.GetFiles(root, "*.partial", SearchOption.AllDirectories));
            Assert.Single(Directory.GetFiles(root, "*.mkv", SearchOption.AllDirectories));
            Assert.Empty(await db.MediaSources.ToListAsync());
            await scope.ServiceProvider.GetRequiredService<IngestService>().RetryAsync(ingestId, default);
        }
        interrupt = false;
        await harness.Orchestrator.DriveAsync(ingestId, default);
        using var verify = harness.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        Assert.Equal(IngestStatus.Done, (await verifyDb.IngestItems.SingleAsync()).Status);
        Assert.True((await verifyDb.Downloads.SingleAsync()).KeepSeeding);
        Assert.Single(await verifyDb.MediaSources.ToListAsync());
    }

    [Fact]
    public async Task Cancelling_a_partially_placed_import_preserves_outputs_and_ownership()
    {
        var calls = 0;
        var filesystem = IFilesystemInspector.Imposter();
        filesystem.GetAvailableFreeBytes(Arg<string>.Any()).Returns((string _) => ++calls >= 3 ? 0L : 1_000_000L);
        using var harness = new PipelineTestHarness(services => services.AddSingleton(filesystem.Instance()));
        Match(harness);
        var (ingestId, _, downloadId) = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Inception.2010", "Inception.2010.HDR.mkv", keepSeeding: true,
            additionalSourceRelativePaths: ["Inception.2010.SDR.mkv"]);
        await harness.Orchestrator.DriveAsync(ingestId, default);
        using var scope = harness.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        Assert.Equal(IngestStatus.AwaitingSpace, (await db.IngestItems.SingleAsync()).Status);
        var files = await db.SourceFiles.ToListAsync();
        var placed = Assert.Single(files, x => !CatalogPaths.IsIncoming(x.RelativePath));
        var root = (await db.Catalogs.SingleAsync()).Root;
        var bytes = await File.ReadAllBytesAsync(Path.Combine(root, placed.RelativePath));
        await Assert.ThrowsAsync<TorrentRequestException>(() => scope.ServiceProvider.GetRequiredService<DownloadDeletionService>().DeleteAsync(downloadId, true, default));
        await Assert.ThrowsAsync<TorrentRequestException>(() => scope.ServiceProvider.GetRequiredService<IngestService>().DeleteAsync(ingestId, default));
        // The maintenance/worker path cannot bypass the same protection.
        var download = await db.Downloads.SingleAsync();
        await Assert.ThrowsAsync<TorrentRequestException>(() => scope.ServiceProvider.GetRequiredService<DownloadRetentionService>().CleanupAsync(download, true, default));
        Assert.False(download.CancellationRequested);
        Assert.False(download.StopRequested);
        Assert.False(download.CleanupRequested);
        Assert.Equal(2, await db.SourceFiles.CountAsync());
        Assert.Single(await db.IngestItems.ToListAsync());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(root, placed.RelativePath)));
        Assert.All(files, file => Assert.True(File.Exists(Path.Combine(root, file.OriginalRelativePath!))));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sidecar_capacity_recovery_preserves_completed_video_stages(bool keepSeed)
    {
        var fail = true;
        var output = IPlacementOutput.Imposter();
        output.Create(Arg<string>.Any()).Returns((string path) => fail && path.Contains(".srt.ingest-")
            ? new FailingOutput(path, 28) : File.Create(path));
        using var harness = new PipelineTestHarness(services => services.AddSingleton(output.Instance()));
        Match(harness);
        var (ingestId, _, downloadId) = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Inception.2010", "Inception.2010.mkv", keepSeeding: true,
            additionalSourceRelativePaths: ["Inception.2010.eng.srt"]);
        var videoProbes = 0;
        var defaultProbe = harness.MediaProbe.OnProbe;
        harness.MediaProbe.OnProbe = path => { if (path.EndsWith(".mkv")) videoProbes++; return defaultProbe(path); };
        await harness.Orchestrator.DriveAsync(ingestId, default);
        using (var scope = harness.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var item = await db.IngestItems.SingleAsync();
            Assert.Equal(IngestStatus.AwaitingSpace, item.Status);
            Assert.Equal(IngestStage.Probe, item.Stage);
            Assert.Contains("organize", item.StagesCompleted);
            Assert.Contains("probe", item.StagesCompleted);
            Assert.DoesNotContain("sidecar", item.StagesCompleted);
            Assert.Single(await db.MediaSources.ToListAsync());
            Assert.Empty(await db.MediaStreams.Where(x => x.IsExternal).ToListAsync());
            if (keepSeed) await scope.ServiceProvider.GetRequiredService<IngestService>().RetryAsync(ingestId, default);
            else await scope.ServiceProvider.GetRequiredService<TorrentService>().StopSeedingAsync(downloadId, default);
        }
        fail = false;
        await harness.Orchestrator.DriveAsync(ingestId, default);
        using var verify = harness.CreateScope();
        var finalDb = verify.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        Assert.Equal(IngestStatus.Done, (await finalDb.IngestItems.SingleAsync()).Status);
        Assert.Single(await finalDb.MediaSources.ToListAsync());
        Assert.Single(await finalDb.MediaStreams.Where(x => x.IsExternal).ToListAsync());
        Assert.Equal(1, videoProbes);
    }

    [Fact]
    public async Task Occupied_unowned_sidecar_does_not_block_publication_or_discard_original()
    {
        using var harness = new PipelineTestHarness();
        Match(harness);
        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Inception.2010", "Inception.2010.mkv", keepSeeding: true,
            additionalSourceRelativePaths: ["Inception.2010.eng.srt"]);
        var defaultProbe = harness.MediaProbe.OnProbe;
        string? occupied = null;
        harness.MediaProbe.OnProbe = path =>
        {
            if (path.EndsWith(".mkv"))
            {
                occupied = Path.ChangeExtension(path, "eng.srt");
                File.WriteAllText(occupied, "unrelated subtitle");
            }
            return defaultProbe(path);
        };
        await harness.Orchestrator.DriveAsync(ingestId, default);
        using var scope = harness.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        Assert.Equal(IngestStatus.Done, (await db.IngestItems.SingleAsync()).Status);
        Assert.NotNull((await db.MediaItems.SingleAsync()).PublicId);
        Assert.Equal("unrelated subtitle", await File.ReadAllTextAsync(occupied!));
        Assert.Empty(await db.MediaStreams.Where(x => x.IsExternal).ToListAsync());
        var companion = await db.SourceFiles.SingleAsync(x => x.RelativePath.EndsWith(".srt"));
        Assert.True(CatalogPaths.IsIncoming(companion.RelativePath));
        Assert.Null(companion.PlacementPath);
        var download = await db.Downloads.SingleAsync();
        await scope.ServiceProvider.GetRequiredService<TorrentService>().StopSeedingAsync(download.Id, default);
        Assert.True(Directory.Exists(download.SavePath));
        Assert.Contains("protected", download.RetentionError);
    }

    [Fact]
    public async Task One_ingest_gate_does_not_block_other_downloads_or_analysis()
    {
        using var harness = new PipelineTestHarness();
        var (ingestId, _, downloadId) = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "A", "A.mkv");
        var (_, _, otherId) = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "B", "B.mkv");
        using var placementScope = harness.CreateScope();
        using var gate = await IngestMutationGate.EnterIngestAsync(placementScope.ServiceProvider.GetRequiredService<MediaServerDbContext>(), ingestId, default);
        using var blockedScope = harness.CreateScope();
        var blocked = blockedScope.ServiceProvider.GetRequiredService<TorrentService>().PauseAsync(downloadId, default);
        Assert.False(blocked.IsCompleted);
        using var independentScope = harness.CreateScope();
        Assert.True(await independentScope.ServiceProvider.GetRequiredService<TorrentService>().PauseAsync(otherId, default).WaitAsync(TimeSpan.FromSeconds(5)));
        var db = independentScope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var analysis = new TemporaryDownloadService(db, independentScope.ServiceProvider.GetRequiredService<DownloadRetentionService>());
        Assert.Equal(2, (await analysis.AnalyzeAsync(default).WaitAsync(TimeSpan.FromSeconds(5))).Count);
        gate.Dispose();
        Assert.True(await blocked.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Cancelled_gate_waiter_does_not_release_another_owners_lock()
    {
        var id = Guid.NewGuid();
        using var held = await IngestMutationGate.EnterAsync(id, default);
        using var cancel = new CancellationTokenSource();
        var cancelled = IngestMutationGate.EnterAsync(id, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var next = IngestMutationGate.EnterAsync(id, default);
        Assert.False(next.IsCompleted);
        held.Dispose();
        using var acquired = await next.WaitAsync(TimeSpan.FromSeconds(5));
        held.Dispose(); // A lease is idempotent; it must not release the new owner's lock.
        var last = IngestMutationGate.EnterAsync(id, default);
        Assert.False(last.IsCompleted);
        acquired.Dispose();
        using var final = await last.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class FailingOutput : Stream
    {
        private readonly FileStream file;
        private readonly int code;
        public FailingOutput(string path, int code) { file = File.Create(path); this.code = code; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => file.Length;
        public override long Position { get => file.Position; set => throw new NotSupportedException(); }
        public override void Flush() => file.Flush();
        public override void Write(byte[] buffer, int offset, int count)
        {
            file.Write(buffer, offset, Math.Min(count, 16));
            throw new IOException("Injected disk full", code);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            file.Write(buffer.Span[..Math.Min(buffer.Length, 16)]);
            return ValueTask.FromException(new IOException("Injected disk full", code));
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) file.Dispose(); base.Dispose(disposing); }
    }

}
