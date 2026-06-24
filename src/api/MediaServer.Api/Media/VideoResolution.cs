namespace MediaServer.Api.Media;

/// <summary>
/// Maps a video stream's pixel dimensions to a display label (2160p/1080p/720p/480p). Keyed off width:
/// widescreen films keep the nominal width (e.g. 1920) while the height drops below the matching value, so
/// height alone mislabels them (a 1920x816 cut is 1080p, not 720p). Height is a fallback for vertical video
/// and the unbucketed remainder.
/// </summary>
public static class VideoResolution
{
    public static string? Label(int? width, int? height)
    {
        var w = width ?? 0;
        var h = height ?? 0;
        return (w, h) switch
        {
            ( <= 0, <= 0) => null,
            ( >= 3800, _) or (_, >= 2000) => "2160p",
            ( >= 1900, _) or (_, >= 1000) => "1080p",
            ( >= 1260, _) or (_, >= 700) => "720p",
            ( >= 700, _) or (_, >= 480) => "480p",
            _ => $"{(h > 0 ? h : w)}p",
        };
    }
}
