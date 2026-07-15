using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Media;
using MediaServer.Api.Probe;

namespace MediaServer.Api.Mux;

/// <summary>
/// Merges matched external audio tracks into their video files while everything still sits in staging,
/// before Organize — so the canonical library file lands complete, with every dub inside it, and playback
/// clients that direct-play a single container (Infuse) see all tracks. For each media item that has both
/// a video and Confirmed companion-audio source files, the audio streams are appended by a stream-copy
/// remux into Matroska (the staged video's extension becomes <c>.mkv</c>), with per-stream language/title
/// taken from the track's own tags or inferred from its path. Consumed audio rows flip to
/// <see cref="SourceFileAssignmentStatus.Merged"/> — persisted per item, so a re-driven stage never appends
/// the same tracks twice — and the freed audio files are swept later with the staging leftovers.
/// Failures propagate: the orchestrator parks the item as a retryable failure with the ffmpeg error.
/// </summary>
public sealed class AudioMuxService(
    MediaServerDbContext database,
    ICatalogPathSandbox sandbox,
    IMediaProbe probe,
    IAudioMuxer muxer,
    ILogger<AudioMuxService> logger)
{
    // Path equality follows the filesystem, same as the organizer's move guard.
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public async Task MuxAsync(IReadOnlyList<SourceFile> sourceFiles, Catalog catalog, CancellationToken cancellationToken)
    {
        var groups = sourceFiles
            .Where(file => file.MediaItemId is not null)
            .GroupBy(file => file.MediaItemId!.Value);

        foreach (var group in groups)
        {
            var audios = group
                .Where(file => file.AssignmentStatus == SourceFileAssignmentStatus.Confirmed &&
                    MediaFormats.IsCompanionAudio(file.RelativePath))
                .OrderBy(file => file.TorrentFileIndex ?? int.MaxValue)
                .ThenBy(file => file.RelativePath, StringComparer.Ordinal)
                .ToList();
            if (audios.Count == 0)
            {
                continue;
            }

            var videos = group
                .Where(file => MediaFormats.IsPlayableMedia(file.RelativePath, file.SizeBytes))
                .ToList();
            if (videos.Count == 0)
            {
                // E.g. a dub-only torrent whose tracks the operator matched to episodes that have no video
                // in this batch. Merging into already-published library files is a separate feature.
                logger.LogWarning(
                    "Audio tracks for item {MediaItem} have no video file in this ingest; they will be discarded with the staging leftovers.",
                    group.Key);
                continue;
            }

            // Only staged (torrent) files are muxed: a scan-imported file is the operator's own library
            // file, which the pipeline never rewrites.
            if (group.Any(file => !CatalogPaths.IsIncoming(file.RelativePath)))
            {
                logger.LogWarning(
                    "Skipping audio mux for item {MediaItem}: its files are not in staging (scan import).", group.Key);
                continue;
            }

            var inputs = await BuildInputsAsync(catalog, audios, videos[0].RelativePath, cancellationToken);
            if (inputs.Count == 0)
            {
                continue;
            }

            var muxedAny = false;
            var replacedOriginals = new List<string>();
            foreach (var video in videos)
            {
                var (muxed, replacedOriginal) = await MuxOneAsync(catalog, video, inputs, cancellationToken);
                muxedAny |= muxed;
                if (replacedOriginal is not null)
                {
                    replacedOriginals.Add(replacedOriginal);
                }
            }

            // The tracks are consumed only once a mux actually happened; otherwise leave them Confirmed so
            // the warning above (plus the staging sweep) is the whole story.
            if (muxedAny)
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var audio in audios)
                {
                    audio.AssignmentStatus = SourceFileAssignmentStatus.Merged;
                    audio.UpdatedAt = now;
                }
            }

            await database.SaveChangesAsync(cancellationToken);

            // Replaced originals (the extension-change path) are removed only after the rows land: had the
            // save failed, the rows still point at an original that still exists, so a re-drive re-muxes
            // cleanly instead of finding its video gone. Deleting is best-effort — a leftover original is
            // swept with the staging folder anyway.
            foreach (var original in replacedOriginals)
            {
                try
                {
                    File.Delete(original);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(exception, "Failed to remove the replaced original after mux: {Path}", original);
                }
            }
        }
    }

    /// <summary>Probes each track and pairs it with the tags to write: the stream's own language/title win,
    /// then the path-inferred fallbacks. Missing/empty tracks are dropped with a warning.</summary>
    private async Task<IReadOnlyList<AudioMuxInput>> BuildInputsAsync(
        Catalog catalog, IReadOnlyList<SourceFile> audios, string videoRelativePath, CancellationToken cancellationToken)
    {
        var inputs = new List<AudioMuxInput>();
        foreach (var audio in audios)
        {
            if (!sandbox.TryResolve(catalog, audio.RelativePath, out var absolute) || !File.Exists(absolute))
            {
                logger.LogWarning("Audio track missing on disk, not muxed: {Path}", audio.RelativePath);
                continue;
            }

            var result = await probe.ProbeAsync(absolute, cancellationToken);
            var streams = result.Streams.Where(stream => stream.Type == StreamType.Audio).ToList();
            if (streams.Count == 0)
            {
                logger.LogWarning("File contains no audio streams, not muxed: {Path}", audio.RelativePath);
                continue;
            }

            var language = AudioTrackLabeler.InferLanguage(audio.RelativePath);
            var title = AudioTrackLabeler.InferTitle(audio.RelativePath, videoRelativePath);
            inputs.Add(new AudioMuxInput(
                absolute,
                streams.Select(stream => new AudioMuxStreamTag(stream.Language ?? language, stream.Title ?? title)).ToList()));
        }

        return inputs;
    }

    /// <summary>
    /// Muxes one video: writes to a temp sibling, then takes over the target path (the extension becomes
    /// <c>.mkv</c>) and updates the row. A differently-named original is not deleted here — it comes back
    /// as <c>ReplacedOriginal</c> for the caller to remove after the rows are saved, so a failed save can't
    /// leave the database pointing at a file that is gone. <c>Muxed</c> is false when the video is skipped —
    /// missing on disk, or a different file already owns the target <c>.mkv</c> path.
    /// </summary>
    private async Task<(bool Muxed, string? ReplacedOriginal)> MuxOneAsync(
        Catalog catalog, SourceFile video, IReadOnlyList<AudioMuxInput> inputs, CancellationToken cancellationToken)
    {
        if (!sandbox.TryResolve(catalog, video.RelativePath, out var videoAbsolute) || !File.Exists(videoAbsolute))
        {
            logger.LogWarning("Video missing on disk, audio not muxed: {Path}", video.RelativePath);
            return (false, null);
        }

        var finalAbsolute = Path.ChangeExtension(videoAbsolute, ".mkv");
        var sameContainer = string.Equals(finalAbsolute, videoAbsolute, PathComparison);
        if (!sameContainer && File.Exists(finalAbsolute))
        {
            // "ep01.avi" next to an unrelated "ep01.mkv" — never clobber a sibling payload file.
            logger.LogWarning(
                "Refusing to mux {Video}: {Target} already exists.", video.RelativePath, Path.GetFileName(finalAbsolute));
            return (false, null);
        }

        var videoProbe = await probe.ProbeAsync(videoAbsolute, cancellationToken);
        var existingAudioStreams = videoProbe.Streams.Count(stream => stream.Type == StreamType.Audio);

        var tempAbsolute = Path.Combine(Path.GetDirectoryName(videoAbsolute)!,
            Path.GetFileNameWithoutExtension(videoAbsolute) + ".muxtmp.mkv");
        await muxer.MuxAsync(
            new AudioMuxPlan(videoAbsolute, existingAudioStreams, inputs, tempAbsolute), cancellationToken);

        File.Move(tempAbsolute, finalAbsolute, overwrite: sameContainer);

        var slash = video.RelativePath.LastIndexOf('/');
        var fileName = Path.GetFileName(finalAbsolute);
        video.RelativePath = slash < 0 ? fileName : video.RelativePath[..(slash + 1)] + fileName;
        video.SizeBytes = new FileInfo(finalAbsolute).Length;
        video.UpdatedAt = DateTimeOffset.UtcNow;

        logger.LogInformation(
            "Muxed {Count} audio track(s) into {Video}.", inputs.Sum(input => input.Streams.Count), video.RelativePath);
        return (true, sameContainer ? null : videoAbsolute);
    }
}
