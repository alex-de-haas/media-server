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
    MediaServerSettings settings,
    HostyOptions hosty,
    IPipelineQueue pipelineQueue,
    DownloadCleanupService cleanup,
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

        if (await database.Downloads.AnyAsync(candidate => candidate.InfoHash == descriptor.InfoHash, cancellationToken))
        {
            throw new TorrentRequestException("This torrent is already being managed.");
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
        var limits = new TorrentLimits(settings.TorrentMaxDownloadSpeed, settings.TorrentMaxUploadSpeed);

        // Persist enough to re-add the torrent after a restart: the magnet URI, or the stored .torrent path.
        var sourceUri = PersistSource(source, descriptor.InfoHash);

        var now = DateTimeOffset.UtcNow;
        var download = new Download
        {
            Id = Guid.NewGuid(),
            InfoHash = descriptor.InfoHash,
            Name = descriptor.Name,
            CatalogId = catalog.Id,
            SourceType = source is TorrentSource.Magnet ? TorrentSourceType.Magnet : TorrentSourceType.File,
            State = DownloadState.Downloading,
            KeepSeeding = keepSeeding,
            SavePath = paths.FilesDir,
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
            await engine.AddAsync(source, paths.FilesDir, limits, autoStart: true, cancellationToken);
        }
        catch (Exception exception) when (exception is not TorrentRequestException)
        {
            // Roll back the just-created rows so a failed start never leaves an orphaned download/ingest.
            database.IngestItems.Remove(ingest);
            database.Downloads.Remove(download);
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
        var download = await database.Downloads.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (download is null)
        {
            return false;
        }

        await engine.PauseAsync(download.InfoHash, cancellationToken);
        return true;
    }

    public async Task<bool> ResumeAsync(Guid id, CancellationToken cancellationToken)
    {
        var download = await database.Downloads.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (download is null)
        {
            return false;
        }

        await engine.ResumeAsync(download.InfoHash, cancellationToken);
        return true;
    }

    /// <summary>
    /// Stops seeding a torrent. Seeding is a terminal, one-way choice: there is no resume. If the ingest
    /// has already published (the common case — seeding only begins after publish), the whole download
    /// backing is torn down now (files/ seed copy reclaimed, Download + SourceFiles removed, library kept).
    /// If the pipeline is still running, the torrent is just marked not-seeding and the orchestrator's Done
    /// step performs the teardown.
    /// </summary>
    public async Task<bool> StopSeedingAsync(Guid id, CancellationToken cancellationToken)
    {
        var download = await database.Downloads.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (download is null)
        {
            return false;
        }

        // Only a seeding torrent can stop seeding. Ignore the request for any other state (e.g. one still
        // downloading) so a partial download is never mis-marked Completed and advanced on partial files.
        if (download.State != DownloadState.Seeding)
        {
            return true;
        }

        await engine.StopAsync(download.InfoHash, cancellationToken);
        download.State = DownloadState.Completed;
        download.KeepSeeding = false;
        await database.SaveChangesAsync(cancellationToken);

        var published = await database.IngestItems
            .AnyAsync(item => item.DownloadId == id && item.Status == IngestStatus.Done, cancellationToken);
        if (published)
        {
            await cleanup.TeardownAsync(id, cancellationToken);
        }

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
