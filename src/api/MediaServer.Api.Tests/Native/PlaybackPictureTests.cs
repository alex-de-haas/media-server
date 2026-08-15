using MediaServer.Api.Data;
using MediaServer.Api.Native.Playback;

namespace MediaServer.Api.Tests.Native;

/// <summary>
/// Which stream the resolver judges as "the picture".
///
/// A cover image the muxer never flagged as attached art is an ordinary video stream in every way the
/// database can see, and this library holds such files — the remux path has skipped them since the
/// spike. The resolver did not, and against production it refused a perfectly playable HEVC film for
/// having an undecodable picture.
/// </summary>
public sealed class PlaybackPictureTests
{
    private static NativeCapabilityProfile Apple => new(
        ["mp4", "m4v", "mov"], ["hevc", "h264"], ["aac", "ac3", "eac3"], ["SDR", "HDR10"]);

    private static MediaStream Stream(StreamType type, int index, string codec) =>
        new() { Id = Guid.NewGuid(), StreamType = type, Index = index, Codec = codec };

    [Fact]
    public void A_cover_image_is_not_the_picture()
    {
        var streams = new[]
        {
            Stream(StreamType.Video, 0, "hevc"),
            Stream(StreamType.Video, 4, "mjpeg"),
        };

        Assert.Equal("hevc", NativePlaybackResolver.PictureFor(streams)?.Codec);
    }

    [Fact]
    public void A_cover_image_listed_first_is_still_not_the_picture()
    {
        // The ordering the database returns is not guaranteed, so the choice cannot rest on it alone.
        var streams = new[]
        {
            Stream(StreamType.Video, 4, "mjpeg"),
            Stream(StreamType.Video, 0, "hevc"),
        };

        Assert.Equal("hevc", NativePlaybackResolver.PictureFor(streams)?.Codec);
    }

    [Fact]
    public void A_file_whose_only_video_is_a_still_gets_an_answer_about_that_still()
    {
        // Broken either way, and refusing it with a reason beats pretending it has no picture at all.
        var streams = new[] { Stream(StreamType.Video, 0, "png") };

        Assert.Equal("png", NativePlaybackResolver.PictureFor(streams)?.Codec);
    }

    [Fact]
    public void A_source_with_no_video_has_no_picture()
    {
        var streams = new[] { Stream(StreamType.Audio, 0, "eac3") };

        Assert.Null(NativePlaybackResolver.PictureFor(streams));
    }
}
