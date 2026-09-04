using MediaServer.Api.Data;

namespace MediaServer.Api.Transcoding;

/// <summary>Request to transcode a movie source into a new sibling version, or to merge its sidecars in.
/// <para>
/// <see cref="MergeStreamIds"/> names external streams of this source — sidecar dubs and subtitles sitting
/// beside the file — whose tracks join the output. Naming any makes the job a merge: the video is copied
/// untouched, so the encode-only options must be left unset. The sidecar files themselves are not consumed;
/// the merge produces a new version alongside them.
/// </para>
/// <see cref="VideoCodec"/>
/// (<c>h264</c>/<c>hevc</c>, default <c>hevc</c>, or <c>copy</c> to remux the video untouched — lossless and
/// HDR-safe), <see cref="HardwareAcceleration"/> (<c>auto</c>/<c>vaapi</c>/<c>none</c>, default <c>auto</c>)
/// and <see cref="QualityLevel"/> (<c>highest</c>/<c>high</c>/<c>balanced</c>/<c>small</c>, default
/// <c>high</c>) fall back to defaults when omitted. A level is not a CRF: the engine maps it onto whichever
/// encoder the host can reach, so the same level means the same picture on every one of them.
/// <see cref="MaxHeight"/>
/// downscales to that height (ignored for <c>copy</c> or when the source is already smaller).
/// <see cref="AudioStreamIndexes"/>/<see cref="SubtitleStreamIndexes"/> are the source stream indexes to copy
/// (null = all); <see cref="DefaultAudioStreamIndex"/>/<see cref="DefaultSubtitleStreamIndex"/> mark one
/// copied track as the container default. <see cref="AudioTargets"/> re-encodes chosen audio tracks while
/// the rest are copied — independent of what happens to the picture, so shrinking only the audio is one
/// job.</summary>
public sealed record CreateTranscodeRequest(
    Guid SourceId,
    string? VideoCodec,
    string? HardwareAcceleration,
    string? QualityLevel,
    int? MaxHeight = null,
    IReadOnlyList<int>? AudioStreamIndexes = null,
    IReadOnlyList<int>? SubtitleStreamIndexes = null,
    int? DefaultAudioStreamIndex = null,
    int? DefaultSubtitleStreamIndex = null,
    IReadOnlyList<Guid>? MergeStreamIds = null,
    IReadOnlyList<StreamMetadataEdit>? MetadataEdits = null,
    IReadOnlyList<AudioTargetEdit>? AudioTargets = null,
    /// <summary><c>keep</c> (default) or <c>toProfile81</c>: rewrite a dual-layer Dolby Vision profile 7
    /// picture to single-layer profile 8.1 while it is copied — the form Apple TV and Infuse play as Dolby
    /// Vision. Refused with a re-encode, and on a version whose video is not profile 7.</summary>
    string? DolbyVision = null);

/// <summary>
/// Request to write chosen tracks of a movie source out as files beside it — the inverse of merging.
/// <para>
/// <see cref="StreamIds"/> names embedded audio and subtitle streams of this source. Each becomes a file of
/// its own under the sidecar naming convention, recorded as an external <c>MediaStream</c> of the same
/// source. <b>The container is not touched</b>: extraction copies out and never rewrites the video, so a
/// track exists in both places afterwards. Dropping one from the container is a conversion, composed in the
/// convert dialog.
/// </para>
/// </summary>
public sealed record CreateExtractionRequest(Guid SourceId, IReadOnlyList<Guid> StreamIds);

/// <summary>
/// Re-encodes one of the source's audio tracks instead of copying it. Named by <see cref="StreamId"/>, the
/// same way a metadata edit names its track, so callers never deal in engine stream indexes.
/// <para>
/// Per track rather than per job, because one file's tracks want opposite answers: a lossless multichannel
/// voice-over dub is the bulk of a UHD remux's size, while the original Atmos track beside it must not be
/// touched. <see cref="Bitrate"/> is in kbps and optional — omitted, the encoder scales one to the track's
/// channel count.
/// </para>
/// </summary>
public sealed record AudioTargetEdit(Guid StreamId, string Codec, int? Bitrate = null);

/// <summary>
/// Corrects one output stream's language or title while it is written. <see cref="StreamId"/> names a
/// stream of the source — embedded or sidecar — and a field left null keeps whatever the source stream
/// already carries, so relabelling one track never freezes the others' metadata.
/// <para>
/// There is no standalone rename: changing metadata alone still rewrites the file, so editing is offered
/// only where a job is being submitted anyway. The values are applied by the engine and come back from the
/// re-probed output; nothing edits the stored rows directly.
/// </para>
/// </summary>
public sealed record StreamMetadataEdit(Guid StreamId, string? Language = null, string? Title = null);

/// <summary>A transcode job with its persisted facts plus the live engine snapshot (when running).
/// <see cref="Kind"/> is <c>Convert</c> or <c>Extract</c>; an extraction has no single
/// <see cref="OutputPath"/> and lists its files in <see cref="OutputPaths"/> instead.</summary>
public sealed record TranscodeJobResponse(
    Guid Id,
    string EngineJobId,
    Guid MediaSourceId,
    Guid MediaItemId,
    string Kind,
    string? Name,
    string InputPath,
    string? OutputPath,
    IReadOnlyList<string> OutputPaths,
    string VideoCodec,
    string HardwareAcceleration,
    string? QualityLevel,
    int ReEncodedAudioTracks,
    string State,
    double PercentComplete,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    // Live snapshot (null when the engine has no active job for this id).
    double? Fps,
    double? Speed,
    double? EtaSeconds,
    long? OutputSizeBytes,
    // "toProfile81" when the copy also rewrote the picture's Dolby Vision to profile 8.1; null otherwise.
    string? DolbyVision = null)
{
    public static TranscodeJobResponse From(TranscodeJob job, JobSnapshot? snapshot)
    {
        var complete = job.State is TranscodeJobState.Completed;
        return new(
            job.Id,
            job.EngineJobId,
            job.MediaSourceId,
            job.MediaItemId,
            job.Kind.ToString(),
            job.Name,
            job.InputPath,
            job.OutputPath,
            // One entry either way, so a client that only wants "what did this produce" never has to know
            // which kind it is looking at.
            job.Outputs.Count > 0
                ? job.Outputs.OrderBy(output => output.SourceStreamIndex).Select(output => output.RelativePath).ToList()
                : job.OutputPath is { Length: > 0 } path ? [path] : [],
            job.VideoCodec,
            job.HardwareAcceleration,
            job.QualityLevel,
            job.ReEncodedAudioTracks,
            job.State.ToString(),
            complete ? 100 : snapshot?.PercentComplete ?? job.PercentComplete,
            job.Error,
            job.CreatedAt,
            job.CompletedAt,
            snapshot?.Fps,
            snapshot?.Speed,
            snapshot?.EtaSeconds,
            snapshot?.OutputSizeBytes,
            job.DolbyVision);
    }
}

/// <summary>Raised for invalid transcode requests (bad source, non-movie, missing file, no mount) — a 400.</summary>
public class TranscodeRequestException(string message) : Exception(message);

/// <summary>
/// Raised when a valid transcode request loses to concurrent state (the movie is mid-move to another
/// catalog) — a 409, matching the move-locking surface, so clients can tell "retry later" from "fix the
/// request".
/// </summary>
public sealed class TranscodeConflictException(string message) : TranscodeRequestException(message);
