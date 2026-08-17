namespace MediaServer.Api.Remux;

/// <summary>
/// Turns the viewer's choice into the tracks the synthesiser is asked for.
///
/// The choice arrives as stream indexes, because that is what the database stores and what
/// <see cref="Native.Playback.NativeTrackSelector"/> resolves a stored preference into. A stream index is
/// the track's position in the file, which is <see cref="IndexedTrack.Ordinal"/> — deliberately not its
/// Matroska track number, since a file may number its tracks however it likes.
/// </summary>
internal static class RemuxTrackChoice
{
    /// <summary>
    /// The video, the chosen audio track, and the chosen subtitle — **one of each, not all of them**.
    ///
    /// The reason is measured rather than argued.
    ///
    /// Carrying every track made the player's own menu work — no second request to change a dub. It also
    /// made the header enormous: a sample table costs about twelve bytes a sample once <c>stsz</c> and
    /// <c>co64</c> are counted, and one sample per chunk is forced on us because the source interleaves
    /// its tracks. A film with seven audio tracks and eight subtitles came to **29.5 MB of tables** — all
    /// of which AVFoundation reads and parses *before the first frame*, which is the measurement this
    /// whole design was built on.
    ///
    /// On an Apple TV that was two and a half seconds of transfer before anything could start, and
    /// playback that stuttered for the rest of the film. Titles with two tracks played perfectly on the
    /// same hardware and the same network, which is what identified the size rather than the bitrate as
    /// the cause.
    ///
    /// So a viewer changing track costs a new URL and a re-seated player. That is a second of
    /// interruption when they ask for it, against a film that plays.
    /// </summary>
    internal static IReadOnlyList<ulong> Resolve(
        MatroskaIndex index, int? audioStreamIndex, int? subtitleStreamIndex)
    {
        var chosen = new List<ulong>();

        if (Video(index) is { } video)
        {
            chosen.Add(video.Number);
        }

        if (Pick(index, IndexedTrackKind.Audio, audioStreamIndex) is { } audio)
        {
            chosen.Add(audio.Number);
        }

        // A subtitle only when one was asked for *and found*: a track in the container is a track the
        // player may turn on, and nobody asked for subtitles by not choosing any — nor did they ask for
        // some other language by choosing one that has since gone.
        if (subtitleStreamIndex is not null
            && Pick(index, IndexedTrackKind.Subtitle, subtitleStreamIndex, exact: true) is { } subtitle)
        {
            chosen.Add(subtitle.Number);
        }

        return chosen;
    }

    /// <summary>
    /// The first video track a sample entry can be written for, which is not always the first video track.
    /// A file may carry a still image as a real video track — cover art that its muxer did not flag — and
    /// taking that one would produce an output with no picture at all.
    /// </summary>
    internal static IndexedTrack? Video(MatroskaIndex index) =>
        index.Tracks.FirstOrDefault(track =>
            track.Kind == IndexedTrackKind.Video && RemuxCodecs.CanPackageVideo(track));

    /// <summary>
    /// The track of that kind to carry: the one at the viewer's stream index, or — unless
    /// <paramref name="exact"/> — the first that can be described at all.
    ///
    /// Only describable tracks are considered, and that is the point rather than an optimisation. The
    /// resolver offers a remux when <em>some</em> audio track can be packaged; taking the file's first
    /// regardless would hand a viewer whose film leads with TrueHD a container with a picture and no
    /// sound.
    ///
    /// Returns a track rather than its number, because a number has no absent value: the default of
    /// <c>ulong</c> is zero, which is a track a file may really have.
    /// </summary>
    private static IndexedTrack? Pick(
        MatroskaIndex index, IndexedTrackKind kind, int? streamIndex, bool exact = false)
    {
        var ofKind = index.Tracks
            .Where(track => track.Kind == kind && RemuxCodecs.WantsSamples(track))
            .ToList();

        if (streamIndex is { } wanted
            && ofKind.FirstOrDefault(track => track.Ordinal == wanted) is { } match)
        {
            return match;
        }

        return exact ? null : ofKind.FirstOrDefault();
    }
}
