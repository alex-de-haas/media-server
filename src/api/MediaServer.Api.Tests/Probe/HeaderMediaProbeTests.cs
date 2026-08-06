using MediaServer.Api.Data;
using MediaServer.Api.Probe;
using Microsoft.Extensions.Logging.Abstractions;
using static MediaServer.Api.Tests.Probe.ContainerBuilders;

namespace MediaServer.Api.Tests.Probe;

/// <summary>
/// The header reader, over containers built byte by byte. What it must never do is guess: a field the
/// container does not state has to come back null, because the whole point of this provider is that a
/// consumer can tell "not HDR" from "nobody could tell".
/// </summary>
public sealed class HeaderMediaProbeTests : IDisposable
{
    private readonly List<string> _files = [];

    private string Write(string extension, byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"probe-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, content);
        _files.Add(path);
        return path;
    }

    private static HeaderMediaProbe Probe() => new(NullLogger<HeaderMediaProbe>.Instance);

    public void Dispose()
    {
        foreach (var path in _files)
        {
            File.Delete(path);
        }
    }

    private static double Seconds(ProbeResult result) => result.DurationTicks / (double)TimeSpan.TicksPerSecond;

    // ---- MP4 ----

    [Fact]
    public void Reads_an_mp4_duration_from_its_movie_header()
    {
        var result = Probe().TryProbe(Write(".mp4", Mp4(Mvhd(timescale: 1000, duration: 137_440))))!;

        Assert.Equal(137.44, Seconds(result), 3);
        Assert.Equal("mp4", result.Container);
    }

    [Fact]
    public void Reads_a_64_bit_mp4_duration()
    {
        // Version 1 widens the dates and the duration, which shifts every field after the version byte.
        var result = Probe().TryProbe(Write(".mp4", Mp4(Mvhd64(timescale: 90_000, duration: 12_345_678))))!;

        Assert.Equal(137.174, Seconds(result), 3);
    }

    [Fact]
    public void Maps_an_mp4_track()
    {
        var file = Write(".mp4", Mp4(
            Mvhd(1000, 1000),
            Trak("vide", "avc1", width: 1920, height: 1080, transferCharacteristics: 1),
            Trak("soun", "mp4a", language: "rus", name: "Дубляж", channels: 6, sampleRate: 48000)));

        var streams = Probe().TryProbe(file)!.Streams;

        var video = streams.Single(stream => stream.Type == StreamType.Video);
        Assert.Equal("h264", video.Codec);
        Assert.Equal(1920, video.Width);
        Assert.Equal("SDR", video.HdrFormat);

        var audio = streams.Single(stream => stream.Type == StreamType.Audio);
        Assert.Equal("aac", audio.Codec);
        Assert.Equal("rus", audio.Language);
        Assert.Equal("Дубляж", audio.Title);
        Assert.Equal(6, audio.Channels);
        Assert.Equal(48000, audio.SampleRate);
        // A per-track bitrate is beyond this reader, and a null from it means "could not tell" rather than
        // "the file states none" — which is exactly why the source's provider is recorded alongside it.
        Assert.Null(audio.Bitrate);
        Assert.Null(video.Bitrate);
    }

    [Fact]
    public void Embedded_cover_art_shifts_every_later_index_the_way_ffprobe_reports_it()
    {
        // ffprobe synthesizes a video stream for the artwork and places it at index 1. Job creation and
        // client track selection both address streams by absolute index, so a provider that numbered the
        // real tracks 0,1,2 while ffprobe numbered them 0,2,3 would select the wrong track.
        var file = Write(".m4v", Mp4(
            Mvhd(1000, 1000),
            CoverArtUdta(),
            Trak("vide", "avc1", width: 1280, height: 544),
            Trak("soun", "mp4a", language: "eng"),
            Trak("soun", "mp4a", language: "rus")));

        var streams = Probe().TryProbe(file)!.Streams;

        Assert.Equal([0, 1, 2, 3], streams.Select(stream => stream.Index));
        Assert.Equal("mjpeg", streams[1].Codec);
        Assert.Equal("eng", streams[2].Language);
        Assert.Equal("rus", streams[3].Language);
    }

