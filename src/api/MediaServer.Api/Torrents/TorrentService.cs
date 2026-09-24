using MediaServer.Api.Catalogs;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.IO;
using MediaServer.Api.Pipeline;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Torrents;

/// <summary>
/// Operator-facing torrent commands: add (with the pre-download free-space check and pipeline kick-off),
/// pause/resume, stop seeding, and remove. Persists only durable facts and state transitions — live
/// progress stays in the engine and is broadcast over the realtime stream (SSE).
/// </summary>
public sealed class TorrentService(
    MediaServerDbContext database,
    ITorrentEngine engine,
    IFilesystemInspector filesystem,
    HostyOptions hosty,
    IPipelineQueue pipelineQueue,
    DownloadDeletionService downloadDeletion,
    DownloadRetentionService retention,
    ILogger<TorrentService> logger)
{
    public async Task<DownloadResponse> AddAsync(AddTorrentRequest request, CancellationToken cancellationToken)
    {
        var source = ResolveSource(request);

        var catalog = await database.Catalogs.FirstOrDefaultAsync(candidate => candidate.Id == request.CatalogId, cancellationToken)
            ?? throw new TorrentRequestException("Catalog not found.");

        TorrentDescriptor descriptor;
        try
        {
            descriptor = engine.Inspect(source);
        }
        catch (Exception exception) when (exception is not TorrentRequestException)
        {
            throw new TorrentRequestException($"Could not parse the torrent source: {exception.Message}");
        }

        // Block only an actively-managed torrent. A stale download for this info hash — orphaned (no ingest),
        // or whose only ingest(s) failed (e.g. a phantom completion the operator is re-adding) — is reclaimed:
        // drop it along with its staging and engine resume state, then add fresh below.
        var existing = await database.Downloads.FirstOrDefaultAsync(candidate => candidate.InfoHash == descriptor.InfoHash, cancellationToken);
        if (existing is not null)
        {
            var managed = await database.IngestItems
                .AnyAsync(item => item.DownloadId == existing.Id && item.Status != IngestStatus.Failed, cancellationToken);
            if (managed)
            {
                throw new TorrentRequestException("This torrent is already being managed.");
            }

            await downloadDeletion.DeleteAsync(existing.Id, deleteFiles: true, cancellationToken);
        }

        var paths = CatalogPaths.For(catalog);
        paths.EnsureCreated();

        // .torrent size is known up front: refuse an oversized download before it starts. Magnet size
        // is unknown until metadata, so that check runs later in the coordinator and only notifies.
        if (descriptor is { HasMetadata: true, TotalSize: { } size } && size > filesystem.GetAvailableFreeBytes(catalog.Root))
        {
            throw new TorrentRequestException(
                $"Not enough free space in catalog '{catalog.Name}' for this download ({size} bytes required).");
        }

        var keepSeeding = request.KeepSeeding ?? catalog.DefaultKeepSeeding;

        // Persist enough to re-add the torrent after a restart: the magnet URI, or the stored .torrent path.
        var sourceUri = PersistSource(source, descriptor.InfoHash);

        var now = DateTimeOffset.UtcNow;
        var downloadId = Guid.NewGuid();
        var savePath = paths.IncomingFor(downloadId);
        Directory.CreateDirectory(savePath);
        var download = new Download
        {
            Id = downloadId,
            InfoHash = descriptor.InfoHash,
            Name = descriptor.Name,
            CatalogId = catalog.Id,
            SourceType = source is TorrentSource.Magnet ? TorrentSourceType.Magnet : TorrentSourceType.File,
            State = DownloadState.Downloading,
            KeepSeeding = keepSeeding,
            SavePath = savePath,
            SourceUri = sourceUri,
            AddedAt = now,
        };
        database.Downloads.Add(download);

        // Kick off the pipeline: an ingest item is created at Intake and handed to the orchestrator.
        var ingest = new IngestItem
        {
            Id = Guid.NewGuid(),
            CatalogId = catalog.Id,
            DownloadId = download.Id,
            Stage = IngestStage.Intake,
            Status = IngestStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };
        database.IngestItems.Add(ingest);

        // Commit BEFORE starting the engine. A re-added, already-complete torrent finishes hashing and
        // fires MetadataReceived/DownloadCompleted almost immediately; the coordinator resolves the
        // download by info hash, so if the row isn't committed yet those handlers no-op and the item is
        // stranded in "download" while the engine seeds. Persisting first closes that race.
        await database.SaveChangesAsync(cancellationToken);

        try
        {
            await engine.AddAsync(source, savePath, autoStart: true, cancellationToken);
        }
        catch (Exception exception) when (exception is not TorrentRequestException)
        {
            // An add timeout may have reached the engine. Keep ownership evidence for safe cancellation.
            download.State = DownloadState.Error;
            download.RetentionError = $"Could not start the torrent: {exception.Message}";
            ingest.Status = IngestStatus.Failed;
            ingest.LastError = download.RetentionError;
            await database.SaveChangesAsync(cancellationToken);
            throw new TorrentRequestException($"Could not start the torrent: {exception.Message}");
        }

        pipelineQueue.Enqueue(ingest.Id);

        logger.LogInformation("Added torrent {InfoHash} to catalog {Catalog}; ingest {IngestItem} queued.",
            download.InfoHash, catalog.Name, ingest.Id);

        return DownloadResponse.From(download, engine.GetSnapshot(download.InfoHash));
    }

    public async Task<IReadOnlyList<DownloadResponse>> ListAsync(CancellationToken cancellationToken)
    {
        var downloads = await database.Downloads
            .AsNoTracking()
            .OrderByDescending(download => download.AddedAt)
            .ToListAsync(cancellationToken);

        return downloads
            .Select(download => DownloadResponse.From(download, engine.GetSnapshot(download.InfoHash)))
            .ToList();
    }

    public async Task<bool> PauseAsync(Guid id, CancellationToken cancellationToken)
    {
        using var gate = await IngestMutationGate.EnterAsync(cancellationToken);
        var download = await database.Downloads.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (download is null)
        {
            return false;
        }

        if (download.StopRequested || download.EngineReleased) throw new TorrentRequestException("This torrent is being released and cannot be resumed.");
        await engine.PauseAsync(download.InfoHash, cancellationToken);
        return true;
    }

    public async Task<bool> ResumeAsync(Guid id, CancellationToken cancellationToken)
    {
        using var gate = await IngestMutationGate.EnterAsync(cancellationToken);
        var download = await database.Downloads.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (download is null)
        {
            return false;
        }

        if (download.StopRequested || download.EngineReleased) throw new TorrentRequestException("This torrent is being released and cannot be resumed.");
        await engine.ResumeAsync(download.InfoHash, cancellationToken);
        return true;
    }

    /// <summary>
    /// Ends retention after acknowledged engine release. Incomplete imports keep their original data;
    /// published imports remove only their owned staging tree, preserving the library and history.
    /// </summary>
    public async Task<bool> StopSeedingAsync(Guid id, CancellationToken cancellationToken)
    {
        using var gate = await IngestMutationGate.EnterAsync(cancellationToken);
        var download = await database.Downloads.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (download is null) return false;
        if (download.CompletedAt is null && download.State is not (DownloadState.Seeding or DownloadState.Completed or DownloadState.StoppedSeeding))
            throw new TorrentRequestException("The download is not complete. Change its seeding policy instead.");

        // Persist cleanup intent before releasing the engine; a restart cannot silently resume this seed.
        download.CleanupRequested = true;
        download.CleanupAttempts = 0;
        download.CleanupAfter = null;
        await retention.ReleaseAsync(download, cancellationToken);
        var ingests = await database.IngestItems.Where(item => item.DownloadId == id).ToListAsync(cancellationToken);
        foreach (var ingest in ingests.Where(item => item.Status != IngestStatus.Done))
        {
            if (ingest.Status != IngestStatus.NeedsReview)
            {
                ingest.Status = IngestStatus.Pending;
                ingest.NextAttemptAt = null;
                ingest.AttemptCount = 0;
                ingest.LeaseOwner = null;
                ingest.LeaseUntil = null;
                pipelineQueue.Enqueue(ingest.Id);
            }
        }
        await database.SaveChangesAsync(cancellationToken);
        await retention.CleanupAsync(download, false, cancellationToken);
        return true;
    }

    public async Task<bool> SetSeedingPolicyAsync(Guid id, bool keepSeeding, CancellationToken ct)
    {
        using var gate = await IngestMutationGate.EnterAsync(ct);
        var download = await database.Downloads.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (download is null) return false;
        if (download.PlacementStarted || download.StopRequested || download.EngineReleased)
            throw new TorrentRequestException("Placement has started. Use Stop seeding and continue to release the original files.");
        download.KeepSeeding = keepSeeding;
        if (download.CompletedAt is not null)
            download.State = keepSeeding ? DownloadState.Seeding : DownloadState.Completed;
        await database.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Returns a durable handle for restart-resume: the magnet URI, or the path to the stored
    /// .torrent file (written under the app data dir so it survives restarts).</summary>
    private string PersistSource(TorrentSource source, string infoHash)
    {
        if (source is TorrentSource.Magnet magnet)
        {
            return magnet.Uri;
        }

        var directory = Path.Combine(hosty.AppDataDir, "torrents");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{infoHash}.torrent");
        File.WriteAllBytes(path, ((TorrentSource.File)source).Content);
        return path;
    }

    private static TorrentSource ResolveSource(AddTorrentRequest request)
    {
        var hasMagnet = !string.IsNullOrWhiteSpace(request.Magnet);
        var hasFile = !string.IsNullOrWhiteSpace(request.TorrentFileBase64);

        if (hasMagnet == hasFile)
        {
            throw new TorrentRequestException("Provide exactly one of 'magnet' or 'torrentFileBase64'.");
        }

        if (hasMagnet)
        {
            return new TorrentSource.Magnet(request.Magnet!.Trim());
        }

        try
        {
            return new TorrentSource.File(Convert.FromBase64String(request.TorrentFileBase64!), null);
        }
        catch (FormatException)
        {
            throw new TorrentRequestException("'torrentFileBase64' is not valid base64.");
        }
    }
}
