using MediaServer.Api.Catalogs;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
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
    ILogger<TranscodeService> logger)
{
    public async Task<TranscodeJobResponse> CreateAsync(CreateTranscodeRequest request, CancellationToken cancellationToken)
    {
        var source = await database.MediaSources
            .Include(candidate => candidate.MediaItem)
            .ThenInclude(item => item!.Catalog)
            .FirstOrDefaultAsync(candidate => candidate.Id == request.SourceId, cancellationToken)
            ?? throw new TranscodeRequestException("Media source not found.");

        var item = source.MediaItem ?? throw new TranscodeRequestException("Source is not attached to a media item.");
        if (item.Kind != MediaKind.Movie)
        {
            throw new TranscodeRequestException("Only movies can be transcoded for now.");
        }

        var catalog = item.Catalog ?? throw new TranscodeRequestException("Source's catalog is unavailable.");

        var codec = NormalizeCodec(request.VideoCodec);
        var hardware = NormalizeHardware(request.HardwareAcceleration);
        if (request.Crf is < 0 or > 51)
        {
            throw new TranscodeRequestException("crf must be between 0 and 51.");
        }

        if (!sandbox.TryResolve(catalog, source.Path, out var inputAbsolute) || !File.Exists(inputAbsolute))
        {
            throw new TranscodeRequestException("Source file not found on disk.");
        }

        var outputRelative = BuildOutputRelative(source.Path, codec);
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
                $"This movie already has a version at '{outputRelative}'. Delete that version first, or choose a different codec.");
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
                new TranscodeJobRequest(input.Label, input.Relative, output.Label, output.Relative, codec, hardware, request.Crf),
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

        await engine.RemoveAsync(job.EngineJobId, deleteOutput, cancellationToken);
        database.TranscodeJobs.Remove(job);
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Output is a sibling of the input with a codec suffix and the same container, so it lands in
    /// the same movie folder and can become a new version of the same item (verified before replacing the
    /// original). Operates on the catalog-root-relative (posix) path.</summary>
    internal static string BuildOutputRelative(string sourceRelative, string codec)
    {
        var slashed = sourceRelative.Replace('\\', '/');
        var lastSlash = slashed.LastIndexOf('/');
        var directory = lastSlash >= 0 ? slashed[..lastSlash] : string.Empty;
        var file = lastSlash >= 0 ? slashed[(lastSlash + 1)..] : slashed;

        var dot = file.LastIndexOf('.');
        var stem = dot >= 0 ? file[..dot] : file;
        var extension = dot >= 0 ? file[dot..] : string.Empty;

        var name = $"{stem} - {codec.ToUpperInvariant()}{extension}";
        return directory.Length > 0 ? $"{directory}/{name}" : name;
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
            if (string.Equals(full, rootFull, comparison) ||
                full.StartsWith(rootFull + Path.DirectorySeparatorChar, comparison))
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
        _ => throw new TranscodeRequestException($"videoCodec '{raw}' is not supported (use 'h264' or 'hevc')."),
    };

    private static string NormalizeHardware(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        null or "" or "auto" => "auto",
        "vaapi" => "vaapi",
        "none" or "software" or "cpu" => "none",
        _ => throw new TranscodeRequestException($"hardwareAcceleration '{raw}' is not supported (use 'auto', 'vaapi' or 'none')."),
    };
}