    [Fact]
    public void Without_cover_art_the_indexes_are_the_track_order()
    {
        var file = Write(".mp4", Mp4(Mvhd(1000, 1000), Trak("vide", "avc1"), Trak("soun", "mp4a")));

        Assert.Equal([0, 1], Probe().TryProbe(file)!.Streams.Select(stream => stream.Index));
    }

    [Fact]
    public void A_dolby_vision_record_outranks_the_transfer_function()
    {
        var file = Write(".mp4", Mp4(
            Mvhd(1000, 1000),
            Trak("vide", "hvc1", transferCharacteristics: 16, dolbyVision: true)));

        Assert.Equal("Dolby Vision", Probe().TryProbe(file)!.Streams[0].HdrFormat);
    }

    // ---- Matroska ----

    [Fact]
    public void Reads_a_matroska_duration_scaled_by_its_timestamp_scale()
    {
        var file = Write(".mkv", Matroska(Info(durationTicks: 137_463, timestampScale: 1_000_000)));

        Assert.Equal(137.463, Seconds(Probe().TryProbe(file)!), 3);
    }

    [Fact]
    public void Maps_matroska_tracks_with_their_flags_and_names()
    {
        var file = Write(".mkv", Matroska(
            Info(30_000),
            Tracks(
                TrackEntry(1, "V_MPEGH/ISO/HEVC", "eng", width: 1920, height: 872, transferCharacteristics: 16, bitsPerChannel: 10),
                TrackEntry(2, "A_AC3", "rus", "MVO заКАДРЫ", channels: 6),
                TrackEntry(17, "S_TEXT/UTF8", "rus", "Forced", isDefault: false, isForced: true))));

        var streams = Probe().TryProbe(file)!.Streams;

        Assert.Equal("hevc", streams[0].Codec);
        Assert.Equal("HDR", streams[0].HdrFormat);
        Assert.Equal(10, streams[0].BitDepth);

        Assert.Equal("ac3", streams[1].Codec);
        Assert.Equal("MVO заКАДРЫ", streams[1].Title);
        Assert.Equal(6, streams[1].Channels);

        Assert.Equal(StreamType.Subtitle, streams[2].Type);
        Assert.Equal("subrip", streams[2].Codec);
        Assert.True(streams[2].IsForced);
        Assert.False(streams[2].IsDefault);
    }

    [Theory]
    // 16 is PQ and 18 is HLG; a stated non-HDR function is a real SDR answer, and no colour data at all is
    // not an answer — the authoritative copy may live in the codec bitstream, out of a header's reach.
    [InlineData(16, "HDR")]
    [InlineData(18, "HLG")]
    [InlineData(1, "SDR")]
    [InlineData(0, null)]
    public void Hdr_says_only_what_the_container_states(int transfer, string? expected)
    {
        var file = Write(".mkv", Matroska(
            Info(1000),
            Tracks(TrackEntry(1, "V_MPEGH/ISO/HEVC", transferCharacteristics: transfer))));

        Assert.Equal(expected, Probe().TryProbe(file)!.Streams[0].HdrFormat);
    }

    [Fact]
    public void A_two_letter_bcp47_language_becomes_the_three_letter_form()
    {
        // The newer LanguageBCP47 element yields "ru" where the legacy element and ffprobe say "rus";
        // without normalizing, one library would hold both spellings depending on the muxer.
        var file = Write(".mkv", Matroska(Info(1000), Tracks(TrackEntry(2, "A_AC3", "ru"))));

        Assert.Equal("rus", Probe().TryProbe(file)!.Streams[0].Language);
    }

    [Theory]
    [InlineData("und")]
    [InlineData(null)]
    public void An_undefined_language_is_no_language(string? language)
    {
        var file = Write(".mkv", Matroska(Info(1000), Tracks(TrackEntry(2, "A_AC3", language))));

        Assert.Null(Probe().TryProbe(file)!.Streams[0].Language);
    }

