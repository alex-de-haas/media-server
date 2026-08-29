using MediaServer.Api.Data;
using MediaServer.Api.Native.Playback;
using MediaServer.Api.Remux;

namespace MediaServer.Api.Tests.Native;

/// <summary>
/// Which tracks a resolved URL will carry.
///
/// The picker ticks whatever this reports, so reporting a track the container will not carry is worse
/// than reporting none: the viewer is told they are hearing Russian while the packager quietly serves
/// the first thing it could describe.
/// </summary>
public sealed class NativeTrackChoiceTests
{
    private static readonly Guid Ac3English = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Ac3Russian = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid DtsRussian = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SrtEnglish = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid PgsEnglish = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid Foreign = Guid.Parse("99999999-9999-9999-9999-999999999999");

    private static IReadOnlyList<NativePlaybackResolver.StreamFacts> Streams() =>
    [
        new(StreamType.Video, "hevc", "HDR10", null, false, Guid.NewGuid()),
        new(StreamType.Audio, "ac3", null, 6, false, Ac3English, "eng"),
        new(StreamType.Audio, "ac3", null, 6, false, Ac3Russian, "rus"),
        new(StreamType.Audio, "dts", null, 6, false, DtsRussian, "rus"),
        new(StreamType.Subtitle, "subrip", null, null, false, SrtEnglish, "eng"),
        new(StreamType.Subtitle, "hdmv_pgs_subtitle", null, null, false, PgsEnglish, "eng"),
    ];

    private static NativeTrackSelection Choose(
        Guid? audio = null, Guid? subtitle = null, bool subtitlesOff = false,
        PlaybackPreference? preference = null) =>
        NativePlaybackResolver.Chosen(Streams(), audio, subtitle, subtitlesOff, preference, "eng");

    [Fact]
    public void The_stored_preference_decides_when_nothing_was_picked()
    {
        var chosen = Choose(preference: new PlaybackPreference { AudioLanguage = "rus" });

        // The Russian AC-3, not the Russian DTS: both match the language and only one can be written.
        Assert.Equal(Ac3Russian, chosen.AudioStreamId);
    }

    [Fact]
    public void A_picked_track_beats_the_preference()
    {
        var chosen = Choose(
            audio: Ac3English, preference: new PlaybackPreference { AudioLanguage = "rus" });

        Assert.Equal(Ac3English, chosen.AudioStreamId);
    }

    [Fact]
    public void A_track_the_packager_cannot_write_is_refused_rather_than_reported()
    {
        // DTS is out of scope for this client, and `RemuxTrackChoice` would drop it and serve AC-3.
        // Reporting the DTS id would tick the picker against audio nobody is hearing.
        var chosen = Choose(audio: DtsRussian);

        Assert.NotEqual(DtsRussian, chosen.AudioStreamId);
        Assert.Equal(Ac3English, chosen.AudioStreamId);
    }

    [Fact]
    public void A_bitmap_subtitle_is_refused_the_same_way()
    {
        var chosen = Choose(subtitle: PgsEnglish);

        Assert.Null(chosen.SubtitleStreamId);
    }

    [Fact]
    public void An_id_from_another_edition_names_nothing_here()
    {
        // One request resolves every edition of a title, and their track ids do not overlap.
        var chosen = Choose(audio: Foreign, preference: new PlaybackPreference { AudioLanguage = "rus" });

        Assert.Equal(Ac3Russian, chosen.AudioStreamId);
    }

    [Fact]
    public void An_audio_id_offered_as_a_subtitle_is_refused()
    {
        var chosen = Choose(subtitle: Ac3Russian);

        Assert.Null(chosen.SubtitleStreamId);
    }

    [Fact]
    public void Off_turns_subtitles_off_even_when_the_preference_names_a_language()
    {
        // The failure this exists to stop: "absent" and "none" as one value means the Off row hands the
        // viewer their preference straight back, and does nothing for precisely the people who need it.
        var chosen = Choose(
            subtitlesOff: true, preference: new PlaybackPreference { SubtitleLanguage = "eng" });

        Assert.Null(chosen.SubtitleStreamId);
    }

    [Fact]
    public void Without_off_that_same_preference_still_chooses_words()
    {
        var chosen = Choose(preference: new PlaybackPreference { SubtitleLanguage = "eng" });

        Assert.Equal(SrtEnglish, chosen.SubtitleStreamId);
    }

    [Fact]
    public void No_subtitle_language_asked_for_means_no_subtitles()
    {
        Assert.Null(Choose(preference: new PlaybackPreference { AudioLanguage = "eng" }).SubtitleStreamId);
    }
}
