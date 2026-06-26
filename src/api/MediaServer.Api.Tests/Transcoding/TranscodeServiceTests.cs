using MediaServer.Api.Transcoding;

namespace MediaServer.Api.Tests.Transcoding;

public sealed class TranscodeServiceTests
{
    // The output is a sibling of the source in the same folder, with a codec suffix and the same container,
    // so the re-encode can become a new version of the same movie.
    [Theory]
    [InlineData("The Rock (1996)/The Rock (1996).mkv", "hevc", "The Rock (1996)/The Rock (1996) - HEVC.mkv")]
    [InlineData("Movies/Heat (1995).mp4", "h264", "Movies/Heat (1995) - H264.mp4")]
    [InlineData("a/b/c.mkv", "hevc", "a/b/c - HEVC.mkv")]
    [InlineData("movie.mp4", "hevc", "movie - HEVC.mp4")]
    [InlineData("movie", "hevc", "movie - HEVC")]
    [InlineData("dir\\file.mkv", "hevc", "dir/file - HEVC.mkv")]
    public void BuildOutputRelative_AddsCodecSuffix_KeepsFolderAndContainer(string source, string codec, string expected) =>
        Assert.Equal(expected, TranscodeService.BuildOutputRelative(source, codec));
}
