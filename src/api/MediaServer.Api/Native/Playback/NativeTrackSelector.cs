using MediaServer.Api.Data;

namespace MediaServer.Api.Native.Playback;

/// <summary>
/// Turns a stored intent into the tracks of one particular source.
///
/// This is the whole reason preferences are not stream indexes: "Russian dub, no subtitles" survives a
/// title gaining a second edition with a different track order, and an index does not. Sidecars count
/// as candidates — an external dub is a track a client can play, and on this surface it is fetchable.
/// </summary>
public static class NativeTrackSelector
{
    public static NativeTrackSelection Select(
        IReadOnlyList<TrackCandidate> streams, PlaybackPreference? preference, string? originalLanguage)
    {
        var audio = streams.Where(stream => stream.StreamType == StreamType.Audio).ToList();
        var subtitles = streams.Where(stream => stream.StreamType == StreamType.Subtitle).ToList();

        return new NativeTrackSelection(
            AudioStreamId: PickAudio(audio, preference, originalLanguage)?.Id,
            SubtitleStreamId: PickSubtitle(subtitles, preference)?.Id);
    }

    private static TrackCandidate? PickAudio(
        IReadOnlyList<TrackCandidate> audio, PlaybackPreference? preference, string? originalLanguage)
    {
        if (audio.Count == 0)
        {
            return null;
        }

        // "Prefer the original" wins over the language preference when the source actually has it —
        // that is what the flag is for: a viewer who normally takes a dub watching this one subtitled.
        if (preference?.PreferOriginalAudio == true && Match(audio, originalLanguage) is { } original)
        {
            return original;
        }

        if (Match(audio, preference?.AudioLanguage) is { } preferred)
        {
            return preferred;
        }

        // Nothing asked for, or nothing matching: leave the source's own default rather than imposing
        // a choice the viewer never made.
        return audio.FirstOrDefault(track => track.IsDefault) ?? audio[0];
    }

    private static TrackCandidate? PickSubtitle(
        IReadOnlyList<TrackCandidate> subtitles, PlaybackPreference? preference)
    {
        if (subtitles.Count == 0 || preference is null)
        {
            return null;
        }

        var candidates = preference.SubtitlesForcedOnly
            ? subtitles.Where(track => track.IsForced).ToList()
            : subtitles;

        // No subtitle language asked for means none is chosen. Silence is a real answer here: picking
        // one anyway is how a viewer ends up with subtitles they never asked for.
        return Match(candidates, preference.SubtitleLanguage);
    }

    private static TrackCandidate? Match(IReadOnlyList<TrackCandidate> tracks, string? language) =>
        string.IsNullOrWhiteSpace(language)
            ? null
            : tracks.FirstOrDefault(track =>
                  !string.IsNullOrWhiteSpace(track.Language) &&
                  track.Language.Trim().Equals(language.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>One track a source offers, embedded or sidecar.</summary>
    public sealed record TrackCandidate(
        Guid Id, StreamType StreamType, string? Language, bool IsDefault, bool IsForced, bool IsExternal);
}

/// <summary>Which tracks a client should start on. Null means "leave it to the player".</summary>
public sealed record NativeTrackSelection(Guid? AudioStreamId, Guid? SubtitleStreamId);
