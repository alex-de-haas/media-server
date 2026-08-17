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
        index.Tracks.Add(new IndexedTrack
        {
            Number = 2, Ordinal = 1, Kind = IndexedTrackKind.Audio, CodecId = "A_AC3",
        });
        index.Tracks.Add(new IndexedTrack
        {
            Number = 3, Ordinal = 2, Kind = IndexedTrackKind.Audio, CodecId = "A_EAC3",
        });
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
    public void One_track_of_each_kind_is_carried_rather_than_every_one()
    {
        // Video and the first describable audio. No subtitle, because none was asked for.
        //
        // Carrying them all made the header 29.5 MB on a fifteen-track film — read and parsed before the
        // first frame — and an Apple TV could not play it while two-track titles were flawless.
        Assert.Equal([1ul, 2ul], RemuxTrackChoice.Resolve(Library(), null, null));
    }

    [Fact]
    public void A_chosen_dub_is_the_one_carried()
    {
        // Changing it means a new URL and a re-seated player, which is the cost of a header a device can
        // actually parse.
        Assert.Equal([1ul, 3ul], RemuxTrackChoice.Resolve(Library(), audioStreamIndex: 2, null));
    }

    [Fact]
    public void A_chosen_subtitle_leads_the_subtitles()
    {
        var index = Library();
        index.Tracks.Add(new IndexedTrack
        {
            Number = 11, Ordinal = 5, Kind = IndexedTrackKind.Subtitle, CodecId = "S_TEXT/ASS",
        });

        Assert.Equal([1ul, 2ul, 11ul], RemuxTrackChoice.Resolve(index, null, subtitleStreamIndex: 5));
    }

    [Fact]
    public void A_stale_choice_falls_back_to_the_files_own_order()
    {
        Assert.Equal([1ul, 2ul], RemuxTrackChoice.Resolve(Library(), audioStreamIndex: 99, null));
        Assert.Equal([1ul, 2ul], RemuxTrackChoice.Resolve(Library(), null, subtitleStreamIndex: 42));
    }

    [Fact]
    public void A_bitmap_subtitle_is_never_carried_because_it_cannot_be_rewritten()
    {
        // Even asked for by name. A track in the menu that shows nothing when selected is worse than a
        // track that is not offered.
        Assert.DoesNotContain(10ul, RemuxTrackChoice.Resolve(Library(), null, subtitleStreamIndex: 4));
        Assert.Equal([1ul, 2ul], RemuxTrackChoice.Resolve(Library(), null, subtitleStreamIndex: 4));
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
        index.Tracks.Add(new IndexedTrack
        {
            Number = 3, Ordinal = 2, Kind = IndexedTrackKind.Audio, CodecId = "A_AC3",
        });

        Assert.Equal([2ul, 3ul], RemuxTrackChoice.Resolve(index, null, null));
    }

    [Fact]
    public void An_audio_track_nothing_can_describe_is_not_taken_as_the_default()
    {
        var index = new MatroskaIndex { SourceLength = 1 };
        index.Tracks.Add(new IndexedTrack
        {
            Number = 1, Ordinal = 0, Kind = IndexedTrackKind.Video,
            CodecId = "V_MPEGH/ISO/HEVC", CodecPrivate = [0x01],
        });
        // A film that leads with its lossless track and keeps AC-3 behind it, which is the ordinary layout
        // for anything remuxed from a disc. The resolver offers a remux because *an* audio track can be
        // packaged; taking the first one regardless would deliver a picture and no sound.
        index.Tracks.Add(new IndexedTrack
        {
            Number = 2, Ordinal = 1, Kind = IndexedTrackKind.Audio, CodecId = "A_TRUEHD",
        });
        index.Tracks.Add(new IndexedTrack
        {
            Number = 3, Ordinal = 2, Kind = IndexedTrackKind.Audio, CodecId = "A_AC3",
        });

        Assert.Equal([1ul, 3ul], RemuxTrackChoice.Resolve(index, null, null));
    }

    [Fact]
    public void Choosing_an_audio_track_nothing_can_describe_falls_back_to_one_that_plays()
    {
        var index = new MatroskaIndex { SourceLength = 1 };
        index.Tracks.Add(new IndexedTrack
        {
            Number = 1, Ordinal = 0, Kind = IndexedTrackKind.Video,
            CodecId = "V_MPEGH/ISO/HEVC", CodecPrivate = [0x01],
        });
        index.Tracks.Add(new IndexedTrack
        {
            Number = 2, Ordinal = 1, Kind = IndexedTrackKind.Audio, CodecId = "A_DTS",
        });
        index.Tracks.Add(new IndexedTrack
        {
            Number = 3, Ordinal = 2, Kind = IndexedTrackKind.Audio, CodecId = "A_AC3",
        });

        // The preference is real and current — it simply points at something no sample entry covers, which
        // is the same situation as a preference that points nowhere.
        Assert.Equal([1ul, 3ul], RemuxTrackChoice.Resolve(index, audioStreamIndex: 1, null));
    }

    [Fact]
    public void A_source_whose_every_audio_track_is_undescribable_still_gives_its_video()
    {
        var index = new MatroskaIndex { SourceLength = 1 };
        index.Tracks.Add(new IndexedTrack
        {
            Number = 1, Ordinal = 0, Kind = IndexedTrackKind.Video,
            CodecId = "V_MPEGH/ISO/HEVC", CodecPrivate = [0x01],
        });
        index.Tracks.Add(new IndexedTrack
        {
            Number = 2, Ordinal = 1, Kind = IndexedTrackKind.Audio, CodecId = "A_TRUEHD",
        });

        Assert.Equal([1ul], RemuxTrackChoice.Resolve(index, null, null));
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
