namespace MediaServer.Api.Jellyfin.Streaming;

/// <summary>
/// The containers Media Server serves by Direct Play/Direct Stream (no conversion). Maps a file
/// extension to a content type and gates which files are streamable. See
/// <c>docs/planning/jellyfin-compatibility.md</c>.
/// </summary>
public static class DirectPlay
{
    private static readonly IReadOnlyDictionary<string, string> ContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["mp4"] = "video/mp4",
            ["m4v"] = "video/x-m4v",
            ["mov"] = "video/quicktime",
            ["mkv"] = "video/x-matroska",
            ["webm"] = "video/webm",
            ["avi"] = "video/x-msvideo",
            ["ts"] = "video/mp2t",
            ["m2ts"] = "video/mp2t",
        };

    public static string Normalize(string? extensionOrContainer) =>
        (extensionOrContainer ?? string.Empty).TrimStart('.').ToLowerInvariant();

    public static bool IsSupported(string? extensionOrContainer) =>
        ContentTypes.ContainsKey(Normalize(extensionOrContainer));

    public static string ContentType(string? extensionOrContainer) =>
        ContentTypes.GetValueOrDefault(Normalize(extensionOrContainer), "application/octet-stream");
}
