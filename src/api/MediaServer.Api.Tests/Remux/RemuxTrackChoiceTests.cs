using MediaServer.Api.Remux;

namespace MediaServer.Api.Tests.Remux;

public sealed class RemuxTrackChoiceTests
{
    private static MatroskaIndex Library()
    {
        var index = new MatroskaIndex { SourceLength = 1 };
        index.Tracks.Add(new IndexedTrack { Number = 1, Ordinal = 0, Kind = IndexedTrackKind.Video });
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
        index.Tracks.Add(new IndexedTrack { Number = 1, Ordinal = 0, Kind = IndexedTrackKind.Video });

        Assert.Equal([1ul], RemuxTrackChoice.Resolve(index, null, null));
    }
}
