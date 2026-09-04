using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.IO;
using MediaServer.Api.Pipeline;
using MediaServer.Api.Probe;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Library;

/// <summary>
/// The outcome of filling in media data that was read without the engine. <paramref name="Remaining"/>
/// counts the sources still on header-read data afterwards — files the engine could not answer for either,
/// typically because their catalog root is not bound into it.
/// </summary>
/// <param name="ItemsRefreshed">How many media items were re-probed.</param>
/// <param name="Remaining">Sources still carrying header-read data.</param>
/// <param name="SidecarsFilled">How many sidecar tracks gained the codec/channels/sample rate they lacked.</param>
public sealed record MediaBackfillReport(int ItemsRefreshed, int Remaining, int SidecarsFilled = 0);

/// <summary>
/// On-demand library maintenance for a single item: re-running the idempotent enrich step to pull fresh
/// provider data and images, and re-probing an item's files to replace its stored media data. Syncing a
/// whole catalog with its disk is <see cref="CatalogScanService"/>'s job.
/// </summary>
public sealed class LibraryMaintenanceService(
    MediaServerDbContext database,
    ICatalogPathSandbox sandbox,
    IMediaProbe probe,
    EnrichService enrichService,
    ILogger<LibraryMaintenanceService> logger)
{
    /// <summary>Re-fetches provider metadata + images for one published item. False when it isn't refreshable.</summary>
    public async Task<bool> RefreshMetadataAsync(Guid mediaItemId, CancellationToken cancellationToken)
    {
        var item = await database.MediaItems.FirstOrDefaultAsync(candidate => candidate.Id == mediaItemId, cancellationToken);
        if (item is null || item.IdentityProvider is null || item.IdentityProviderId is null)
        {
            return false; // Unknown, or never identified — nothing authoritative to refresh from.
        }

        var catalog = await database.Catalogs.FirstOrDefaultAsync(candidate => candidate.Id == item.CatalogId, cancellationToken);
        if (catalog is null)
        {
            return false;
        }

        await enrichService.EnrichAsync(catalog, item, cancellationToken);
        logger.LogInformation("Refreshed metadata for media item {MediaItem}.", item.Id);
        return true;
    }

    /// <summary>Re-runs ffprobe on every media source of one item and replaces its stored streams (and the
    /// source's own container/size/bitrate/duration). Lets an operator pick up probe data that wasn't
    /// captured at import time — e.g. per-track titles — without a full library rescan. Sources whose file is
    /// missing or that fail to probe are left untouched. False only when the item itself is unknown.</summary>
    public async Task<bool> RefreshMediaAsync(Guid mediaItemId, CancellationToken cancellationToken)
    {
        var item = await database.MediaItems.FirstOrDefaultAsync(candidate => candidate.Id == mediaItemId, cancellationToken);
        if (item is null)
        {
            return false;
        }

        var catalog = await database.Catalogs.FirstOrDefaultAsync(candidate => candidate.Id == item.CatalogId, cancellationToken);
        if (catalog is null)
        {
            return false;
        }

        var sources = await database.MediaSources
            .Include(source => source.Streams)
            .Where(source => source.MediaItemId == mediaItemId)
            .ToListAsync(cancellationToken);

        var reprobed = 0;
        foreach (var source in sources)
        {
            if (!sandbox.TryResolve(catalog, source.Path, out var absolute) || !File.Exists(absolute))
            {
                logger.LogWarning(
                    "Refresh media data: source '{Path}' of item {MediaItem} is missing on disk; skipping.",
                    source.Path, mediaItemId);
                continue;
            }

            ProbeResult result;
            try
            {
                result = await probe.ProbeAsync(absolute, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    exception, "Refresh media data: ffprobe failed for source '{Path}' of item {MediaItem}; skipping.",
                    source.Path, mediaItemId);
                continue;
            }

            source.Container = result.Container;
            source.SizeBytes = result.SizeBytes;
            source.Bitrate = result.Bitrate;
            source.DurationTicks = result.DurationTicks;
            source.ProbeSource = result.Source;

            // Swap the embedded stream set: deleting the old rows (distinct ids) and inserting the freshly
            // probed ones is simpler and safer than diffing by index, and the cascade keeps no orphans. The
            // new rows carry an explicit MediaSourceId, so they're added to the DbSet rather than the tracked
            // navigation (clearing/adding via source.Streams here trips an EF optimistic-concurrency failure
            // on save).
            //
            // External streams are deliberately spared. They describe sidecar files beside the video, not
            // tracks inside it, so probing the video says nothing about them — sweeping them here would
            // delete rows whose files are still on disk, making the tracks vanish from the UI with no way to
            // merge or remove them.
            database.MediaStreams.RemoveRange(source.Streams.Where(stream => !stream.IsExternal));
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
                    DvProfile = stream.DolbyVision?.Profile,
                    DvLevel = stream.DolbyVision?.Level,
                    DvBlSignalCompatibilityId = stream.DolbyVision?.BlSignalCompatibilityId,
                    DvElPresent = stream.DolbyVision?.ElPresent,
                    Channels = stream.Channels,
                    SampleRate = stream.SampleRate,
                    Bitrate = stream.Bitrate,
                    IsDefault = stream.IsDefault,
                    IsForced = stream.IsForced,
                });
            }

            reprobed++;
        }

        await database.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Refreshed media data for item {MediaItem}: re-probed {Count} of {Total} source(s).",
            mediaItemId, reprobed, sources.Count);
        return true;
    }

    /// <summary>
    /// Re-probes every source whose media data came from the container-header reader rather than the engine,
    /// so a library built while the transcode engine was detached can be filled in once it is back, and
    /// fills in the sidecar tracks that predate their specs being recorded. Returns how many items were
    /// touched, how many sources are still without engine data, and how many sidecars were filled.
    /// Scoped to one catalog, the unit a refresh runs on.
    /// <para>
    /// It rides along with a catalog's metadata refresh rather than standing as its own action, because it
    /// answers the same question that one does — what could not be known when these rows were written, that
    /// can be known now — only about the file rather than about the title. It is deliberately bounded to
    /// the rows a weaker provider wrote: re-probing a library that already has engine data would be a long
    /// pass that changes nothing.
    /// </para>
    /// </summary>
    public async Task<MediaBackfillReport> BackfillHeaderProbedAsync(Guid catalogId, CancellationToken cancellationToken)
    {
        // Two kinds of row have something to gain: those the header reader wrote, and those labelled Dolby
        // Vision before the configuration record was recorded — engine rows too, so this is the one place the
        // pass reaches past provenance. Both are bounded, and both are answered by the same re-probe.
        var itemIds = await database.MediaSources.AsNoTracking()
            .Where(source => source.MediaItem!.CatalogId == catalogId &&
                (source.ProbeSource == ProbeSource.Header ||
                 source.Streams.Any(stream => stream.HdrFormat != null && stream.HdrFormat.Contains("Dolby Vision") && stream.DvProfile == null)))
            .Select(source => source.MediaItemId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var refreshed = 0;
        if (itemIds.Count > 0)
        {
            logger.LogInformation("Backfilling media data for {Count} item(s) probed without the engine.", itemIds.Count);
            foreach (var itemId in itemIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await RefreshMediaAsync(itemId, cancellationToken))
                {
                    refreshed++;
                }
            }
        }

        // Sidecars ride along rather than getting their own action: both answer the same question — "what
        // could not be known when this row was written, that can be now" — and a `RefreshMediaAsync` pass
        // cannot do it, because it deliberately never touches external rows.
        var sidecarsFilled = await BackfillSidecarSpecsAsync(catalogId, cancellationToken);

        var remaining = await database.MediaSources.AsNoTracking()
            .CountAsync(source => source.ProbeSource == ProbeSource.Header && source.MediaItem!.CatalogId == catalogId,
                cancellationToken);

        logger.LogInformation(
            "Media backfill finished: {Refreshed} item(s) refreshed, {Sidecars} sidecar(s) filled, {Remaining} source(s) still without engine data.",
            refreshed, sidecarsFilled, remaining);
        return new MediaBackfillReport(refreshed, remaining, sidecarsFilled);
    }

    /// <summary>
    /// Reads codec, channel count, sample rate and bitrate into the sidecar rows that lack them — the ones
    /// placed before those were recorded, and any whose file the engine could not answer for at the time.
    /// <para>
    /// Only the technical fields are written. Language and title are a <b>labelling decision</b> the sidecar
    /// stage made across a whole cohort of files, weighing what the container tagged against what the paths
    /// reveal; re-reading one file's tags here would undo that with strictly less information.
    /// </para>
    /// <para>
    /// A missing codec is the marker for "never answered", so a file the engine still cannot read is simply
    /// picked up again next run rather than being recorded as having no codec.
    /// </para>
    /// <para>
    /// A missing bitrate on an <b>audio</b> sidecar is a second marker, because bitrate arrived after codec
    /// did: a row placed in between has a codec and would otherwise never be revisited, and the item-level
    /// refresh deliberately never touches external rows — so this is the only path that can reach it. Audio
    /// only, since a subtitle sidecar has no bitrate to find and would be re-probed on every run forever.
    /// </para>
    /// </summary>
    private async Task<int> BackfillSidecarSpecsAsync(Guid catalogId, CancellationToken cancellationToken)
    {
        // Projected to plain values rather than entities: `AsNoTracking()` on any source sets the whole
        // query's tracking behavior, so a joined entity here would come back untracked and mutating it would
        // silently save nothing. The writes below are explicit instead.
        var pending = await database.MediaStreams.AsNoTracking()
            .Where(stream => stream.IsExternal && stream.ExternalPath != null &&
                (stream.Codec == null ||
                 (stream.StreamType == StreamType.Audio && stream.Bitrate == null)))
            .Join(database.MediaSources.AsNoTracking(), stream => stream.MediaSourceId, source => source.Id,
                (stream, source) => new { stream.Id, stream.ExternalPath, source.MediaItemId })
            .Join(database.MediaItems.AsNoTracking(), pair => pair.MediaItemId, item => item.Id,
                (pair, item) => new { pair.Id, pair.ExternalPath, item.CatalogId })
            .Where(entry => entry.CatalogId == catalogId)
            .ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return 0;
        }

        var catalog = await database.Catalogs.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == catalogId, cancellationToken);
        if (catalog is null)
        {
            return 0;
        }

        var filled = 0;
        foreach (var entry in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sandbox.TryResolve(catalog, entry.ExternalPath!, out var absolute) || !File.Exists(absolute))
            {
                continue;
            }

            ProbedStream? track;
            try
            {
                track = (await probe.ProbeAsync(absolute, cancellationToken)).Streams.FirstOrDefault();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogDebug(exception, "Could not probe sidecar {Path}.", entry.ExternalPath);
                continue;
            }

            if (track?.Codec is null)
            {
                continue; // Nothing to record — an elementary stream read without the engine, typically.
            }

            // Each field keeps what it has when this probe cannot better it. A row selected for a missing
            // bitrate may already carry engine-read specs, and the provider answering now can be the weaker
            // one — writing its nulls over them would lose information this run never had.
            await database.MediaStreams.Where(stream => stream.Id == entry.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(stream => stream.Codec, track.Codec)
                    .SetProperty(stream => stream.Channels, stream => track.Channels ?? stream.Channels)
                    .SetProperty(stream => stream.SampleRate, stream => track.SampleRate ?? stream.SampleRate)
                    .SetProperty(stream => stream.Bitrate, stream => track.Bitrate ?? stream.Bitrate), cancellationToken);
            filled++;
        }

        return filled;
    }

}

