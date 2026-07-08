using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Media;
using MediaServer.Api.Organizer;
using MediaServer.Api.Probe;
using MediaServer.Api.Torrents;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Pipeline.Stages;

/// <summary>Resolves the catalog layout for a freshly added download (torrent entry only).</summary>
public sealed class IntakeStage : IPipelineStage
{
    public string Key => "intake";
    public PipelinePhase Phase => PipelinePhase.Processing;
    public int Order => 100;
    public IngestStage Stage => IngestStage.Intake;

    public bool ShouldRun(IngestContext context) => !context.Item.StagesCompleted.Contains(Key);

    public Task<StageResult> RunAsync(IngestContext context, CancellationToken cancellationToken)
    {
        context.Paths.EnsureCreated();
        return Task.FromResult(StageResult.Done);
    }
}

/// <summary>
/// Waits for the torrent, then performs the download→identify hand-off: a completed, non-seeding download
/// is dropped (its files stay in <c>.incoming/</c>, now owned by the ingest) so the rest of the pipeline
/// runs torrent-free. While <c>keepSeeding</c> is on the item parks here until the operator stops seeding.
/// Scan-originated items have no download and skip straight through.
/// </summary>
public sealed class DownloadStage(MediaServerDbContext database, ITorrentEngine engine, ICatalogPathSandbox sandbox, ILogger<DownloadStage> logger) : IPipelineStage
{
    public string Key => "download";
    public PipelinePhase Phase => PipelinePhase.Processing;
    public int Order => 200;
    public IngestStage Stage => IngestStage.Download;

    public bool ShouldRun(IngestContext context) => !context.Item.StagesCompleted.Contains(Key);

    public async Task<StageResult> RunAsync(IngestContext context, CancellationToken cancellationToken)
    {
        // Scan import enters the pipeline at identify; there is no download to wait for.
        if (context.Download is not { } download)
        {
            return StageResult.Done;
        }

        switch (download.State)
        {
            case DownloadState.Error:
                return new StageResult.Failed("Torrent entered an error state.", Retryable: false);
            case DownloadState.Queued or DownloadState.Downloading:
                return new StageResult.Deferred(TimeSpan.FromSeconds(30));
            case DownloadState.Seeding:
                // keepSeeding: the file stays seedable in .incoming/ and the item parks here until the
                // operator stops seeding (TorrentService.StopSeedingAsync flips the state and re-drives).
                return new StageResult.Deferred(TimeSpan.FromSeconds(30));
        }

        // Completed / StoppedSeeding: the transfer reports done. Guard against a phantom completion before
        // the irreversible hand-off: a torrent can report complete from stale resume data with nothing on
        // the new save path, and handing off would drop the Download and strand a file-less ingest.
        if (context.SourceFiles.Count == 0)
        {
            // File list not in yet; wait for the coordinator to upsert it.
            return new StageResult.Deferred(TimeSpan.FromSeconds(15));
        }

        var anyFileOnDisk = context.SourceFiles.Any(file =>
            sandbox.TryResolve(context.Catalog, file.RelativePath, out var absolute) && File.Exists(absolute));
        if (!anyFileOnDisk)
        {
            return new StageResult.Failed(
                "The torrent reports complete but its files are missing on disk (likely stale resume data). " +
                "Remove it and add it again to download.", Retryable: false);
        }

        // Hand off — drop the Download row first (re-parent its source files + the ingest item), then do a
        // best-effort engine removal. The files stay in .incoming/, owned by the ingest (the download FKs are
        // SET NULL). Persisting the DB change before the engine call means a transient engine error can't
        // strand the pipeline with a half-applied hand-off. Done once, even if a later stage re-drives.
        foreach (var sourceFile in context.SourceFiles)
        {
            sourceFile.DownloadId = null;
        }

        context.Item.DownloadId = null;
        database.Downloads.Remove(download);
        await database.SaveChangesAsync(cancellationToken);

        try
        {
            await engine.RemoveAsync(download.InfoHash, deleteFiles: false, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Engine removal during hand-off of download {DownloadId} failed; files kept in .incoming/.", download.Id);
        }

        logger.LogInformation(
            "Handed off download {DownloadId} to ingest {Ingest}: row removed, files kept in .incoming/.",
            download.Id, context.Item.Id);
        return StageResult.Done;
    }
}

/// <summary>Maps each playable source file to a movie or episode; ambiguous matches route to review.</summary>
public sealed class IdentifyStage(IdentifyService identifyService) : IPipelineStage
{
    public string Key => "identify";
    public PipelinePhase Phase => PipelinePhase.Processing;
    public int Order => 300;
    public IngestStage Stage => IngestStage.Identify;

    public bool ShouldRun(IngestContext context) => !context.Item.StagesCompleted.Contains(Key);

    public async Task<StageResult> RunAsync(IngestContext context, CancellationToken cancellationToken)
    {
        if (context.SourceFiles.Count == 0)
        {
            // Torrent metadata not in yet; the coordinator re-drives once the file list arrives.
            return new StageResult.Deferred(TimeSpan.FromSeconds(15));
        }

        var outcome = await identifyService.IdentifyAsync(
            context.Catalog, context.SourceFiles, context.Download?.Name, PinnedTargetOf(context.Item), cancellationToken);

        return outcome.AllResolved
            ? StageResult.Done
            : new StageResult.NeedsReview(outcome.ReviewReason ?? "Manual match required.", outcome.Candidates);
    }

