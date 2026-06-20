using MediaServer.Api.Data;
using MediaServer.Api.Organizer;
using MediaServer.Api.Probe;
using MediaServer.Api.Torrents;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Pipeline.Stages;

/// <summary>Resolves the catalog layout and seeding policy for a freshly added download.</summary>
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

/// <summary>Waits for the torrent to finish; the engine started downloading the moment it was added.</summary>
public sealed class DownloadStage : IPipelineStage
{
    public string Key => "download";
    public PipelinePhase Phase => PipelinePhase.Processing;
    public int Order => 200;
    public IngestStage Stage => IngestStage.Download;

    public bool ShouldRun(IngestContext context) => !context.Item.StagesCompleted.Contains(Key);

    public Task<StageResult> RunAsync(IngestContext context, CancellationToken cancellationToken)
    {
        if (context.Download is null)
        {
            return Task.FromResult<StageResult>(new StageResult.Failed("Ingest item has no download.", Retryable: false));
        }

        return Task.FromResult<StageResult>(context.Download.State switch
        {
            DownloadState.Completed or DownloadState.Seeding or DownloadState.StoppedSeeding => StageResult.Done,
            DownloadState.Error => new StageResult.Failed("Torrent entered an error state.", Retryable: false),
            _ => new StageResult.Deferred(TimeSpan.FromSeconds(30)),
        });
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
            // Magnet metadata not in yet; the coordinator re-drives once the file list arrives.
            return new StageResult.Deferred(TimeSpan.FromSeconds(15));
        }

        var outcome = await identifyService.IdentifyAsync(
            context.Catalog, context.SourceFiles, context.Download?.Name, cancellationToken);

        return outcome.AllResolved
            ? StageResult.Done
            : new StageResult.NeedsReview(outcome.ReviewReason ?? "Manual match required.", outcome.Candidates);
    }
}

/// <summary>Hardlinks confirmed files into the clean library layout and applies the seeding policy.</summary>
public sealed class OrganizeStage(
    IOrganizer organizer, MediaServerDbContext database, ITorrentEngine engine) : IPipelineStage
{
    public string Key => "organize";
    public PipelinePhase Phase => PipelinePhase.Processing;
    public int Order => 400;
    public IngestStage Stage => IngestStage.Organize;

    public bool ShouldRun(IngestContext context) => !context.Item.StagesCompleted.Contains(Key);

    public async Task<StageResult> RunAsync(IngestContext context, CancellationToken cancellationToken)
    {
        if (context.Download is null)
        {
            return new StageResult.Failed("Ingest item has no download.", Retryable: false);
        }

        await organizer.OrganizeAsync(context.Download, context.SourceFiles, context.Catalog, cancellationToken);

        // Seeding policy. Keep seeding from files/, or ensure the torrent is not seeding. The files/ seed
        // copy and the Download row are intentionally NOT torn down here — that happens once the item
        // reaches Done, so a stall before publish (e.g. NeedsReview) never strands a torn-down item that
        // still needs its source files. See DownloadCleanupService.
        if (context.Download.KeepSeeding)
        {
            context.Download.State = DownloadState.Seeding;
        }
        else
        {
            // Belt-and-suspenders: the coordinator already stops the engine on completion when seeding is
            // off. Leave State = Completed so the Done teardown recognises this download as not-seeding.
            await engine.StopAsync(context.Download.InfoHash, cancellationToken);
        }

        await database.SaveChangesAsync(cancellationToken);
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
        if (context.Download is null)
        {
            return new StageResult.Failed("Ingest item has no download.", Retryable: false);
        }

        var graph = await IngestGraph.LoadAsync(database, context.Download.Id, cancellationToken);

        foreach (var item in graph.Leaves.Where(item => item.LibraryPath is not null))
        {
            var absolute = Path.Combine(context.Catalog.Root, item.LibraryPath!.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absolute))
            {
                continue;
            }

            var alreadyProbed = await database.MediaSources.AnyAsync(
                source => source.MediaItemId == item.Id && source.Path == item.LibraryPath, cancellationToken);
            if (alreadyProbed)
            {
                continue;
            }

            var result = await probe.ProbeAsync(absolute, cancellationToken);
            var source = new MediaSource
            {
                Id = Guid.NewGuid(),
                MediaItemId = item.Id,
                Container = result.Container,
                Path = item.LibraryPath!,
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
        if (context.Download is null)
        {
            return new StageResult.Failed("Ingest item has no download.", Retryable: false);
        }

        var graph = await IngestGraph.LoadAsync(database, context.Download.Id, cancellationToken);

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
        if (context.Download is null)
        {
            return new StageResult.Failed("Ingest item has no download.", Retryable: false);
        }

        var graph = await IngestGraph.LoadAsync(database, context.Download.Id, cancellationToken);

        foreach (var item in graph.All)
        {
            item.PublicId ??= BuildPublicId(item);
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

    private static string BuildPublicId(MediaItem item)
    {
        var provider = item.IdentityProvider ?? "local";
        var providerId = item.IdentityProviderId ?? item.Id.ToString("N");

        return item.Kind switch
        {
            MediaKind.Movie or MediaKind.Video => PublicIdFactory.ForMovie(item.CatalogId, provider, providerId),
            MediaKind.Series => PublicIdFactory.ForSeries(item.CatalogId, provider, providerId),
            MediaKind.Season => PublicIdFactory.ForSeason(item.CatalogId, provider, providerId, item.IdentitySeasonNumber ?? item.IndexNumber ?? 1),
            MediaKind.Episode => PublicIdFactory.ForEpisode(item.CatalogId, provider, providerId,
                item.IdentitySeasonNumber ?? item.ParentIndexNumber ?? 1, item.IdentityEpisodeNumber ?? item.IndexNumber ?? 0),
            _ => PublicIdFactory.ForMovie(item.CatalogId, provider, providerId),
        };
    }
}
