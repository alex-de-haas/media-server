using MediaServer.Api.Remux;

namespace MediaServer.Api.Tests.Remux;

public sealed class RemuxTrackChoiceTests
{
    private static MatroskaIndex Library()
    {
        var index = new MatroskaIndex { SourceLength = 1 };
        index.Tracks.Add(new IndexedTrack
        {
            Number = 1, Ordinal = 0, Kind = IndexedTrackKind.Video,
            CodecId = "V_MPEGH/ISO/HEVC", CodecPrivate = [0x01],
        });
        index.Tracks.Add(new IndexedTrack { Number = 2, Ordinal = 1, Kind = IndexedTrackKind.Audio });
        index.Tracks.Add(new IndexedTrack { Number = 3, Ordinal = 2, Kind = IndexedTrackKind.Audio });
        index.Tracks.Add(new IndexedTrack
        {
            Number = 9, Ordinal = 3, Kind = IndexedTrackKind.Subtitle, CodecId = "S_TEXT/UTF8",
        });
        index.Tracks.Add(new IndexedTrack
        {
            Number = 10, Ordinal = 4, Kind = IndexedTrackKind.Subtitle, CodecId = "S_HDMV/PGS",
        });
        return index;
    }

    [Fact]
    public void Nothing_chosen_gives_the_first_video_and_the_first_audio()
    {
        Assert.Equal([1ul, 2ul], RemuxTrackChoice.Resolve(Library(), null, null));
    }

    [Fact]
    public void A_chosen_dub_is_the_one_carried()
    {
        Assert.Equal([1ul, 3ul], RemuxTrackChoice.Resolve(Library(), audioStreamIndex: 2, null));
    }

    [Fact]
    public void A_stale_choice_falls_back_rather_than_playing_nothing()
    {
        Assert.Equal([1ul, 2ul], RemuxTrackChoice.Resolve(Library(), audioStreamIndex: 99, null));
    }

    [Fact]
    public void Subtitles_are_carried_only_when_they_were_asked_for()
    {
        Assert.Equal([1ul, 2ul, 9ul], RemuxTrackChoice.Resolve(Library(), null, subtitleStreamIndex: 3));
        Assert.Equal([1ul, 2ul], RemuxTrackChoice.Resolve(Library(), null, null));
    }

    [Fact]
    public void A_bitmap_subtitle_is_not_carried_because_it_cannot_be_rewritten()
    {
        Assert.Equal([1ul, 2ul], RemuxTrackChoice.Resolve(Library(), null, subtitleStreamIndex: 4));
    }

    [Fact]
    public void A_subtitle_choice_that_no_longer_matches_adds_none()
    {
        Assert.Equal([1ul, 2ul], RemuxTrackChoice.Resolve(Library(), null, subtitleStreamIndex: 42));
    }

    [Fact]
    public void A_source_with_no_audio_still_gives_its_video()
    {
        var index = new MatroskaIndex { SourceLength = 1 };
        index.Tracks.Add(new IndexedTrack
        {
            Number = 1, Ordinal = 0, Kind = IndexedTrackKind.Video,
            CodecId = "V_MPEGH/ISO/HEVC", CodecPrivate = [0x01],
        });

        Assert.Equal([1ul], RemuxTrackChoice.Resolve(index, null, null));
    }

    [Fact]
    public void A_still_image_carried_as_a_video_track_is_not_mistaken_for_the_picture()
    {
        var index = new MatroskaIndex { SourceLength = 1 };
        // Cover art a muxer wrote as a real track rather than as an attachment, and listed first. Taking
        // it because it comes first would produce an output with no picture at all.
        index.Tracks.Add(new IndexedTrack
        {
            Number = 1, Ordinal = 0, Kind = IndexedTrackKind.Video, CodecId = "V_MJPEG",
        });
        index.Tracks.Add(new IndexedTrack
        {
            Number = 2, Ordinal = 1, Kind = IndexedTrackKind.Video,
            CodecId = "V_MPEGH/ISO/HEVC", CodecPrivate = [0x01],
        });
        index.Tracks.Add(new IndexedTrack { Number = 3, Ordinal = 2, Kind = IndexedTrackKind.Audio });

        Assert.Equal([2ul, 3ul], RemuxTrackChoice.Resolve(index, null, null));
    }

    [Fact]
    public void A_video_track_with_no_configuration_is_not_taken_either()
    {
        var index = new MatroskaIndex { SourceLength = 1 };
        // The codec is one we can write, but the track came without the record that describes it.
        index.Tracks.Add(new IndexedTrack
        {
            Number = 1, Ordinal = 0, Kind = IndexedTrackKind.Video, CodecId = "V_MPEGH/ISO/HEVC",
        });

        Assert.Empty(RemuxTrackChoice.Resolve(index, null, null));
    }
}
