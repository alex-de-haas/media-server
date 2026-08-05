using MediaServer.Api.Data;
using MediaServer.Api.Native.Playback;

namespace MediaServer.Api.Tests.Native;

/// <summary>
/// Resolving a stored intent against one source's tracks. The point of storing intent rather than
/// indexes is that the same preference has to keep meaning the same thing across editions with
/// different track layouts, so these run the same preference against two of them.
/// </summary>
public sealed class NativeTrackSelectorTests
{
    private static NativeTrackSelector.TrackCandidate Audio(
        Guid id, string? language, bool isDefault = false, bool external = false) =>
        new(id, StreamType.Audio, language, isDefault, IsForced: false, external);

    private static NativeTrackSelector.TrackCandidate Subtitle(
        Guid id, string? language, bool forced = false) =>
        new(id, StreamType.Subtitle, language, IsDefault: false, IsForced: forced, IsExternal: false);

    private static PlaybackPreference Preference(
        string? audio = null, string? subtitle = null, bool forcedOnly = false, bool original = false) =>
        new()
        {
            Id = Guid.NewGuid(),
            AppUserId = 1,
            AudioLanguage = audio,
            SubtitleLanguage = subtitle,
            SubtitlesForcedOnly = forcedOnly,
            PreferOriginalAudio = original,
        };

    [Fact]
    public void The_same_preference_picks_the_right_track_in_two_differently_ordered_editions()
    {
        var remuxRussian = Guid.NewGuid();
        var cutRussian = Guid.NewGuid();

        var remux = new[] { Audio(Guid.NewGuid(), "eng", isDefault: true), Audio(remuxRussian, "rus") };
        var cut = new[] { Audio(cutRussian, "rus"), Audio(Guid.NewGuid(), "eng", isDefault: true) };

        var preference = Preference(audio: "rus");

        Assert.Equal(remuxRussian, NativeTrackSelector.Select(remux, preference, "eng").AudioStreamId);
        Assert.Equal(cutRussian, NativeTrackSelector.Select(cut, preference, "eng").AudioStreamId);
    }

    [Fact]
    public void A_sidecar_dub_is_a_candidate_like_any_other_track()
    {
        // The thing no existing client can do: an external dub is a track, and on this surface it is
        // fetchable, so a preference may land on it.
        var sidecar = Guid.NewGuid();
        var streams = new[] { Audio(Guid.NewGuid(), "eng", isDefault: true), Audio(sidecar, "rus", external: true) };

        Assert.Equal(sidecar, NativeTrackSelector.Select(streams, Preference(audio: "rus"), "eng").AudioStreamId);
    }

    [Fact]
    public void Preferring_the_original_beats_the_language_preference_when_the_source_has_it()
    {
        var english = Guid.NewGuid();
        var streams = new[] { Audio(english, "eng"), Audio(Guid.NewGuid(), "rus", isDefault: true) };

        var selection = NativeTrackSelector.Select(
            streams, Preference(audio: "rus", original: true), originalLanguage: "eng");

        Assert.Equal(english, selection.AudioStreamId);
    }

    [Fact]
    public void Preferring_the_original_falls_back_when_the_source_does_not_have_it()
    {
        var russian = Guid.NewGuid();
        var streams = new[] { Audio(russian, "rus", isDefault: true) };

        var selection = NativeTrackSelector.Select(
            streams, Preference(audio: "rus", original: true), originalLanguage: "eng");

        Assert.Equal(russian, selection.AudioStreamId);
    }

    [Fact]
    public void With_nothing_asked_for_the_sources_own_default_is_left_alone()
    {
        var defaulted = Guid.NewGuid();
        var streams = new[] { Audio(Guid.NewGuid(), "eng"), Audio(defaulted, "rus", isDefault: true) };

        Assert.Equal(defaulted, NativeTrackSelector.Select(streams, preference: null, "eng").AudioStreamId);
    }

    [Fact]
    public void No_subtitle_language_means_no_subtitles()
    {
        // Silence is a real answer: picking one anyway is how a viewer ends up with subtitles they
        // never asked for.
        var streams = new[] { Subtitle(Guid.NewGuid(), "rus") };

        Assert.Null(NativeTrackSelector.Select(streams, Preference(audio: "rus"), "eng").SubtitleStreamId);
    }

    [Fact]
    public void Forced_only_ignores_a_full_dialogue_track_of_the_same_language()
    {
        var forced = Guid.NewGuid();
        var streams = new[] { Subtitle(Guid.NewGuid(), "rus"), Subtitle(forced, "rus", forced: true) };

        var selection = NativeTrackSelector.Select(
            streams, Preference(subtitle: "rus", forcedOnly: true), "eng");

        Assert.Equal(forced, selection.SubtitleStreamId);
    }

    [Fact]
    public void Forced_only_with_no_forced_track_picks_nothing_rather_than_the_full_one()
    {
        var streams = new[] { Subtitle(Guid.NewGuid(), "rus") };

        var selection = NativeTrackSelector.Select(
            streams, Preference(subtitle: "rus", forcedOnly: true), "eng");

        Assert.Null(selection.SubtitleStreamId);
    }
}
