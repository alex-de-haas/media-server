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

    // A merge that only copies keeps the plain "Merged" every merge has produced so far; one that also
    // encodes carries both, because the label is the whole of what separates two output paths — and the
    // duplicate check refuses a second job producing a path that exists.
    [Theory]
    [InlineData("copy", null, "Merged")]
    [InlineData("hevc", null, "HEVC Merged")]
    [InlineData("hevc", 1080, "HEVC 1080p Merged")]
    [InlineData("h264", 720, "H.264 720p Merged")]
    public void VersionLabel_AppendsMerged_WhenSidecarsJoinTheOutput(string codec, int? targetHeight, string expected) =>
        Assert.Equal(expected, TranscodeService.VersionLabel(codec, targetHeight, isMerge: true));

    private static CreateTranscodeRequest Request(string? codec, int? maxHeight = null, int? crf = null, bool merge = false) =>
        new(Guid.NewGuid(), codec, null, crf, maxHeight, MergeStreamIds: merge ? [Guid.NewGuid()] : null);

    // Merging says what joins the output, not what happens to the picture — so it no longer forces a copy.
    // What it keeps is the default: omitting the codec copies, where a plain job would encode to HEVC.
    [Theory]
    [InlineData(null, false, "hevc")]
    [InlineData(null, true, "copy")]
    [InlineData("", true, "copy")]
    [InlineData("hevc", true, "hevc")]
    [InlineData("h264", true, "h264")]
    [InlineData("copy", true, "copy")]
    public void ResolveCodec_LetsAMergeReEncode_ButNeverByOmission(string? codec, bool merge, string expected) =>
        Assert.Equal(expected, TranscodeService.ResolveCodec(Request(codec, merge: merge), merge));

    [Fact]
    public void ResolveCodec_RefusesEncodeOnlyKnobs_OnAMergeThatNamesNoCodec()
    {
        var error = Assert.Throws<TranscodeRequestException>(() =>
            TranscodeService.ResolveCodec(Request(null, maxHeight: 1080, merge: true), isMerge: true));

        // The message has to point at the fix, because the request looks like a downscale that was ignored.
        Assert.Contains("need a videoCodec", error.Message);
    }

    [Fact]
    public void ResolveCodec_AcceptsEncodeOnlyKnobs_OnAMergeThatNamesOne() =>
        Assert.Equal("hevc", TranscodeService.ResolveCodec(
            Request("hevc", maxHeight: 1080, crf: 24, merge: true), isMerge: true));

    [Fact]
    public void ResolveCodec_StillRefusesEncodeOnlyKnobs_OnAnExplicitCopy()
    {
        var error = Assert.Throws<TranscodeRequestException>(() =>
            TranscodeService.ResolveCodec(Request("copy", crf: 24), isMerge: false));

        Assert.Contains("video is copied", error.Message);
    }
}
