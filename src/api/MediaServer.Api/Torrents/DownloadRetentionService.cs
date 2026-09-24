using MediaServer.Api.Catalogs;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.Pipeline;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Torrents;

/// <summary>Owns the durable engine-release and staging-cleanup lifecycle. Caller holds the ingest gate.</summary>
public sealed class DownloadRetentionService(
    MediaServerDbContext database, ITorrentEngine engine, ICatalogPathSandbox sandbox, HostyOptions hosty)
{
    public async Task ReleaseAsync(Download download, CancellationToken ct)
    {
        download.StopRequested = true;
        download.KeepSeeding = false;
        await database.SaveChangesAsync(ct); // A restart must not re-add after an uncertain stop reply.
        if (download.EngineReleased) return;
        try
        {
            if (engine is DisabledTorrentEngine) throw new IOException("Torrent Engine is unavailable; its release cannot be confirmed.");
            // Remove with retained files is the authoritative acknowledgement. It is idempotent on 404.
            // Stop may itself return 404 after a lost successful Remove reply; Remove still reconciles it.
            try { await engine.StopAsync(download.InfoHash, ct); }
            catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { }
            await engine.RemoveAsync(download.InfoHash, deleteFiles: false, ct);
            download.EngineReleased = true;
            download.State = DownloadState.StoppedSeeding;
            download.RetentionError = null;
            await database.SaveChangesAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            download.RetentionError = $"Could not release torrent; original files are retained. {e.Message}";
            await database.SaveChangesAsync(ct);
            throw;
        }
    }

    public async Task<bool> CleanupAsync(Download download, bool cancelImport, CancellationToken ct)
    {
        if (cancelImport && (download.PlacementStarted || await database.SourceFiles.AnyAsync(
                x => x.DownloadId == download.Id && x.PlacementPath != null, ct)))
            throw new TorrentRequestException("Placement has started. Retry the import or stop seeding and continue; remove library files after publication.");
        download.CleanupRequested = true;
        download.CancellationRequested |= cancelImport;
        await database.SaveChangesAsync(ct);
        try
        {
            await ReleaseAsync(download, ct);
            var ingests = await database.IngestItems.Where(x => x.DownloadId == download.Id).ToListAsync(ct);
            var ids = ingests.Select(x => x.Id).ToArray();
            var files = await database.SourceFiles.Where(x => ids.Contains(x.IngestItemId)).ToListAsync(ct);
            if (!cancelImport && (ingests.Any(x => x.Status != IngestStatus.Done) ||
                files.Any(x => CatalogPaths.IsIncoming(x.RelativePath) && x.AssignmentStatus != SourceFileAssignmentStatus.Skipped)))
            {
                download.RetentionError = "Original files are protected until review and all required placements finish.";
                await database.SaveChangesAsync(ct);
                return false;
            }
            var catalog = await database.Catalogs.SingleAsync(x => x.Id == download.CatalogId, ct);
            var root = ResolveOwnedRoot(download, catalog);
            var prefix = CatalogPaths.IncomingRelative(download.Id) + "/";
            if (await database.SourceFiles.AnyAsync(x => !ids.Contains(x.IngestItemId) &&
                x.IngestItem!.CatalogId == catalog.Id && x.RelativePath.StartsWith(prefix), ct))
                throw new IOException("Another import still references this staging directory.");
            if (Directory.Exists(root))
            {
                RejectLinks(root);
                Directory.Delete(root, recursive: true);
            }
            // Only our exact app-owned metadata path is eligible, never an arbitrary SourceUri.
            var metadata = Path.Combine(hosty.AppDataDir, "torrents", download.InfoHash + ".torrent");
            if (download.SourceType == TorrentSourceType.File &&
                string.Equals(download.SourceUri, metadata, StringComparison.Ordinal) && File.Exists(metadata))
                File.Delete(metadata);

            foreach (var file in files) file.DownloadId = null;
            foreach (var ingest in ingests) ingest.DownloadId = null;
            if (cancelImport) database.IngestItems.RemoveRange(ingests.Where(x => x.Status != IngestStatus.Done));
            database.Downloads.Remove(download);
            await database.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            download.CleanupAttempts++;
            download.CleanupAfter = download.CleanupAttempts < 5
                ? DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, 5 * Math.Pow(2, download.CleanupAttempts))) : null;
            download.RetentionError = $"Temporary files could not be cleaned: {e.Message}";
            await database.SaveChangesAsync(ct);
            return false;
        }
    }

    public string ResolveOwnedRoot(Download download, Catalog catalog)
    {
        var relative = CatalogPaths.IncomingRelative(download.Id);
        if (!sandbox.TryResolve(catalog, relative, out var expected) ||
            !Path.GetFullPath(download.SavePath).Equals(Path.GetFullPath(expected),
                OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("The recorded download directory no longer matches its catalog-owned staging root.");
        // Validate every ancestor: a nested regular file under a symlink must not bypass containment.
        for (var path = expected; path is not null && Path.GetFullPath(path) != Path.GetFullPath(catalog.Root); path = Path.GetDirectoryName(path))
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked staging directories are protected from cleanup.");
        return expected;
    }

    private static void RejectLinks(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked staging content is protected from recursive cleanup.");
            if ((attributes & FileAttributes.Directory) != 0) RejectLinks(entry);
        }
    }
}

public sealed class DownloadCleanupWorker(IServiceScopeFactory scopes, ILogger<DownloadCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
                var service = scope.ServiceProvider.GetRequiredService<DownloadRetentionService>();
                var now = DateTimeOffset.UtcNow;
                var pending = await db.Downloads.AsNoTracking().Where(x => x.CleanupRequested && x.CleanupAttempts < 5 &&
                    (x.CleanupAfter == null || x.CleanupAfter <= now)).Select(x => x.Id).ToListAsync(stoppingToken);
                foreach (var id in pending)
                {
                    using var gate = await IngestMutationGate.EnterAsync(id, stoppingToken);
                    var download = await db.Downloads.FirstOrDefaultAsync(x => x.Id == id, stoppingToken);
                    if (download is null || !download.CleanupRequested || download.CleanupAttempts >= 5 || download.CleanupAfter > DateTimeOffset.UtcNow) continue;
                    await service.CleanupAsync(download, download.CancellationRequested, stoppingToken);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            { logger.LogWarning(e, "Temporary download cleanup failed."); }
        }
    }
}