    [Fact]
    public void An_unknown_codec_id_is_passed_through_rather_than_guessed_at()
    {
        var file = Write(".mkv", Matroska(Info(1000), Tracks(TrackEntry(2, "A_SOMETHING_NEW"))));

        Assert.Equal("a_something_new", Probe().TryProbe(file)!.Streams[0].Codec);
    }

    [Fact]
    public void A_matroska_file_with_no_duration_element_yields_no_duration()
    {
        // A live-muxed file states no Duration; ffprobe reports nothing for these either.
        var file = Write(".mkv", Matroska(Info(durationTicks: null), Tracks(TrackEntry(1, "V_MPEG4/ISO/AVC"))));

        Assert.Equal(0, Probe().TryProbe(file)!.DurationTicks);
    }

    [Fact]
    public void Reads_the_writing_app_for_grouping_divergence_reports()
    {
        var file = Write(".mkv", Matroska(Info(1000, writingApp: "mkvmerge v82.0")));

        Assert.Equal("mkvmerge v82.0", Probe().TryReadWritingApp(file));
    }

    [Fact]
    public void An_mka_sidecar_is_read_as_matroska()
    {
        // A sidecar dub carries its own language and title, which is why the file name never has to.
        var file = Write(".mka", Matroska(Info(600_000), Tracks(TrackEntry(2, "A_AC3", "rus", "DUB | DD5.1 @ 640 kbps"))));

        var stream = Assert.Single(Probe().TryProbe(file)!.Streams);
        Assert.Equal("rus", stream.Language);
        Assert.Equal("DUB | DD5.1 @ 640 kbps", stream.Title);
    }

    // ---- AVI ----

    [Fact]
    public void Reads_an_avi_duration_from_its_frame_count()
    {
        var file = Write(".avi", Avi(microsecondsPerFrame: 41_667, totalFrames: 195_354));

        Assert.Equal(8139.815, Seconds(Probe().TryProbe(file)!), 2);
    }

    [Fact]
    public void An_open_dml_frame_count_overrides_the_main_header()
    {
        // Past ~2 GB an AVI continues in further RIFF AVIX segments and avih.TotalFrames counts only the
        // first, so a long file reads short. Two files in the development library were 1252 s and 715 s out
        // until the extended header was read.
        var file = Write(".avi", Avi(41_667, totalFrames: 195_354, openDmlTotalFrames: 225_398));

        Assert.Equal(9391.658, Seconds(Probe().TryProbe(file)!), 2);
    }

    [Fact]
    public void An_avi_reports_no_track_list()
    {
        // AVI stream headers carry no language and no title at all, so a track list from one would be
        // poorer than saying nothing and letting the other provider answer.
        var file = Write(".avi", Avi(41_667, 24_000));

        Assert.Empty(Probe().TryProbe(file)!.Streams);
    }

    // ---- refusals ----

    [Fact]
    public void An_unsupported_container_yields_nothing()
    {
        // A transport stream states no duration in any header; finding one means scanning, which is what
        // the other provider is for.
        Assert.Null(Probe().TryProbe(Write(".ts", [1, 2, 3, 4])));
    }

    [Fact]
    public void A_truncated_file_yields_nothing_rather_than_a_wrong_answer()
    {
        var complete = Mp4(Mvhd(1000, 137_440), Trak("vide", "avc1"));
        Assert.Null(Probe().TryProbe(Write(".mp4", complete[..24])));
    }

    [Fact]
    public void A_file_that_is_not_a_container_at_all_yields_nothing() =>
        Assert.Null(Probe().TryProbe(Write(".mkv", "this is not media"u8.ToArray())));

    [Fact]
    public async Task ProbeAsync_throws_where_TryProbe_declines_so_a_caller_cannot_miss_it()
    {
        var path = Write(".ts", [1, 2, 3, 4]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Probe().ProbeAsync(path, CancellationToken.None));
    }
}
