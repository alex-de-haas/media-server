using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.IO;
using MediaServer.Api.Pipeline;
using MediaServer.Api.Probe;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Library;

/// <summary>One published copy of a title, as reported by the cross-catalog duplicate audit.</summary>
public sealed record DuplicateCopy(Guid MediaItemId, Guid CatalogId, string CatalogName);

/// <summary>
/// A work published in more than one catalog — the shape the single-catalog rule forbids going forward,
/// and which only pre-dates the gate. Its copies keep separate watched state and favorites, so the
/// repair is to move one onto the other (which merges them into versions of a single item).
/// </summary>
public sealed record CrossCatalogDuplicate(string Kind, string Title, int? Year, IReadOnlyList<DuplicateCopy> Copies);

/// <summary>
/// Result of a library scan pass: how much was checked, which library files are missing on disk, and any
/// title published in two catalogs at once.
/// </summary>
public sealed record LibraryScanReport(
    int CatalogsScanned,
    int SourcesChecked,
    int MissingFiles,
    IReadOnlyList<string> MissingPaths,
    IReadOnlyList<CrossCatalogDuplicate> CrossCatalogDuplicates);

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
/// M4 automation polish: on-demand and scheduled library maintenance. The scan verifies every published
/// <see cref="MediaSource"/> still resolves to a file on disk (drift from out-of-band deletes), skipping
/// offline catalogs so an unmounted volume isn't reported as missing media. Metadata refresh re-runs the
/// idempotent enrich step for a single item to pull fresh provider data and images.
/// </summary>
public sealed class LibraryMaintenanceService(
    MediaServerDbContext database,
    ICatalogPathSandbox sandbox,
    IFilesystemInspector filesystem,
    IMediaProbe probe,
    EnrichService enrichService,
    IHostyCoreClient core,
    ILogger<LibraryMaintenanceService> logger)
{
    private const int MaxMissingReported = 50;

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
                    Channels = stream.Channels,
                    SampleRate = stream.SampleRate,
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
    /// <para>
    /// Deliberately an explicit action rather than something that fires when the dependency reconnects: a
    /// probe is fast, so a whole-library pass is a foreground operation an operator can simply run, and
    /// rewriting stored data on its own the moment a dependency reappears would be a surprise.
    /// </para>
    /// </summary>
    public async Task<MediaBackfillReport> BackfillHeaderProbedAsync(CancellationToken cancellationToken)
    {
        var itemIds = await database.MediaSources.AsNoTracking()
            .Where(source => source.ProbeSource == ProbeSource.Header)
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
        var sidecarsFilled = await BackfillSidecarSpecsAsync(cancellationToken);

        var remaining = await database.MediaSources.AsNoTracking()
            .CountAsync(source => source.ProbeSource == ProbeSource.Header, cancellationToken);

        logger.LogInformation(
            "Media backfill finished: {Refreshed} item(s) refreshed, {Sidecars} sidecar(s) filled, {Remaining} source(s) still without engine data.",
            refreshed, sidecarsFilled, remaining);
        return new MediaBackfillReport(refreshed, remaining, sidecarsFilled);
    }

    /// <summary>
    /// Reads codec, channel count and sample rate into the sidecar rows that lack them — the ones placed
    /// before those were recorded, and any whose file the engine could not answer for at the time.
    /// <para>
    /// Only the technical fields are written. Language and title are a <b>labelling decision</b> the sidecar
    /// stage made across a whole cohort of files, weighing what the container tagged against what the paths
    /// reveal; re-reading one file's tags here would undo that with strictly less information.
    /// </para>
    /// <para>
    /// A missing codec is the marker for "never answered", so a file the engine still cannot read is simply
    /// picked up again next run rather than being recorded as having no codec.
    /// </para>
    /// </summary>
    private async Task<int> BackfillSidecarSpecsAsync(CancellationToken cancellationToken)
    {
        // Projected to plain values rather than entities: `AsNoTracking()` on any source sets the whole
        // query's tracking behavior, so a joined entity here would come back untracked and mutating it would
        // silently save nothing. The writes below are explicit instead.
        var pending = await database.MediaStreams.AsNoTracking()
            .Where(stream => stream.IsExternal && stream.Codec == null && stream.ExternalPath != null)
            .Join(database.MediaSources.AsNoTracking(), stream => stream.MediaSourceId, source => source.Id,
                (stream, source) => new { stream.Id, stream.ExternalPath, source.MediaItemId })
            .Join(database.MediaItems.AsNoTracking(), pair => pair.MediaItemId, item => item.Id,
                (pair, item) => new { pair.Id, pair.ExternalPath, item.CatalogId })
            .ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return 0;
        }

        var catalogIds = pending.Where(entry => entry.CatalogId != null)
            .Select(entry => entry.CatalogId!.Value).Distinct().ToList();
        var catalogs = await database.Catalogs.AsNoTracking()
            .Where(catalog => catalogIds.Contains(catalog.Id))
            .ToDictionaryAsync(catalog => catalog.Id, cancellationToken);

        var filled = 0;
        foreach (var entry in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.CatalogId is not { } catalogId || !catalogs.TryGetValue(catalogId, out var catalog))
            {
                continue;
            }

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

            await database.MediaStreams.Where(stream => stream.Id == entry.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(stream => stream.Codec, track.Codec)
                    .SetProperty(stream => stream.Channels, track.Channels)
                    .SetProperty(stream => stream.SampleRate, track.SampleRate), cancellationToken);
            filled++;
        }

        return filled;
    }

    public async Task<LibraryScanReport> ScanAsync(CancellationToken cancellationToken)
    {
        var catalogsById = await database.Catalogs.AsNoTracking().ToDictionaryAsync(catalog => catalog.Id, cancellationToken);
        var sources = await database.MediaSources.AsNoTracking()
            .Include(source => source.MediaItem)
            .ToListAsync(cancellationToken);

        var scannedCatalogs = new HashSet<Guid>();
        var checkedCount = 0;
        var missing = new List<string>();

        foreach (var source in sources)
        {
            if (source.MediaItem?.CatalogId is not { } sourceCatalogId || !catalogsById.TryGetValue(sourceCatalogId, out var catalog))
            {
                continue;
            }

            // An offline root means the volume is unmounted, not that the media vanished — don't scan it.
            if (!filesystem.DirectoryExists(catalog.Root))
            {
                continue;
            }

            scannedCatalogs.Add(catalog.Id);
            checkedCount++;

            var exists = sandbox.TryResolve(catalog, source.Path, out var absolute) && File.Exists(absolute);
            if (!exists)
            {
                missing.Add(source.Path);
            }
        }

        if (missing.Count > 0)
        {
            logger.LogWarning("Library scan found {Count} missing source file(s).", missing.Count);
            await core.PublishNotificationAsync(
                CoreNotificationLevel.Warning,
                "Media Server: missing library files",
                $"{missing.Count} library file(s) are missing from disk. The affected items may not play until they are re-downloaded.",
                link: null,
                dedupeKey: "media-server:library-missing",
                cancellationToken: cancellationToken);
        }

        var duplicates = await FindCrossCatalogDuplicatesAsync(cancellationToken);
        if (duplicates.Count > 0)
        {
            logger.LogWarning("Library scan found {Count} title(s) published in more than one catalog.", duplicates.Count);
        }

        return new LibraryScanReport(
            scannedCatalogs.Count, checkedCount, missing.Count, missing.Take(MaxMissingReported).ToList(), duplicates);
    }

    /// <summary>
    /// Titles published in more than one catalog. Identification now refuses to create these (see
    /// <c>IdentifyService</c>), so anything reported here pre-dates the gate and is repaired by moving one
    /// copy onto the other. Published rows only — a tombstone beside a live copy is the adoption path, not
    /// a duplicate.
    /// </summary>
    public async Task<IReadOnlyList<CrossCatalogDuplicate>> FindCrossCatalogDuplicatesAsync(CancellationToken cancellationToken)
    {
        var copies = await database.MediaItems.AsNoTracking()
            .Where(item => item.PublicId != null && item.CatalogId != null &&
                (item.Kind == MediaKind.Movie || item.Kind == MediaKind.Series) &&
                item.IdentityProvider != null && item.IdentityProviderId != null)
            .Join(database.Catalogs.AsNoTracking(), item => item.CatalogId, catalog => (Guid?)catalog.Id,
                (item, catalog) => new
                {
                    item.Id,
                    item.Kind,
                    item.Title,
                    item.Year,
                    item.IdentityProvider,
                    item.IdentityProviderId,
                    CatalogId = catalog.Id,
                    CatalogName = catalog.Name,
                })
            .ToListAsync(cancellationToken);

        return copies
            .GroupBy(copy => (copy.Kind, copy.IdentityProvider, copy.IdentityProviderId))
            .Where(group => group.Select(copy => copy.CatalogId).Distinct().Count() > 1)
            .Select(group =>
            {
                var first = group.First();
                return new CrossCatalogDuplicate(
                    first.Kind.ToString(),
                    first.Title,
                    first.Year,
                    group.Select(copy => new DuplicateCopy(copy.Id, copy.CatalogId, copy.CatalogName)).ToList());
            })
            .OrderBy(duplicate => duplicate.Title)
            .ToList();
    }
}

/// <summary>Runs <see cref="LibraryMaintenanceService.ScanAsync"/> on a timer to catch out-of-band file drift.</summary>
public sealed class LibraryScanWorker(IServiceScopeFactory scopeFactory, ILogger<LibraryScanWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
            await RunOnceAsync(stoppingToken);

            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<LibraryMaintenanceService>();
            var report = await service.ScanAsync(cancellationToken);
            logger.LogInformation(
                "Library scan: {Sources} source(s) across {Catalogs} catalog(s), {Missing} missing.",
                report.SourcesChecked, report.CatalogsScanned, report.MissingFiles);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Scheduled library scan failed.");
        }
    }
}
