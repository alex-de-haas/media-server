using MediaServer.Api.Catalogs;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Transcoding;

/// <summary>
/// Operator-facing transcode commands: create (resolve a movie source → engine job + persisted row),
/// list, cancel, remove. Delegates the actual encode to the external transcode-engine app; persists only
/// durable facts and state transitions — live progress stays in the engine. Scoped to movies for now.
/// </summary>
public sealed class TranscodeService(
    MediaServerDbContext database,
    ITranscodeEngine engine,
    ICatalogPathSandbox sandbox,
    MediaServerSettings settings,
    LibraryMoveGuard moveGuard,
    ILogger<TranscodeService> logger)
{
    public async Task<TranscodeJobResponse> CreateAsync(CreateTranscodeRequest request, CancellationToken cancellationToken)
    {
        var source = await database.MediaSources
            .Include(candidate => candidate.MediaItem)
            .ThenInclude(item => item!.Catalog)
            .Include(candidate => candidate.Streams)
            .FirstOrDefaultAsync(candidate => candidate.Id == request.SourceId, cancellationToken)
            ?? throw new TranscodeRequestException("Media source not found.");

        var item = source.MediaItem ?? throw new TranscodeRequestException("Source is not attached to a media item.");
        if (item.Kind != MediaKind.Movie)
        {
            throw new TranscodeRequestException("Only movies can be transcoded for now.");
        }

        // The mirror of the move coordinator's transcode check: a move is relocating this movie's files, so
        // an encode reading them (and writing a sibling into the old catalog) would break both.
        if (await moveGuard.IsItemMovingAsync(item.Id, cancellationToken))
        {
            throw new TranscodeRequestException(LibraryMoveGuard.MoveInProgressError);
        }

        var catalog = item.Catalog ?? throw new TranscodeRequestException("Source's catalog is unavailable.");

        var codec = NormalizeCodec(request.VideoCodec);
        var hardware = NormalizeHardware(request.HardwareAcceleration);
        if (request.Crf is < 0 or > 51)
        {
            throw new TranscodeRequestException("crf must be between 0 and 51.");
        }

        // Resolve track selection and target resolution against the source's probed streams.
        var orderedAudio = source.Streams.Where(stream => stream.StreamType == StreamType.Audio)
            .OrderBy(stream => stream.Index).Select(stream => stream.Index).ToList();
        var orderedSubtitles = source.Streams.Where(stream => stream.StreamType == StreamType.Subtitle)
            .OrderBy(stream => stream.Index).Select(stream => stream.Index).ToList();

        var audioSelection = ResolveSelection(request.AudioStreamIndexes, orderedAudio, "audio");
        var subtitleSelection = ResolveSelection(request.SubtitleStreamIndexes, orderedSubtitles, "subtitle");
        var defaultAudio = ResolveDefault(request.DefaultAudioStreamIndex, ref audioSelection, orderedAudio, "audio");
        var defaultSubtitle = ResolveDefault(request.DefaultSubtitleStreamIndex, ref subtitleSelection, orderedSubtitles, "subtitle");

        // Downscale only — never upscale, and never when remuxing (copy keeps the original picture untouched).
        // Max known video height (ignores a null/cover-art stream that might sort first); null only when no
        // video height is known at all, in which case we don't scale rather than risk an upscale.
        var sourceHeight = source.Streams.Where(stream => stream.StreamType == StreamType.Video)
            .Max(stream => stream.Height);
        int? targetHeight = null;
        if (codec != "copy" && request.MaxHeight is { } requestedHeight)
        {
            if (requestedHeight is < 16 or > 4320)
            {
                throw new TranscodeRequestException("maxHeight must be between 16 and 4320.");
            }

            // Downscale only: apply the target solely when the real source height is known and strictly larger.
            if (sourceHeight is { } known && requestedHeight < known)
            {
                targetHeight = requestedHeight;
            }
        }

        if (!sandbox.TryResolve(catalog, source.Path, out var inputAbsolute) || !File.Exists(inputAbsolute))
        {
            throw new TranscodeRequestException("Source file not found on disk.");
        }

        var outputRelative = BuildOutputRelative(source.Path, VersionLabel(codec, targetHeight));
        if (!sandbox.TryResolve(catalog, outputRelative, out var outputAbsolute))
        {
            throw new TranscodeRequestException("Could not place the output inside the catalog.");
        }

        // A leftover output file from a previously-deleted version must not block re-conversion: only refuse
        // when a live version (MediaSource) still points at this output, or another transcode is already
        // producing it. Any orphan file at this path is overwritten by ffmpeg's -y.
        if (await database.MediaSources.AnyAsync(
                candidate => candidate.MediaItemId == item.Id && candidate.Path == outputRelative, cancellationToken))
        {
            throw new TranscodeRequestException(
                $"This movie already has a version at '{outputRelative}'. Delete that version first, or change the settings.");
        }

        if (await database.TranscodeJobs.AnyAsync(
                candidate => candidate.MediaItemId == item.Id && candidate.OutputPath == outputRelative &&
                    (candidate.State == TranscodeJobState.Queued || candidate.State == TranscodeJobState.Running), cancellationToken))
        {
            throw new TranscodeRequestException($"A transcode is already producing '{outputRelative}'.");
        }

        var input = ToMount(inputAbsolute)
            ?? throw new TranscodeRequestException("The catalog root is not bound as a media mount; transcoding needs the same host path bound into the transcode-engine.");
        var output = ToMount(outputAbsolute)
            ?? throw new TranscodeRequestException("The output path is not under a configured media mount.");

        JobDescriptor descriptor;
        try
        {
            descriptor = await engine.CreateAsync(
                new TranscodeJobRequest(
                    input.Label, input.Relative, output.Label, output.Relative, codec, hardware, request.Crf,
                    targetHeight, audioSelection, subtitleSelection, defaultAudio, defaultSubtitle),
                cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            // The engine is disabled (no dependency) or rejected the job — surface it as a 400.
            throw new TranscodeRequestException(exception.Message);
        }

        var job = new TranscodeJob
        {
            Id = Guid.NewGuid(),
            EngineJobId = descriptor.JobId,
            MediaSourceId = source.Id,
            MediaItemId = item.Id,
            CatalogId = catalog.Id,
            Name = Path.GetFileName(outputRelative),
            InputPath = source.Path,
            OutputPath = outputRelative,
            VideoCodec = codec,
            HardwareAcceleration = hardware,
            Crf = request.Crf,
            State = TranscodeJobState.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        database.TranscodeJobs.Add(job);
        await database.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Created transcode job {JobId} for source {SourceId} → {Output}.", descriptor.JobId, source.Id, outputRelative);
        return TranscodeJobResponse.From(job, engine.GetSnapshot(descriptor.JobId));
    }

    public async Task<IReadOnlyList<TranscodeJobResponse>> ListAsync(CancellationToken cancellationToken)
    {
        var jobs = await database.TranscodeJobs
            .AsNoTracking()
            .OrderByDescending(job => job.CreatedAt)
            .ToListAsync(cancellationToken);

        return jobs.Select(job => TranscodeJobResponse.From(job, engine.GetSnapshot(job.EngineJobId))).ToList();
    }

    public async Task<bool> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        var job = await database.TranscodeJobs.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (job is null)
        {
            return false;
        }

        await engine.CancelAsync(job.EngineJobId, cancellationToken);

        if (job.State is TranscodeJobState.Queued or TranscodeJobState.Running)
        {
            job.State = TranscodeJobState.Cancelled;
            job.CompletedAt = DateTimeOffset.UtcNow;
            await database.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    public async Task<bool> RemoveAsync(Guid id, bool deleteOutput, CancellationToken cancellationToken)
    {
        var job = await database.TranscodeJobs.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (job is null)
        {
            return false;
        }

        var engineJobId = job.EngineJobId;
        database.TranscodeJobs.Remove(job);
        await database.SaveChangesAsync(cancellationToken);

        // Best-effort engine/file cleanup AFTER the row is gone, so a transient engine failure can't roll
        // back (or block) the removal the operator asked for.
        try
        {
            await engine.RemoveAsync(engineJobId, deleteOutput, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Removed transcode job {JobId} but the engine cleanup failed.", id);
        }

        return true;
    }

    /// <summary>Output is a sibling of the input with a descriptive version suffix (codec + resolution, or
    /// "Remux"), always in a Matroska (.mkv) container — the universal carrier that keeps every audio track,
    /// subtitle, attachment and HDR layer. The suffix captures the codec and target resolution; conversions
    /// that share those but differ only in track selection still land on the same path, which the caller's
    /// existing "a version already lives here" check rejects. Operates on the catalog-root-relative (posix)
    /// path.</summary>
    internal static string BuildOutputRelative(string sourceRelative, string label)
    {
        var slashed = sourceRelative.Replace('\\', '/');
        var lastSlash = slashed.LastIndexOf('/');
        var directory = lastSlash >= 0 ? slashed[..lastSlash] : string.Empty;
        var file = lastSlash >= 0 ? slashed[(lastSlash + 1)..] : slashed;

        var dot = file.LastIndexOf('.');
        var stem = dot >= 0 ? file[..dot] : file;

        var name = $"{stem} - {label}.mkv";
        return directory.Length > 0 ? $"{directory}/{name}" : name;
    }

    /// <summary>The version label used for the output filename: "Remux" for a video copy, otherwise the codec
    /// plus the target height when downscaling (e.g. "HEVC 1080p") or just the codec at full resolution.</summary>
    internal static string VersionLabel(string codec, int? targetHeight) =>
        codec == "copy"
            ? "Remux"
            : targetHeight is { } height ? $"{CodecLabel(codec)} {height}p" : CodecLabel(codec);

    private static string CodecLabel(string codec) => codec == "h264" ? "H.264" : "HEVC";

    /// <summary>Validates a requested per-type stream selection against the source's streams. Null = copy all
    /// (left to the engine's default). An explicit list must reference real streams of that type; order is
    /// preserved and duplicates dropped. An empty list drops every stream of that type.</summary>
    private static IReadOnlyList<int>? ResolveSelection(IReadOnlyList<int>? requested, IReadOnlyList<int> available, string kind)
    {
        if (requested is null)
        {
            return null;
        }

        var valid = available.ToHashSet();
        var result = new List<int>();
        foreach (var index in requested)
        {
            if (!valid.Contains(index))
            {
                throw new TranscodeRequestException($"Stream {index} is not a {kind} track of this source.");
            }

            if (!result.Contains(index))
            {
                result.Add(index);
            }
        }

        return result;
    }

    /// <summary>Resolves the chosen default track. A non-null choice forces an explicit selection (so the
    /// engine can translate the absolute index into an output position) and must be one of the copied tracks.</summary>
    private static int? ResolveDefault(int? requested, ref IReadOnlyList<int>? selection, IReadOnlyList<int> available, string kind)
    {
        if (requested is not { } index)
        {
            return null;
        }

        selection ??= available;
        if (!selection.Contains(index))
        {
            throw new TranscodeRequestException($"The default {kind} track must be one of the copied {kind} tracks.");
        }

        return index;
    }

    /// <summary>Maps an absolute path under a configured catalog mount to that mount's <c>Label</c> plus a
    /// path relative to the mount root, so the engine resolves it against its own media root with the same
    /// label (the same host path). Returns null when no mount contains the path. Mirrors the same mapping
    /// the torrent client does for save directories.</summary>
    private (string? Label, string Relative)? ToMount(string absolutePath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var full = Path.GetFullPath(absolutePath);
        foreach (var mount in settings.CatalogMountRoots)
        {
            var rootFull = Path.GetFullPath(mount.Path);
            // Don't double-append a separator when the mount root already ends with one (e.g. a filesystem
            // root like "/"), which would break the descendant check.
            var rootPrefix = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
            if (string.Equals(full, rootFull, comparison) ||
                full.StartsWith(rootPrefix, comparison))
            {
                var label = string.IsNullOrEmpty(mount.Label) ? null : mount.Label;
                return (label, Path.GetRelativePath(rootFull, full).Replace('\\', '/'));
            }
        }

        return null;
    }

    private static string NormalizeCodec(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        null or "" or "hevc" or "h265" or "x265" => "hevc",
        "h264" or "avc" or "x264" => "h264",
        "copy" or "remux" => "copy",
        _ => throw new TranscodeRequestException($"videoCodec '{raw}' is not supported (use 'h264', 'hevc' or 'copy')."),
    };

    private static string NormalizeHardware(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        null or "" or "auto" => "auto",
        "vaapi" => "vaapi",
        "none" or "software" or "cpu" => "none",
        _ => throw new TranscodeRequestException($"hardwareAcceleration '{raw}' is not supported (use 'auto', 'vaapi' or 'none')."),
    };
}
