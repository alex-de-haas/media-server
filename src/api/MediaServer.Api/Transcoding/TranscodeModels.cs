using MediaServer.Api.Data;

namespace MediaServer.Api.Transcoding;

/// <summary>Request to transcode a movie source into a new sibling version. <see cref="VideoCodec"/>
/// (<c>h264</c>/<c>hevc</c>, default <c>hevc</c>, or <c>copy</c> to remux the video untouched — lossless and
/// HDR-safe), <see cref="HardwareAcceleration"/> (<c>auto</c>/<c>vaapi</c>/<c>none</c>, default <c>auto</c>)
/// and <see cref="Crf"/> (software only) fall back to defaults when omitted. <see cref="MaxHeight"/>
/// downscales to that height (ignored for <c>copy</c> or when the source is already smaller).
/// <see cref="AudioStreamIndexes"/>/<see cref="SubtitleStreamIndexes"/> are the source stream indexes to copy
/// (null = all); <see cref="DefaultAudioStreamIndex"/>/<see cref="DefaultSubtitleStreamIndex"/> mark one
/// copied track as the container default.</summary>
public sealed record CreateTranscodeRequest(
    Guid SourceId,
    string? VideoCodec,
    string? HardwareAcceleration,
    int? Crf,
    int? MaxHeight = null,
    IReadOnlyList<int>? AudioStreamIndexes = null,
    IReadOnlyList<int>? SubtitleStreamIndexes = null,
    int? DefaultAudioStreamIndex = null,
    int? DefaultSubtitleStreamIndex = null);

/// <summary>A transcode job with its persisted facts plus the live engine snapshot (when running).</summary>
public sealed record TranscodeJobResponse(
    Guid Id,
    string EngineJobId,
    Guid MediaSourceId,
    Guid MediaItemId,
    string? Name,
    string InputPath,
    string OutputPath,
    string VideoCodec,
    string HardwareAcceleration,
    int? Crf,
    string State,
    double PercentComplete,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    // Live snapshot (null when the engine has no active job for this id).
    double? Fps,
    double? Speed,
    double? EtaSeconds,
    long? OutputSizeBytes)
{
    public static TranscodeJobResponse From(TranscodeJob job, JobSnapshot? snapshot)
    {
        var complete = job.State is TranscodeJobState.Completed;
        return new(
            job.Id,
            job.EngineJobId,
            job.MediaSourceId,
            job.MediaItemId,
            job.Name,
            job.InputPath,
            job.OutputPath,
            job.VideoCodec,
            job.HardwareAcceleration,
            job.Crf,
            job.State.ToString(),
            complete ? 100 : snapshot?.PercentComplete ?? job.PercentComplete,
            job.Error,
            job.CreatedAt,
            job.CompletedAt,
            snapshot?.Fps,
            snapshot?.Speed,
            snapshot?.EtaSeconds,
            snapshot?.OutputSizeBytes);
    }
}

/// <summary>Raised for invalid transcode requests (bad source, non-movie, missing file, no mount).</summary>
public sealed class TranscodeRequestException(string message) : Exception(message);
