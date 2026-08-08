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

        if (First(index, IndexedTrackKind.Video, null) is { } video)
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
            && First(index, IndexedTrackKind.Subtitle, subtitleStreamIndex) is { } subtitle
            && SubtitleText.IsConvertible(subtitle.CodecId))
        {
            chosen.Add(subtitle.Number);
        }

        return chosen;
    }

    /// <summary>
    /// The track at that stream index if it is of the right kind, and otherwise the first of the kind —
    /// an index that no longer matches is a stale preference, not a reason to play nothing.
    /// </summary>
    private static IndexedTrack? First(MatroskaIndex index, IndexedTrackKind kind, int? streamIndex)
    {
        var ofKind = index.Tracks.Where(track => track.Kind == kind).ToList();
        if (ofKind.Count == 0)
        {
            return null;
        }

        if (streamIndex is { } wanted
            && ofKind.FirstOrDefault(track => track.Ordinal == wanted) is { } exact)
        {
            return exact;
        }

        return kind == IndexedTrackKind.Subtitle ? null : ofKind[0];
    }
}
