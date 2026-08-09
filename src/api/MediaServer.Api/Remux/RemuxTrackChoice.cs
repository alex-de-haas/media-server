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
    /// Video first, then audio, then subtitles: a player takes the first track of each kind as its
    /// default, so the order here is the viewer's answer rather than the file's.
    /// </summary>
    internal static IReadOnlyList<ulong> Resolve(
        MatroskaIndex index, int? audioStreamIndex, int? subtitleStreamIndex)
    {
        var chosen = new List<ulong>();

        if (Video(index) is { } video)
        {
            chosen.Add(video.Number);
        }

        if (First(index, IndexedTrackKind.Audio, audioStreamIndex) is { } audio)
        {
            chosen.Add(audio.Number);
        }

        // A subtitle track is only added when one was asked for: a player that finds one enables it, and
        // nobody asked for subtitles by not choosing any.
        if (subtitleStreamIndex is not null
            && First(index, IndexedTrackKind.Subtitle, subtitleStreamIndex) is { } subtitle)
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
    /// The track at that stream index if it is of the right kind and can be described, and otherwise the
    /// first that can be — an index that no longer matches is a stale preference, not a reason to play
    /// nothing.
    ///
    /// Only describable tracks are considered, and that is the whole point rather than an optimisation.
    /// The resolver offers a remux when <em>some</em> audio track can be packaged; taking the file's first
    /// audio track regardless would hand a viewer whose film leads with TrueHD a container with a picture
    /// and no sound. A choice that lands on such a track is treated exactly like a choice that lands on
    /// nothing.
    /// </summary>
    private static IndexedTrack? First(MatroskaIndex index, IndexedTrackKind kind, int? streamIndex)
    {
        var ofKind = index.Tracks
            .Where(track => track.Kind == kind && RemuxCodecs.WantsSamples(track))
            .ToList();
        if (ofKind.Count == 0)
        {
            return null;
        }

        if (streamIndex is { } wanted
            && ofKind.FirstOrDefault(track => track.Ordinal == wanted) is { } exact)
        {
            return exact;
        }

        // Subtitles are shown only when asked for by index; falling back to "some subtitle track" would
        // turn them on for a viewer who never wanted them.
        return kind == IndexedTrackKind.Subtitle ? null : ofKind[0];
    }
}
