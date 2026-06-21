using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.IO;
using MediaServer.Api.Pipeline;
using MediaServer.Api.Realtime;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Torrents;

/// <summary>
/// Bridges the (database-unaware) torrent engine to persistence and the pipeline. Broadcasts live
/// progress on a timer, and translates engine events into persisted <see cref="DownloadState"/>
/// transitions that re-drive the affected ingest item. On startup it re-adds non-terminal downloads
/// so they resume after a restart.
/// </summary>
public sealed class TorrentCoordinator(
    ITorrentEngine engine,
    IServiceScopeFactory scopeFactory,
    IRealtimeNotifier notifier,
    ILogger<TorrentCoordinator> logger)
    : BackgroundService
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(1500);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        engine.MetadataReceived += OnMetadataReceived;
        engine.DownloadCompleted += OnDownloadCompleted;
        engine.DownloadErrored += OnDownloadErrored;

        await ResumeDownloadsAsync(stoppingToken);

        try
        {
            using var timer = new PeriodicTimer(ProgressInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await BroadcastProgressAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            engine.MetadataReceived -= OnMetadataReceived;
            engine.DownloadCompleted -= OnDownloadCompleted;
            engine.DownloadErrored -= OnDownloadErrored;
        }
    }

    private async Task ResumeDownloadsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var settings = scope.ServiceProvider.GetRequiredService<MediaServerSettings>();

            var active = await database.Downloads
                .Where(download => download.State == DownloadState.Downloading
                                   || download.State == DownloadState.Queued
                                   || download.State == DownloadState.Seeding)
                .ToListAsync(cancellationToken);

            foreach (var download in active.Where(download => download.SourceUri is not null))
            {
                try
                {
                    var source = ResolveResumeSource(download.SourceUri!);
                    if (source is null)
                    {
                        logger.LogWarning("Cannot resume download {InfoHash}: source {SourceUri} is unavailable.", download.InfoHash, download.SourceUri);
                        continue;
                    }

                    var limits = new TorrentLimits(settings.TorrentMaxDownloadSpeed, settings.TorrentMaxUploadSpeed);
                    await engine.AddAsync(source, download.SavePath, limits, autoStart: true, cancellationToken);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Failed to resume download {InfoHash} after restart.", download.InfoHash);
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Error resuming downloads after restart.");
        }
    }

    /// <summary>Rebuilds the torrent source from the persisted handle: a magnet URI, or a stored
    /// .torrent file. Returns null when the stored file is missing.</summary>
    private static TorrentSource? ResolveResumeSource(string sourceUri)
    {
        if (sourceUri.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return new TorrentSource.Magnet(sourceUri);
        }

        return File.Exists(sourceUri) ? new TorrentSource.File(File.ReadAllBytes(sourceUri), Path.GetFileName(sourceUri)) : null;
    }

    private async Task BroadcastProgressAsync(CancellationToken cancellationToken)
    {
        var snapshots = engine.GetAllSnapshots();
        if (snapshots.Count == 0)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var byHash = await database.Downloads
            .AsNoTracking()
            .ToDictionaryAsync(download => download.InfoHash, download => download, StringComparer.OrdinalIgnoreCase, cancellationToken);

        foreach (var snapshot in snapshots)
        {
            if (!byHash.TryGetValue(snapshot.InfoHash, out var download))
            {
                continue;
            }

            long? eta = snapshot.DownloadRateBytesPerSecond > 0 && !snapshot.Complete
                ? (long)(snapshot.SizeBytes * (1 - snapshot.PercentComplete / 100.0) / snapshot.DownloadRateBytesPerSecond)
                : null;

            // Send the live engine state (e.g. Paused/Downloading), not the persisted DownloadState — the
            // UI derives the pause/resume affordance from it, and the DB state never reflects a pause. Coarse
            // DB transitions ride the separate downloadStateChanged event.
            await notifier.DownloadProgressAsync(new DownloadProgress(
                download.Id,
                snapshot.EngineState,
                snapshot.PercentComplete,
                snapshot.DownloadRateBytesPerSecond,
                snapshot.UploadRateBytesPerSecond,
                snapshot.Ratio,
                snapshot.Peers,
                snapshot.SizeBytes,
                eta), cancellationToken);

            // Self-heal a missed completion: the engine reports the torrent complete but our persisted
            // state never caught up (e.g. a re-added, already-complete torrent that finished hashing). Drive
            // the completion transition now — idempotent, and only while the state is still non-terminal.
            if (snapshot.Complete && download.State is DownloadState.Queued or DownloadState.Downloading)
            {
                await HandleCompletedAsync(snapshot.InfoHash);
            }
        }
    }

    private void OnMetadataReceived(object? sender, string infoHash) => RunSafely(() => HandleMetadataAsync(infoHash));

    private void OnDownloadCompleted(object? sender, string infoHash) => RunSafely(() => HandleCompletedAsync(infoHash));

    private void OnDownloadErrored(object? sender, string infoHash) => RunSafely(() => HandleErroredAsync(infoHash));

    private async Task HandleMetadataAsync(string infoHash)
    {
        using var scope = scopeFactory.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var fileService = scope.ServiceProvider.GetRequiredService<DownloadFileService>();
        var filesystem = scope.ServiceProvider.GetRequiredService<IFilesystemInspector>();
        var pipelineQueue = scope.ServiceProvider.GetRequiredService<IPipelineQueue>();

        var download = await database.Downloads
            .Include(item => item.Catalog)
            .FirstOrDefaultAsync(item => item.InfoHash == infoHash);
        if (download is null)
        {
            return;
        }

        var files = engine.GetFiles(infoHash);
        var snapshot = engine.GetSnapshot(infoHash);
        if (snapshot?.Name is { Length: > 0 } && string.IsNullOrEmpty(download.Name))
        {
            download.Name = snapshot.Name;
            await database.SaveChangesAsync();
        }

        await fileService.UpsertSourceFilesAsync(download.Id, files, CancellationToken.None);

        // Magnet free-space check: size is only known now. Cannot refuse retroactively, so warn.
        if (snapshot is { SizeBytes: > 0 } && download.Catalog is { } catalog &&
            snapshot.SizeBytes > filesystem.GetAvailableFreeBytes(catalog.Root))
        {
            logger.LogWarning("Download {InfoHash} ({Size} bytes) may not fit in catalog '{Catalog}'.",
                infoHash, snapshot.SizeBytes, catalog.Name);
        }

        await EnqueueIngestAsync(database, pipelineQueue, download.Id);
    }

    private async Task HandleCompletedAsync(string infoHash)
    {
        using var scope = scopeFactory.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var fileService = scope.ServiceProvider.GetRequiredService<DownloadFileService>();
        var pipelineQueue = scope.ServiceProvider.GetRequiredService<IPipelineQueue>();

        var download = await database.Downloads.FirstOrDefaultAsync(item => item.InfoHash == infoHash);
        if (download is null)
        {
            return;
        }

        // Ensure source files exist (a re-added complete torrent may skip the metadata path).
        await fileService.UpsertSourceFilesAsync(download.Id, engine.GetFiles(infoHash), CancellationToken.None);

        if (download.State is not (DownloadState.Completed or DownloadState.Seeding or DownloadState.StoppedSeeding))
        {
            // keepSeeding parks the ingest at the download stage (seeding is mutually exclusive with being
            // in the library) and leaves MonoTorrent's auto-seed running until the operator stops it.
            // Otherwise mark it Completed and stop uploading now so the download→identify hand-off proceeds.
            download.State = download.KeepSeeding ? DownloadState.Seeding : DownloadState.Completed;
            download.CompletedAt = DateTimeOffset.UtcNow;
            await database.SaveChangesAsync();

            if (!download.KeepSeeding)
            {
                await engine.StopAsync(infoHash, CancellationToken.None);
            }
        }

        await notifier.DownloadStateChangedAsync(new DownloadStateChanged(download.Id, download.State.ToString(), download.Name));
        await EnqueueIngestAsync(database, pipelineQueue, download.Id);
    }

    private async Task HandleErroredAsync(string infoHash)
    {
        using var scope = scopeFactory.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var pipelineQueue = scope.ServiceProvider.GetRequiredService<IPipelineQueue>();

        var download = await database.Downloads.FirstOrDefaultAsync(item => item.InfoHash == infoHash);
        if (download is null)
        {
            return;
        }

        download.State = DownloadState.Error;
        await database.SaveChangesAsync();
        await notifier.DownloadStateChangedAsync(new DownloadStateChanged(download.Id, download.State.ToString(), download.Name));
        await EnqueueIngestAsync(database, pipelineQueue, download.Id);
    }

    private static async Task EnqueueIngestAsync(MediaServerDbContext database, IPipelineQueue pipelineQueue, Guid downloadId)
    {
        var ingestIds = await database.IngestItems
            .Where(item => item.DownloadId == downloadId)
            .Select(item => item.Id)
            .ToListAsync();

        foreach (var id in ingestIds)
        {
            pipelineQueue.Enqueue(id);
        }
    }

    private void RunSafely(Func<Task> work)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await work();
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Torrent coordinator event handler failed.");
            }
        });
    }
}