    // The operator- or acquisition-pinned identity, or null when nothing is pinned (the default auto-identify
    // path). The five Target* columns are set and cleared together, so provider + id + kind present them all.
    private static TargetIdentity? PinnedTargetOf(IngestItem item) =>
        item is { TargetProvider: { } provider, TargetProviderId: { } providerId, TargetKind: { } kind }
            ? new TargetIdentity(provider, providerId, kind, item.TargetTitle ?? string.Empty, item.TargetYear)
            : null;
}

/// <summary>Moves confirmed files into the canonical catalog layout (rename in place — no copy/hardlink).</summary>
public sealed class OrganizeStage(IOrganizer organizer) : IPipelineStage
{
    public string Key => "organize";
    public PipelinePhase Phase => PipelinePhase.Processing;
    public int Order => 400;
    public IngestStage Stage => IngestStage.Organize;

    public bool ShouldRun(IngestContext context) => !context.Item.StagesCompleted.Contains(Key);

    public async Task<StageResult> RunAsync(IngestContext context, CancellationToken cancellationToken)
    {
        await organizer.OrganizeAsync(context.SourceFiles, context.Catalog, cancellationToken);
        return StageResult.Done;
    }
}

/// <summary>Probes each organized library file into media sources and streams.</summary>
public sealed class ProbeStage(IMediaProbe probe, MediaServerDbContext database) : IPipelineStage
{
    public string Key => "probe";
    public PipelinePhase Phase => PipelinePhase.Processing;
    public int Order => 500;
    public IngestStage Stage => IngestStage.Probe;

    public bool ShouldRun(IngestContext context) => !context.Item.StagesCompleted.Contains(Key);

    public async Task<StageResult> RunAsync(IngestContext context, CancellationToken cancellationToken)
    {
        // One MediaSource per organized file. An item with several files (e.g. a black-and-white and a
        // regular cut of one episode) becomes a multi-version item: every source is probed and exposed,
        // and clients render a version picker keyed by MediaSourceId.
        var assignedFiles = context.SourceFiles
            .Where(file => file.MediaItemId is not null && MediaFormats.IsPlayableMedia(file.RelativePath, file.SizeBytes));

        foreach (var sourceFile in assignedFiles)
        {
            var mediaItemId = sourceFile.MediaItemId!.Value;
            var absolute = Path.Combine(context.Catalog.Root, sourceFile.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absolute))
            {
                continue;
            }

            var alreadyProbed = await database.MediaSources.AnyAsync(
                source => source.MediaItemId == mediaItemId && source.Path == sourceFile.RelativePath, cancellationToken);
            if (alreadyProbed)
            {
                continue;
            }

            var result = await probe.ProbeAsync(absolute, cancellationToken);
            var source = new MediaSource
            {
                Id = Guid.NewGuid(),
                MediaItemId = mediaItemId,
                SourceFileId = sourceFile.Id,
                VersionName = sourceFile.Edition,
                Container = result.Container,
                Path = sourceFile.RelativePath,
                SizeBytes = result.SizeBytes,
                Bitrate = result.Bitrate,
                DurationTicks = result.DurationTicks,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            database.MediaSources.Add(source);

            foreach (var stream in result.Streams)
            {
                database.MediaStreams.Add(new MediaStream
                {
                    Id = Guid.NewGuid(),
                    MediaSourceId = source.Id,
                    StreamType = stream.Type,
                    Index = stream.Index,
                    Codec = stream.Codec,
                    Profile = stream.Profile,
                    Language = stream.Language,
                    Title = stream.Title,
                    Width = stream.Width,
                    Height = stream.Height,
                    FrameRate = stream.FrameRate,
                    BitDepth = stream.BitDepth,
                    HdrFormat = stream.HdrFormat,
                    Channels = stream.Channels,
                    SampleRate = stream.SampleRate,
                    IsDefault = stream.IsDefault,
                    IsForced = stream.IsForced,
                });
            }
        }

        await database.SaveChangesAsync(cancellationToken);
        return StageResult.Done;
    }
}

/// <summary>Fetches and caches metadata + images for the matched movies/series in all languages.</summary>
public sealed class EnrichStage(EnrichService enrichService, MediaServerDbContext database) : IPipelineStage
{
    public string Key => "enrich";
    public PipelinePhase Phase => PipelinePhase.Processing;
    public int Order => 600;
    public IngestStage Stage => IngestStage.Enrich;

    public bool ShouldRun(IngestContext context) => !context.Item.StagesCompleted.Contains(Key);

    public async Task<StageResult> RunAsync(IngestContext context, CancellationToken cancellationToken)
    {
        var graph = await IngestGraph.LoadAsync(database, context.Item.Id, cancellationToken);

        foreach (var item in graph.All.Where(item => item.Kind is MediaKind.Movie or MediaKind.Series))
        {
            await enrichService.EnrichAsync(context.Catalog, item, cancellationToken);
        }

        return StageResult.Done;
    }
}

/// <summary>Assigns the stable public id and makes the item browsable/playable.</summary>
public sealed class PublishStage(MediaServerDbContext database) : IPipelineStage
{
    public string Key => "publish";
    public PipelinePhase Phase => PipelinePhase.Processing;
    public int Order => 700;
    public IngestStage Stage => IngestStage.Publish;

    public bool ShouldRun(IngestContext context) => !context.Item.StagesCompleted.Contains(Key);

    public async Task<StageResult> RunAsync(IngestContext context, CancellationToken cancellationToken)
    {
        var graph = await IngestGraph.LoadAsync(database, context.Item.Id, cancellationToken);

        foreach (var item in graph.All)
        {
            item.PublicId ??= PublicIdFactory.ForItem(item);
            item.UpdatedAt = DateTimeOffset.UtcNow;
        }

        // Point the ingest item at the primary published media item (the movie, or the first episode).
        var primary = graph.Leaves.FirstOrDefault();
        if (primary is not null)
        {
            context.Item.MediaItemId = primary.Id;
        }

        await database.SaveChangesAsync(cancellationToken);
        return StageResult.Done;
    }
}
