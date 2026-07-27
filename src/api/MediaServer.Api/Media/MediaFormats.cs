namespace MediaServer.Api.Media;

/// <summary>Supported playable container extensions (see <c>docs/features/catalogs/feature.md</c>).</summary>
public static class MediaFormats
{
    public static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".ts", ".m2ts",
    };

    /// <summary>
    /// External audio-track containers that ride alongside the video in a release (e.g. a torrent with the
    /// episodes plus a "Rus Sound" folder of per-episode <c>.mka</c> dubs). These are never playable items
    /// of their own: ingest matches each one to its episode/movie and places it beside that item's video as
    /// a sidecar (see <c>MediaServer.Api.Sidecars.SidecarPlacementService</c>). Merging one into the video
    /// is a separate, later operation.
    /// </summary>
    public static readonly IReadOnlySet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mka", ".ac3", ".eac3", ".dts", ".flac", ".aac", ".opus", ".mp3",
    };

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    /// <summary>True for an external audio track that accompanies a video file (see <see cref="AudioExtensions"/>).</summary>
    public static bool IsCompanionAudio(string path) => AudioExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// External subtitle containers that ride alongside the video in a release (e.g. a torrent's
    /// <c>RUS Subs</c> folder). Unlike an external audio track, these stay useful as files: clients read
    /// sidecar subtitles from disk, which is why their names follow the convention those clients match on.
    /// </summary>
    public static readonly IReadOnlySet<string> SubtitleExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".sub", ".vtt", ".sup",
    };

    /// <summary>True for an external subtitle that accompanies a video file (see <see cref="SubtitleExtensions"/>).</summary>
    public static bool IsCompanionSubtitle(string path) => SubtitleExtensions.Contains(Path.GetExtension(path));

    /// <summary>True for any companion track — audio or subtitle — that belongs beside a video rather than
    /// being a playable item of its own.</summary>
    public static bool IsCompanion(string path) => IsCompanionAudio(path) || IsCompanionSubtitle(path);

    /// <summary>
    /// Filters out non-video payloads and obvious junk (samples) so the organizer only links real
    /// playable media. v1 does not extract archives or handle multi-part releases.
    /// </summary>
    public static bool IsPlayableMedia(string relativePath, long sizeBytes)
    {
        if (!IsVideo(relativePath))
        {
            return false;
        }

        var fileName = Path.GetFileNameWithoutExtension(relativePath);
        if (fileName.Contains("sample", StringComparison.OrdinalIgnoreCase) && sizeBytes < 300L * 1024 * 1024)
        {
            return false;
        }

        return true;
    }
}
