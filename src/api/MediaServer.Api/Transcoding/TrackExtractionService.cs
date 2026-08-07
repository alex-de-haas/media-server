using MediaServer.Api.Catalogs;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Sidecars;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Transcoding;

/// <summary>
/// Writes chosen tracks of a movie source out as files beside it — the inverse of merging, and the operation
/// that turns a container's own track into a sidecar.
/// <para>
/// The result is deliberately indistinguishable from a sidecar a release shipped: same naming rule, same
/// external <see cref="MediaStream"/> rows, same external indexes. Everything
/// <c>external-track-sidecars</c> already does — listing, removal, merging back, the specs backfill, going
/// with the video when it is deleted — then applies with nothing written for it.
/// </para>
/// <para>
/// The source container is never rewritten, so a track exists in both places afterwards. Dropping one from
/// the container is a conversion, composed in the convert dialog.
/// </para>
/// </summary>
public sealed class TrackExtractionService(
    MediaServerDbContext database,
    ITranscodeEngine engine,
    ICatalogPathSandbox sandbox,
    MediaServerSettings settings,
    LibraryMoveGuard moveGuard,
    ILogger<TrackExtractionService> logger)
{
    public async Task<TranscodeJobResponse> CreateAsync(CreateExtractionRequest request, CancellationToken cancellationToken)
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
            throw new TranscodeRequestException("Only movies can have their tracks extracted for now.");
        }

        // The same guard a conversion carries: a move is relocating this movie's files, so reading them and
        // writing sidecars into the old catalog would break both.
        if (await moveGuard.IsItemMovingAsync(item.Id, cancellationToken))
        {
            throw new TranscodeConflictException(LibraryMoveGuard.MoveInProgressError);
        }

        var catalog = item.Catalog ?? throw new TranscodeRequestException("Source's catalog is unavailable.");

        var targets = ResolveTargets(request, source);

        if (!sandbox.TryResolve(catalog, source.Path, out var inputAbsolute) || !File.Exists(inputAbsolute))
        {
            throw new TranscodeRequestException("Source file not found on disk.");
        }

        var input = ToMount(inputAbsolute)
            ?? throw new TranscodeRequestException(
                "The catalog root is not bound as a media mount; extracting needs the same host path bound into the transcode-engine.");

        var planned = PlanOutputs(source, targets, NamesBesideTheVideo(catalog, source));
        await GuardAgainstDuplicatesAsync(source, item, planned, cancellationToken);

        var outputs = new List<EngineExtractionOutput>(planned.Count);
        foreach (var output in planned)
        {
            if (!sandbox.TryResolve(catalog, output.RelativePath, out var absolute))
            {
                throw new TranscodeRequestException("Could not place an extracted track inside the catalog.");
            }

            var mount = ToMount(absolute)
                ?? throw new TranscodeRequestException("An extracted track would land outside every configured media mount.");

            outputs.Add(new EngineExtractionOutput(
                mount.Label, mount.Relative, output.SourceStreamIndex, output.Codec, output.Language, output.Title));
        }

        JobDescriptor descriptor;
        try
        {
            descriptor = await engine.CreateAsync(
                new TranscodeJobRequest(
                    input.Label, input.Relative, OutputMountLabel: null, OutputRelativePath: null,
                    // Structurally required by the request record and meaningless here: an extraction encodes
                    // nothing, and the engine refuses a job that says otherwise.
                    VideoCodec: "copy", HardwareAcceleration: "auto", QualityLevel: null,
                    Outputs: outputs),
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
            Kind = TranscodeJobKind.Extract,
            EngineJobId = descriptor.JobId,
            MediaSourceId = source.Id,
            MediaItemId = item.Id,
            CatalogId = catalog.Id,
            // No single output represents the job, so it is named for what it reads — the same answer the
            // engine's own snapshot gives.
            Name = Path.GetFileName(source.Path),
            InputPath = source.Path,
            OutputPath = null,
            VideoCodec = "copy",
            HardwareAcceleration = "none",
            QualityLevel = null,
            ReEncodedAudioTracks = 0,
            State = TranscodeJobState.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
            Outputs = planned
                .Select(output => new TranscodeJobOutput
                {
                    Id = Guid.NewGuid(),
                    SourceStreamIndex = output.SourceStreamIndex,
                    RelativePath = output.RelativePath,
                    StreamType = output.StreamType,
                    Language = output.Language,
                    Title = output.Title,
                })
                .ToList(),
        };
        database.TranscodeJobs.Add(job);
        await database.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Created extraction job {JobId} for source {SourceId}: {Count} track(s).",
            descriptor.JobId, source.Id, planned.Count);
        return TranscodeJobResponse.From(job, engine.GetSnapshot(descriptor.JobId));
    }

    /// <summary>
    /// The tracks this job extracts, in container order, each with the file it will become. Ordered by index
    /// rather than by the order they were named, so the position fallback in the naming rule is stable
    /// whatever order a client sends its selection in.
    /// </summary>
    internal static IReadOnlyList<ExtractionTarget> ResolveTargets(CreateExtractionRequest request, MediaSource source)
    {
        if (request.StreamIds is not { Count: > 0 })
        {
            throw new TranscodeRequestException("Name at least one track to extract.");
        }

        var targets = new List<ExtractionTarget>();
        foreach (var id in request.StreamIds.Distinct())
        {
            var stream = source.Streams.FirstOrDefault(candidate => candidate.Id == id && !candidate.IsExternal)
                ?? throw new TranscodeRequestException(
                    "A selected track is not a track of this version. A track that is already a file beside it cannot be extracted again.");

            targets.Add(ExtractionTarget.For(stream));
        }

        return targets.OrderBy(target => target.Stream.Index).ToList();
    }

    /// <summary>
    /// Names each extracted track under the sidecar convention and places it beside the video.
    /// <para>
    /// The sidecars already there are handed to the naming rule as well: a dub extracted next to one of the
    /// same kind and language would otherwise be given a numeric suffix while its own label went unused, and
    /// could be handed a name that is already taken.
    /// </para>
    /// </summary>
    private static IReadOnlyList<PlannedOutput> PlanOutputs(
        MediaSource source, IReadOnlyList<ExtractionTarget> targets, IReadOnlyList<string> reserved)
    {
        var placed = source.Streams
            .Where(stream => stream.IsExternal && stream.ExternalPath is { Length: > 0 })
            .Select(stream => new PlacedSidecar(
                Path.GetFileName(stream.ExternalPath!), stream.StreamType == StreamType.Audio, stream.Language))
            .ToList();

        var candidates = targets
            .Select(target => new SidecarCandidate(
                target.Stream.Id,
                target.Extension,
                target.Stream.StreamType == StreamType.Audio,
                target.Stream.Language,
                target.Stream.Title))
            .ToList();

        var slashed = source.Path.Replace('\\', '/');
        var lastSlash = slashed.LastIndexOf('/');
        var folder = lastSlash >= 0 ? slashed[..lastSlash] : string.Empty;

        var planned = new List<PlannedOutput>(targets.Count);
        foreach (var named in SidecarNaming.For(Path.GetFileName(source.Path), candidates, placed, reserved))
        {
            var target = targets.First(entry => entry.Stream.Id == named.Id);
            planned.Add(new PlannedOutput(
                target.Stream.Index,
                folder.Length == 0 ? named.FileName : $"{folder}/{named.FileName}",
                target.Stream.StreamType,
                target.Codec,
                target.Stream.Language,
                target.Stream.Title));
        }

        return planned;
    }

    /// <summary>
    /// Refuses a track that is already out, and a second job producing a file another is already writing.
    /// Re-extracting would put the same track beside the video twice under two names, which nothing would
    /// ever clean up.
    /// </summary>
    private async Task GuardAgainstDuplicatesAsync(
        MediaSource source, MediaItem item, IReadOnlyList<PlannedOutput> planned, CancellationToken cancellationToken)
    {
        // An extracted row records the track it came out of, so "already out" is a property of the row and
        // not of the job that wrote it. Reading the job history instead would forget a track whose job was
        // dismissed (which cascades its outputs away) or whose import only partly succeeded (which leaves
        // the job Failed with its sidecars still on disk) — and a forgotten track is a second copy of it
        // under a different name.
        //
        // Removing the sidecar makes the track extractable again, which is the point: an operator who
        // deleted it is asking for exactly that.
        foreach (var output in planned)
        {
            var previous = source.Streams.FirstOrDefault(stream =>
                stream.IsExternal && stream.SourceStreamIndex == output.SourceStreamIndex);
            if (previous is not null)
            {
                throw new TranscodeRequestException(
                    $"That track is already a file beside this version ('{Path.GetFileName(previous.ExternalPath) ?? "unnamed"}'). Remove that file first to extract it again.");
            }
        }

        var paths = planned.Select(output => output.RelativePath).ToHashSet(StringComparer.Ordinal);
        var contested = await database.TranscodeJobOutputs
            .Where(output => output.TranscodeJob!.MediaItemId == item.Id &&
                (output.TranscodeJob.State == TranscodeJobState.Queued ||
                    output.TranscodeJob.State == TranscodeJobState.Running))
            .Select(output => output.RelativePath)
            .ToListAsync(cancellationToken);

        if (contested.FirstOrDefault(paths.Contains) is { } clash)
        {
            throw new TranscodeRequestException($"An extraction is already producing '{Path.GetFileName(clash)}'.");
        }
    }

    /// <summary>
    /// Every file already sitting in the video's folder, so the naming rule cannot hand one of those names
    /// out again.
    /// <para>
    /// The rows are not enough. A sidecar's entry can be dropped while its file is kept — a supported choice
    /// on the removal control — and an operator can copy a subtitle in by hand; either leaves a file the
    /// database knows nothing about. The engine writes its outputs with ffmpeg's <c>-y</c>, which is right
    /// for a conversion (the operator named that exact path) and wrong here, where the name is generated:
    /// it would silently replace a retained or hand-edited subtitle.
    /// </para>
    /// <para>
    /// A folder this cannot read yields nothing, which is the pre-existing behaviour rather than a refusal —
    /// the same folder is about to be written into, so a real problem surfaces there with a better message.
    /// </para>
    /// </summary>
    private IReadOnlyList<string> NamesBesideTheVideo(Catalog catalog, MediaSource source)
    {
        try
        {
            if (!sandbox.TryResolve(catalog, source.Path, out var absolute))
            {
                return [];
            }

            var folder = Path.GetDirectoryName(absolute);
            return folder is not null && Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>().ToList()
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not list the folder beside {Path}; extracted names may collide.", source.Path);
            return [];
        }
    }

    private (string? Label, string Relative)? ToMount(string absolutePath) =>
        CatalogMounts.TryResolve(settings, absolutePath, out var label, out var relative)
            ? (label, relative)
            : null;

    /// <summary>One extracted track's destination, before it is named.</summary>
    /// <param name="Codec">Null for a stream copy — every case but a subtitle format with no file form.</param>
    internal sealed record ExtractionTarget(MediaStream Stream, string Extension, string? Codec)
    {
        /// <summary>
        /// Which file a track becomes.
        /// <para>
        /// <b>Audio is always Matroska.</b> A <c>.mka</c> carries its own language and title, which is the
        /// property the sidecar feature values it for — it is why a tagged container never needs its name to
        /// be authoritative, and why <c>AudioTrackLabeler</c> exists only for the elementary streams that
        /// have nowhere to put one. Writing a raw <c>.ac3</c> here would manufacture that problem on purpose.
        /// </para>
        /// <para>
        /// <b>Subtitles keep their own text format</b>, which is what clients read from disk. A picture-based
        /// subtitle is refused: it already reaches the viewer by direct play from the container, no client
        /// reads it better as a file, and turning one into text is OCR.
        /// </para>
        /// </summary>
        public static ExtractionTarget For(MediaStream stream)
        {
            if (stream.StreamType == StreamType.Audio)
            {
                return new ExtractionTarget(stream, ".mka", null);
            }

            if (stream.StreamType != StreamType.Subtitle)
            {
                throw new TranscodeRequestException("Only audio and subtitle tracks can be extracted.");
            }

            return stream.Codec?.Trim().ToLowerInvariant() switch
            {
                "subrip" or "srt" => new ExtractionTarget(stream, ".srt", null),
                "ass" or "ssa" => new ExtractionTarget(stream, ".ass", null),
                "webvtt" or "vtt" => new ExtractionTarget(stream, ".vtt", null),
                // The one conversion: 3GPP timed text has no file form of its own, so it cannot be extracted
                // at all without becoming one. Text to text, and UTF-8 on both sides.
                "mov_text" or "tx3g" => new ExtractionTarget(stream, ".srt", "srt"),
                "hdmv_pgs_subtitle" or "pgssub" or "dvd_subtitle" or "dvdsub" or "dvb_subtitle" or "xsub" =>
                    throw new TranscodeRequestException(
                        $"'{Name(stream)}' is a picture-based subtitle. It already plays from the container, and it cannot be written as a text file."),
                null or "" => throw new TranscodeRequestException(
                    $"'{Name(stream)}' has no known codec, so there is no telling what file it should become. Refresh this item's media data and try again."),
                var codec => throw new TranscodeRequestException(
                    $"Subtitles in '{codec}' cannot be extracted to a file."),
            };
        }

        /// <summary>Names a track in a refusal the way an operator sees it in the Media tab — its title, its
        /// language, or its position when it has neither.</summary>
        private static string Name(MediaStream stream) =>
            stream.Title is { Length: > 0 } title ? title
            : stream.Language is { Length: > 0 } language ? language
            : $"track {stream.Index}";
    }

    /// <summary>One extracted track's file, named and placed.</summary>
    private sealed record PlannedOutput(
        int SourceStreamIndex,
        string RelativePath,
        StreamType StreamType,
        string? Codec,
        string? Language,
        string? Title);
}
