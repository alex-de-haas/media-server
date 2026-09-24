using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.Pipeline;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Torrents;

public sealed record TemporaryDownloadRoot(Guid? Id, Guid CatalogId, string Catalog, string Path, long? Bytes, string State, bool CanClean);
public sealed record CleanTemporaryDownloadsRequest(Guid[] Ids);
public sealed record TemporaryDownloadCleanupResult(Guid Id, bool Cleaned, string? Error);

public sealed class TemporaryDownloadService(MediaServerDbContext database, DownloadRetentionService retention)
{
    public async Task<IReadOnlyList<TemporaryDownloadRoot>> AnalyzeAsync(CancellationToken ct)
    {
        var result = new List<TemporaryDownloadRoot>();
        var downloads = await database.Downloads.AsNoTracking().ToListAsync(ct);
        foreach (var catalog in await database.Catalogs.AsNoTracking().ToListAsync(ct))
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var download in downloads.Where(x => x.CatalogId == catalog.Id))
            {
                var relative = CatalogPaths.IncomingRelative(download.Id);
                known.Add(relative.Split('/')[1]);
                try
                {
                    var root = retention.ResolveOwnedRoot(download, catalog);
                    var eligible = await EligibleAsync(download, ct);
                    var state = download.RetentionError ?? (download.KeepSeeding && !download.StopRequested ? "Retained for seeding"
                        : eligible ? "Ready for cleanup" : "Protected: download, review or import still needs originals");
                    result.Add(new(download.Id, catalog.Id, catalog.Name, relative, Size(root, ct), state, eligible));
                }
                catch (IOException e) { result.Add(new(download.Id, catalog.Id, catalog.Name, relative, null, e.Message, false)); }
                catch (UnauthorizedAccessException e) { result.Add(new(download.Id, catalog.Id, catalog.Name, relative, null, e.Message, false)); }
            }
            var incoming = Path.Combine(catalog.Root, ".incoming");
            if (!Directory.Exists(incoming) || (File.GetAttributes(incoming) & FileAttributes.ReparsePoint) != 0) continue;
            foreach (var root in Directory.EnumerateDirectories(incoming))
            {
                ct.ThrowIfCancellationRequested();
                if (known.Contains(Path.GetFileName(root))) continue;
                long? size = null;
                try { size = Size(root, ct); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                result.Add(new(null, catalog.Id, catalog.Name, $".incoming/{Path.GetFileName(root)}", size,
                    "Unknown legacy directory: ownership cannot be verified; manual inspection required", false));
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<TemporaryDownloadCleanupResult>> CleanAsync(Guid[] ids, CancellationToken ct)
    {
        var result = new List<TemporaryDownloadCleanupResult>();
        foreach (var id in ids.Distinct())
        {
            using var gate = await IngestMutationGate.EnterAsync(id, ct);
            var download = await database.Downloads.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (download is null || !await EligibleAsync(download, ct))
            {
                result.Add(new(id, false, "This directory is no longer eligible for cleanup."));
                continue;
            }
            download.CleanupAttempts = 0;
            download.CleanupAfter = null;
            var cleaned = await retention.CleanupAsync(download, download.CancellationRequested, ct);
            result.Add(new(id, cleaned, cleaned ? null : download.RetentionError ?? "Originals are still required."));
        }
        return result;
    }

    private async Task<bool> EligibleAsync(Download download, CancellationToken ct)
    {
        if (download.KeepSeeding || !download.StopRequested) return false;
        if (download.CancellationRequested) return !download.PlacementStarted;
        var ingests = await database.IngestItems.AsNoTracking().Where(x => x.DownloadId == download.Id).ToListAsync(ct);
        if (!download.CleanupRequested || ingests.Any(x => x.Status != IngestStatus.Done)) return false;
        var ids = ingests.Select(x => x.Id).ToArray();
        var needed = await database.SourceFiles.Where(x => ids.Contains(x.IngestItemId) &&
            x.AssignmentStatus != SourceFileAssignmentStatus.Skipped).Select(x => x.RelativePath).ToListAsync(ct);
        return !needed.Any(CatalogPaths.IsIncoming);
    }

    private static long Size(string root, CancellationToken ct)
    {
        if (!Directory.Exists(root)) return 0;
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked directory is protected.");
        long total = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            ct.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked content is protected.");
            total = checked(total + ((attributes & FileAttributes.Directory) != 0 ? Size(path, ct) : new FileInfo(path).Length));
        }
        return total;
    }
}

public static class TemporaryDownloadEndpoints
{
    public static void MapTemporaryDownloadEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/settings/temporary-downloads").RequireAuthorization(AppRoles.AdminPolicy);
        group.MapGet("/", async (TemporaryDownloadService service, CancellationToken ct) => Results.Ok(await service.AnalyzeAsync(ct)));
        group.MapPost("/clean", async (CleanTemporaryDownloadsRequest request, TemporaryDownloadService service, CancellationToken ct) =>
            Results.Ok(await service.CleanAsync(request.Ids, ct)));
    }
}
