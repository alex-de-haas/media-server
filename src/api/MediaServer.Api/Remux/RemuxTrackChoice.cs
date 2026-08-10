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
    /// Video, then **every** audio track that can be described, then every subtitle that can — each kind
    /// with the viewer's choice first, because a player takes the first track of a kind as its default.
    ///
    /// Carrying them all is what makes the player's own track menu work. An MP4 holding one audio track
    /// gives <c>AVPlayerViewController</c> nothing to choose between, so switching a dub would mean
    /// asking for a different URL and re-seating the player at the current time — a visible re-buffer,
    /// and a picker the client would have to build for itself. Carrying them all costs header: a sample
    /// table is around twelve bytes a sample once <c>stsz</c> and <c>co64</c> are counted, so an audio
    /// track of a feature film adds a couple of megabytes to what is fetched before the first frame.
    /// That is the trade, taken deliberately — see <c>docs/features/remux-streaming/feature.md</c>.
    /// </summary>
    internal static IReadOnlyList<ulong> Resolve(
        MatroskaIndex index, int? audioStreamIndex, int? subtitleStreamIndex)
    {
        var chosen = new List<ulong>();

        if (Video(index) is { } video)
        {
            chosen.Add(video.Number);
        }

        chosen.AddRange(Ordered(index, IndexedTrackKind.Audio, audioStreamIndex));
        chosen.AddRange(Ordered(index, IndexedTrackKind.Subtitle, subtitleStreamIndex));

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
    /// Every track of the kind that a sample entry can be written for, the viewer's choice first.
    ///
    /// Only describable tracks are considered, and that is the point rather than an optimisation. The
    /// resolver offers a remux when <em>some</em> audio track can be packaged; including the file's other
    /// ones regardless would put tracks in the player's menu that fall silent when selected. A choice
    /// landing on such a track is treated exactly like a choice landing on nothing: the order falls back
    /// to the file's own, and the default becomes the first track that actually plays.
    /// </summary>
    private static IEnumerable<ulong> Ordered(
        MatroskaIndex index, IndexedTrackKind kind, int? streamIndex)
    {
        var ofKind = index.Tracks
            .Where(track => track.Kind == kind && RemuxCodecs.WantsSamples(track))
            .ToList();

        var preferred = streamIndex is { } wanted
            ? ofKind.FirstOrDefault(track => track.Ordinal == wanted)
            : null;

        if (preferred is not null)
        {
            ofKind.Remove(preferred);
            ofKind.Insert(0, preferred);
        }

        return ofKind.Select(track => track.Number);
    }
}
