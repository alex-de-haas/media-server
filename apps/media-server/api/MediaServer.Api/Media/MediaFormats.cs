namespace MediaServer.Api.Media;

/// <summary>Supported playable container extensions (see <c>docs/planning/catalogs.md</c>).</summary>
public static class MediaFormats
{
    public static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".ts", ".m2ts",
    };

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

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
