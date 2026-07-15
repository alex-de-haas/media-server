using MediaServer.Api.Mux;

namespace MediaServer.Api.Tests.Mux;

public sealed class FfmpegAudioMuxerTests
{
    [Fact]
    public void Builds_a_stream_copy_mux_with_tags_positioned_after_the_videos_own_audio()
    {
        var plan = new AudioMuxPlan(
            "/staging/ep01.mkv",
            VideoAudioStreamCount: 1,
            [
                new AudioMuxInput("/staging/Rus Sound/ep01.mka", [new AudioMuxStreamTag("rus", "Rus Sound")]),
                new AudioMuxInput("/staging/ep01.eng.ac3", [new AudioMuxStreamTag(null, null)]),
            ],
            "/staging/ep01.muxtmp.mkv");

        Assert.Equal(
        [
            "-nostdin", "-y", "-v", "error",
            "-i", "/staging/ep01.mkv",
            "-i", "/staging/Rus Sound/ep01.mka",
            "-i", "/staging/ep01.eng.ac3",
            "-map", "0", "-map", "1:a", "-map", "2:a",
            "-c", "copy",
            // Output audio position 0 is the video's own track; the first appended stream is 1. The second
            // appended stream carries its source tags, so nothing is written for it.
            "-metadata:s:a:1", "language=rus",
            "-metadata:s:a:1", "title=Rus Sound",
            "-f", "matroska",
            "/staging/ep01.muxtmp.mkv",
        ], FfmpegAudioMuxer.BuildArguments(plan));
    }

    [Fact]
    public void A_multi_track_input_advances_the_metadata_position_per_stream()
    {
        var plan = new AudioMuxPlan(
            "/staging/ep01.mkv",
            VideoAudioStreamCount: 2,
            [
                new AudioMuxInput("/staging/dubs.mka",
                [
                    new AudioMuxStreamTag(null, null),
                    new AudioMuxStreamTag("ukr", null),
                ]),
            ],
            "/staging/out.mkv");

        var arguments = FfmpegAudioMuxer.BuildArguments(plan);

        // Two existing tracks + the input's untagged first stream ⇒ the tagged one sits at position 3.
        Assert.Contains("-metadata:s:a:3", arguments);
        Assert.Contains("language=ukr", arguments);
        Assert.DoesNotContain("-metadata:s:a:2", arguments);
    }
}
