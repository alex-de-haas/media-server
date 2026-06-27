using MediaServer.Api.Transcoding;

namespace MediaServer.Api.Tests.Transcoding;

public sealed class TranscodeServiceTests
{
    // The output is a sibling of the source in the same folder, with a version-label suffix, always in a
    // Matroska container (the universal carrier) regardless of the source extension.
    [Theory]
    [InlineData("The Rock (1996)/The Rock (1996).mkv", "HEVC 1080p", "The Rock (1996)/The Rock (1996) - HEVC 1080p.mkv")]
    [InlineData("Movies/Heat (1995).mp4", "H.264", "Movies/Heat (1995) - H.264.mkv")]
    [InlineData("a/b/c.mkv", "Remux", "a/b/c - Remux.mkv")]
    [InlineData("movie.mp4", "HEVC", "movie - HEVC.mkv")]
    [InlineData("movie", "HEVC", "movie - HEVC.mkv")]
    [InlineData("dir\\file.mkv", "HEVC", "dir/file - HEVC.mkv")]
    public void BuildOutputRelative_AddsLabelSuffix_AlwaysMatroska(string source, string label, string expected) =>
        Assert.Equal(expected, TranscodeService.BuildOutputRelative(source, label));

    [Theory]
    [InlineData("copy", null, "Remux")]
    [InlineData("hevc", null, "HEVC")]
    [InlineData("hevc", 1080, "HEVC 1080p")]
    [InlineData("h264", null, "H.264")]
    [InlineData("h264", 720, "H.264 720p")]
    public void VersionLabel_DescribesCodecAndResolution(string codec, int? targetHeight, string expected) =>
        Assert.Equal(expected, TranscodeService.VersionLabel(codec, targetHeight));
}
