using System.Buffers.Binary;
using System.Text;

namespace MediaServer.Api.Probe;

/// <summary>
/// Reads what a container states about itself in its own header — duration, and for MP4/Matroska the track
/// list — without decoding anything. Costs a few hundred bytes and a handful of seeks per file: measured
/// over a 49-file, 52 GB library it read 11.4 KB in total, against 1.66 s of <c>ffprobe</c> process time.
/// <para>
/// Every method answers <c>null</c> rather than guessing. What a header cannot say — a transport stream's
/// duration, a bit depth the muxer omitted, the difference between HDR10 and HDR10+ — stays unanswered for
/// the caller to get elsewhere.
/// </para>
/// </summary>
internal static class ContainerHeader
{
    /// <summary>Containers whose header this can read at all; anything else is left to another provider.</summary>
    internal static bool Supports(string extension) => Kind(extension) is not HeaderKind.None;

    private enum HeaderKind { None, Mp4, Matroska, Avi }

    private static HeaderKind Kind(string extension) => extension.ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" or ".mov" or ".m4a" => HeaderKind.Mp4,
        // .mka (audio-only) and .mks (subtitle-only) are Matroska with the same element tree.
        ".mkv" or ".webm" or ".mka" or ".mks" => HeaderKind.Matroska,
        ".avi" => HeaderKind.Avi,
        _ => HeaderKind.None,
    };

    /// <summary>The file's duration, or null when its header does not state one.</summary>
    public static TimeSpan? ReadDuration(Stream stream, string extension) => Kind(extension) switch
    {
        HeaderKind.Mp4 => Mp4Duration(stream),
        HeaderKind.Matroska => MatroskaDuration(stream),
        HeaderKind.Avi => AviDuration(stream),
        _ => null,
    };

    /// <summary>
    /// The track list, in the order the container stores it. AVI is not covered: its stream headers carry no
    /// language or title at all, so a track list from one would be poorer than saying nothing.
    /// </summary>
    public static IReadOnlyList<HeaderTrack> ReadTracks(Stream stream, string extension) => Kind(extension) switch
    {
        HeaderKind.Mp4 => Mp4Tracks(stream),
        HeaderKind.Matroska => MatroskaTracks(stream),
        _ => [],
    };

    /// <summary>The muxer that wrote a Matroska file, for grouping divergence reports by writer.</summary>
    public static string? ReadWritingApp(Stream stream, string extension)
    {
        if (Kind(extension) != HeaderKind.Matroska)
        {
            return null;
        }

        if (FindElement(stream, 0, stream.Length, IdSegment, stopAt: null) is not { } segment ||
            FindElement(stream, segment.Start, segment.End, IdInfo, stopAt: IdCluster) is not { } info)
        {
            return null;
        }

        string? writing = null;
        string? muxing = null;
        var position = info.Start;
        while (position < info.End)
        {
            if (ReadElement(stream, position, info.End) is not { } element)
            {
                break;
            }

            if (element.Id == IdWritingApp) { writing = ReadString(stream, element.Start, element.End); }
            if (element.Id == IdMuxingApp) { muxing = ReadString(stream, element.Start, element.End); }
            position = element.End;
        }

        return writing ?? muxing;
    }

    // ---- MP4/MOV ----

    private static TimeSpan? Mp4Duration(Stream stream)
    {
        if (FindBox(stream, 0, stream.Length, "moov") is not { } moov ||
            FindBox(stream, moov.Start, moov.End, "mvhd") is not { } mvhd)
        {
            return null;
        }

        Span<byte> buffer = stackalloc byte[32];
        stream.Position = mvhd.Start;
        stream.ReadExactly(buffer[..4]);
        var version = buffer[0];

        // version 0: creation(4) modified(4) timescale(4) duration(4); version 1 widens the dates to 8.
        if (version == 0)
        {
            if (mvhd.Start + 20 > mvhd.End)
            {
                return null;
            }

            stream.Position = mvhd.Start;
            stream.ReadExactly(buffer[..20]);
            var timescale = BinaryPrimitives.ReadUInt32BigEndian(buffer[12..16]);
            var duration = BinaryPrimitives.ReadUInt32BigEndian(buffer[16..20]);
            return timescale == 0 ? null : TimeSpan.FromSeconds((double)duration / timescale);
        }

        if (mvhd.Start + 32 > mvhd.End)
        {
            return null;
        }

        stream.Position = mvhd.Start;
        stream.ReadExactly(buffer);
        var scale64 = BinaryPrimitives.ReadUInt32BigEndian(buffer[20..24]);
        var duration64 = BinaryPrimitives.ReadUInt64BigEndian(buffer[24..32]);
        return scale64 == 0 ? null : TimeSpan.FromSeconds((double)duration64 / scale64);
    }

    private static List<HeaderTrack> Mp4Tracks(Stream stream)
    {
        var tracks = new List<HeaderTrack>();
        if (FindBox(stream, 0, stream.Length, "moov") is not { } moov)
        {
            return tracks;
        }

        // ffprobe synthesizes a video stream for embedded cover art and places it after the first track,
        // shifting every later index. Job creation and client track selection both address streams by those
        // indexes, so the numbering here has to match rather than follow the file's own track order.
        var hasCoverArt = HasCoverArt(stream, moov);
        var position = moov.Start;
        var index = 0;
        while (position + 8 <= moov.End)
        {
            if (ReadBoxHeader(stream, position, moov.End) is not { } box)
            {
                break;
            }

            if (box.Type == "trak")
            {
                if (hasCoverArt && index == 1)
                {
                    tracks.Add(HeaderTrack.CoverArt(index++));
                }

                tracks.Add(DescribeMp4Track(stream, box.Start, box.End, index++));
            }

            position = box.Next;
        }

        return tracks;
    }

    /// <summary>Movie-level artwork lives in <c>moov/udta/meta/ilst/covr</c>, not in a track.</summary>
    private static bool HasCoverArt(Stream stream, (long Start, long End) moov)
    {
        if (FindBox(stream, moov.Start, moov.End, "udta") is not { } udta ||
            FindBox(stream, udta.Start, udta.End, "meta") is not { } meta)
        {
            return false;
        }

        // A full-box meta carries a 4-byte version/flags before its children; a QuickTime one does not.
        foreach (var childStart in new[] { meta.Start + 4, meta.Start })
        {
            if (FindBox(stream, childStart, meta.End, "ilst") is { } ilst &&
                FindBox(stream, ilst.Start, ilst.End, "covr") is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static HeaderTrack DescribeMp4Track(Stream stream, long from, long to, int index)
    {
        long width = 0, height = 0;
        var kind = HeaderTrackKind.Other;
        string codec = "?", language = "und";
        string? title = null;
        int channels = 0, sampleRate = 0, transfer = 0;
        var enabled = true;
        var dolbyVision = false;

        if (FindBox(stream, from, to, "tkhd") is { } tkhd)
        {
            Span<byte> header = stackalloc byte[4];
            stream.Position = tkhd.Start;
            stream.ReadExactly(header);
            var version = header[0];
            // Bit 0 of the 24-bit flags is track_enabled; a disabled track is present but not for playback,
            // which is how ffmpeg marks every audio track after the first in an MP4.
            enabled = (header[3] & 0x01) != 0;
            var payloadEnd = version == 0 ? tkhd.Start + 84 : tkhd.Start + 96;
            if (payloadEnd <= tkhd.End)
            {
                Span<byte> dimensions = stackalloc byte[8];
                stream.Position = payloadEnd - 8;
                stream.ReadExactly(dimensions);
                // 16.16 fixed point.
                width = BinaryPrimitives.ReadUInt32BigEndian(dimensions[..4]) >> 16;
                height = BinaryPrimitives.ReadUInt32BigEndian(dimensions[4..8]) >> 16;
            }
        }

        if (FindBox(stream, from, to, "mdia") is { } mdia)
        {
            language = Mp4Language(stream, mdia) ?? "und";
            if (FindBox(stream, mdia.Start, mdia.End, "hdlr") is { } hdlr && hdlr.Start + 12 <= hdlr.End)
            {
                kind = ReadAscii(stream, hdlr.Start + 8, hdlr.Start + 12) switch
                {
                    "vide" => HeaderTrackKind.Video,
                    "soun" => HeaderTrackKind.Audio,
                    "sbtl" or "text" or "subt" => HeaderTrackKind.Subtitle,
                    _ => HeaderTrackKind.Other,
                };
            }

            if (FindBox(stream, mdia.Start, mdia.End, "minf") is { } minf &&
                FindBox(stream, minf.Start, minf.End, "stbl") is { } stbl &&
                FindBox(stream, stbl.Start, stbl.End, "stsd") is { } stsd &&
                ReadBoxHeader(stream, stsd.Start + 8, stsd.End) is { } entry)
            {
                codec = entry.Type;
                if (kind == HeaderTrackKind.Audio && entry.Start + 28 <= entry.End)
                {
                    // AudioSampleEntry: reserved(6) dataRef(2) version(2) revision(2) vendor(4)
                    //                   channels(2) sampleSize(2) preDefined(2) reserved(2) rate(4 as 16.16)
                    Span<byte> audio = stackalloc byte[20];
                    stream.Position = entry.Start + 8;
                    stream.ReadExactly(audio);
                    channels = BinaryPrimitives.ReadUInt16BigEndian(audio[8..10]);
                    sampleRate = (int)(BinaryPrimitives.ReadUInt32BigEndian(audio[16..20]) >> 16);
                }
                else if (kind == HeaderTrackKind.Video)
                {
                    (transfer, dolbyVision) = Mp4VideoColour(stream, entry.Start + 78, entry.End);
                }
            }
        }

        if (FindBox(stream, from, to, "udta") is { } udta && FindBox(stream, udta.Start, udta.End, "name") is { } name)
        {
            title = ReadString(stream, name.Start, name.End);
        }

        return new HeaderTrack(
            index, kind, codec, NormalizeLanguage(language), title,
            IsDefault: enabled, IsForced: false,
            width > 0 ? (int)width : null, height > 0 ? (int)height : null,
            null, null,
            HdrFrom(transfer, dolbyVision),
            channels > 0 ? channels : null,
            sampleRate > 0 ? sampleRate : null);
    }

    /// <summary>The packed trio of 5-bit letters in <c>mdhd</c>, or the BCP-47 string in <c>elng</c>.</summary>
    private static string? Mp4Language(Stream stream, (long Start, long End) mdia)
    {
        if (FindBox(stream, mdia.Start, mdia.End, "elng") is { } elng && elng.Start + 4 < elng.End)
        {
            return ReadString(stream, elng.Start + 4, elng.End);
        }

        if (FindBox(stream, mdia.Start, mdia.End, "mdhd") is not { } mdhd)
        {
            return null;
        }

        Span<byte> buffer = stackalloc byte[4];
        stream.Position = mdhd.Start;
        stream.ReadExactly(buffer);
        var offset = buffer[0] == 0 ? mdhd.Start + 20 : mdhd.Start + 32;
        if (offset + 2 > mdhd.End)
        {
            return null;
        }

        stream.Position = offset;
        stream.ReadExactly(buffer[..2]);
        var packed = BinaryPrimitives.ReadUInt16BigEndian(buffer[..2]);
        return new string(
        [
            (char)(((packed >> 10) & 0x1F) + 0x60),
            (char)(((packed >> 5) & 0x1F) + 0x60),
            (char)((packed & 0x1F) + 0x60),
        ]);
    }

    /// <summary>Scans a VisualSampleEntry's children for the colour box and the Dolby Vision record.</summary>
    private static (int Transfer, bool DolbyVision) Mp4VideoColour(Stream stream, long from, long to)
    {
        var transfer = 0;
        var dolbyVision = false;
        var position = from;
        Span<byte> colour = stackalloc byte[10];
        while (position + 8 <= to)
        {
            if (ReadBoxHeader(stream, position, to) is not { } box)
            {
                break;
            }

            if (box.Type == "colr" && box.Start + 10 <= box.End)
            {
                stream.Position = box.Start;
                stream.ReadExactly(colour);
                // "nclx"/"nclc": primaries(2) transfer(2) matrix(2) follow the type.
                if (Encoding.ASCII.GetString(colour[..4]) is "nclx" or "nclc")
                {
                    transfer = BinaryPrimitives.ReadUInt16BigEndian(colour[6..8]);
                }
            }
            else if (box.Type is "dvcC" or "dvvC")
            {
                dolbyVision = true;
            }

            position = box.Next;
        }

        return (transfer, dolbyVision);
    }

    private static (long Start, long End)? FindBox(Stream stream, long from, long to, string type)
    {
        var position = from;
        while (position + 8 <= to)
        {
            if (ReadBoxHeader(stream, position, to) is not { } box)
            {
                return null;
            }

            if (box.Type == type)
            {
                return (box.Start, box.End);
            }

            position = box.Next;
        }

        return null;
    }

    private static (string Type, long Start, long End, long Next)? ReadBoxHeader(Stream stream, long position, long limit)
    {
        if (position + 8 > limit)
        {
            return null;
        }

        Span<byte> header = stackalloc byte[16];
        stream.Position = position;
        stream.ReadExactly(header[..8]);
        long size = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
        var type = Encoding.ASCII.GetString(header[4..8]);
        var payload = position + 8;
        if (size == 1) // 64-bit largesize follows the header
        {
            stream.ReadExactly(header[8..16]);
            size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..16]);
            payload += 8;
        }
        else if (size == 0) // extends to the end of its parent
        {
            size = limit - position;
        }

        return size < 8 || position + size > limit ? null : (type, payload, position + size, position + size);
    }

    // ---- Matroska ----

    private const ulong IdSegment = 0x18538067, IdInfo = 0x1549A966, IdCluster = 0x1F43B675;
    private const ulong IdTimestampScale = 0x2AD7B1, IdDuration = 0x4489;
    private const ulong IdWritingApp = 0x5741, IdMuxingApp = 0x4D80;
    private const ulong IdTracks = 0x1654AE6B, IdTrackEntry = 0xAE;
    private const ulong IdTrackType = 0x83, IdCodecId = 0x86;
    private const ulong IdLanguage = 0x22B59C, IdLanguageBcp47 = 0x22B59D, IdName = 0x536E;
    private const ulong IdFlagDefault = 0x88, IdFlagForced = 0x55AA;
    private const ulong IdVideo = 0xE0, IdPixelWidth = 0xB0, IdPixelHeight = 0xBA, IdDefaultDuration = 0x23E383;
    private const ulong IdColour = 0x55B0, IdTransferCharacteristics = 0x55BA, IdBitsPerChannel = 0x55B2;
    private const ulong IdAudio = 0xE1, IdChannels = 0x9F, IdSamplingFrequency = 0xB5;

    private static TimeSpan? MatroskaDuration(Stream stream)
    {
        if (FindElement(stream, 0, stream.Length, IdSegment, stopAt: null) is not { } segment ||
            FindElement(stream, segment.Start, segment.End, IdInfo, stopAt: IdCluster) is not { } info)
        {
            return null;
        }

        double scale = 1_000_000; // TimestampScale default: 1 ms expressed in nanoseconds
        double? duration = null;
        var position = info.Start;
        while (position < info.End)
        {
            if (ReadElement(stream, position, info.End) is not { } element)
            {
                break;
            }

            if (element.Id == IdTimestampScale) { scale = ReadUInt(stream, element.Start, element.End); }
            if (element.Id == IdDuration) { duration = ReadFloat(stream, element.Start, element.End); }
            position = element.End;
        }

        return duration is { } value ? TimeSpan.FromSeconds(value * scale / 1_000_000_000) : null;
    }

    private static List<HeaderTrack> MatroskaTracks(Stream stream)
    {
        var tracks = new List<HeaderTrack>();
        if (FindElement(stream, 0, stream.Length, IdSegment, stopAt: null) is not { } segment ||
            FindElement(stream, segment.Start, segment.End, IdTracks, stopAt: IdCluster) is not { } list)
        {
            return tracks;
        }

        var position = list.Start;
        var index = 0;
        while (position < list.End)
        {
            if (ReadElement(stream, position, list.End) is not { } entry)
            {
                break;
            }

            if (entry.Id == IdTrackEntry)
            {
                tracks.Add(DescribeMatroskaTrack(stream, entry.Start, entry.End, index++));
            }

            position = entry.End;
        }

        return tracks;
    }

    private static HeaderTrack DescribeMatroskaTrack(Stream stream, long from, long to, int index)
    {
        ulong type = 0, flagDefault = 1, flagForced = 0, channels = 0, defaultDuration = 0;
        long width = 0, height = 0;
        int transfer = 0, bitDepth = 0, sampleRate = 0;
        // Matroska's spec makes "eng" the default for an absent Language element, but that default is not
        // applied here: a file that never stated a language has not claimed English, and asserting it would
        // mislabel an untagged Russian dub — exactly the guess this reader exists to avoid. Left unknown,
        // AudioTrackLabeler infers a language from the path instead, which is what it is for. In practice
        // the element is almost always present anyway: ffmpeg writes an explicit "und", which normalizes to
        // no language, matching what ffprobe reports for the same file.
        string codec = "?";
        string? language = null;
        string? title = null;

        var position = from;
        while (position < to)
        {
            if (ReadElement(stream, position, to) is not { } element)
            {
                break;
            }

            switch (element.Id)
            {
                case IdTrackType: type = ReadUInt(stream, element.Start, element.End); break;
                case IdCodecId: codec = ReadString(stream, element.Start, element.End) ?? "?"; break;
                case IdLanguage or IdLanguageBcp47: language = ReadString(stream, element.Start, element.End) ?? language; break;
                case IdName: title = ReadString(stream, element.Start, element.End); break;
                case IdFlagDefault: flagDefault = ReadUInt(stream, element.Start, element.End); break;
                case IdFlagForced: flagForced = ReadUInt(stream, element.Start, element.End); break;
                case IdVideo:
                    var video = element;
                    var videoPosition = video.Start;
                    while (videoPosition < video.End)
                    {
                        if (ReadElement(stream, videoPosition, video.End) is not { } child) { break; }
                        if (child.Id == IdPixelWidth) { width = (long)ReadUInt(stream, child.Start, child.End); }
                        if (child.Id == IdPixelHeight) { height = (long)ReadUInt(stream, child.Start, child.End); }
                        if (child.Id == IdColour)
                        {
                            var colourPosition = child.Start;
                            while (colourPosition < child.End)
                            {
                                if (ReadElement(stream, colourPosition, child.End) is not { } colour) { break; }
                                if (colour.Id == IdTransferCharacteristics) { transfer = (int)ReadUInt(stream, colour.Start, colour.End); }
                                if (colour.Id == IdBitsPerChannel) { bitDepth = (int)ReadUInt(stream, colour.Start, colour.End); }
                                colourPosition = colour.End;
                            }
                        }

                        videoPosition = child.End;
                    }

                    break;
                case IdAudio:
                    var audio = element;
                    var audioPosition = audio.Start;
                    while (audioPosition < audio.End)
                    {
                        if (ReadElement(stream, audioPosition, audio.End) is not { } child) { break; }
                        if (child.Id == IdChannels) { channels = ReadUInt(stream, child.Start, child.End); }
                        if (child.Id == IdSamplingFrequency) { sampleRate = (int)(ReadFloat(stream, child.Start, child.End) ?? 0); }
                        audioPosition = child.End;
                    }

                    break;
                case IdDefaultDuration: defaultDuration = ReadUInt(stream, element.Start, element.End); break;
            }

            position = element.End;
        }

        return new HeaderTrack(
            index,
            type switch { 1 => HeaderTrackKind.Video, 2 => HeaderTrackKind.Audio, 17 => HeaderTrackKind.Subtitle, _ => HeaderTrackKind.Other },
            codec,
            NormalizeLanguage(language),
            title,
            flagDefault != 0,
            flagForced != 0,
            width > 0 ? (int)width : null,
            height > 0 ? (int)height : null,
            // DefaultDuration is one frame's length in nanoseconds.
            defaultDuration > 0 ? 1_000_000_000d / defaultDuration : null,
            bitDepth > 0 ? bitDepth : null,
            HdrFrom(transfer, dolbyVision: false),
            channels > 0 ? (int)channels : null,
            sampleRate > 0 ? sampleRate : null);
    }

    private static (long Start, long End)? FindElement(Stream stream, long from, long to, ulong id, ulong? stopAt)
    {
        var position = from;
        while (position < to)
        {
            if (ReadElement(stream, position, to) is not { } element || (stopAt is { } stop && element.Id == stop))
            {
                return null;
            }

            if (element.Id == id)
            {
                return (element.Start, element.End);
            }

            position = element.End;
        }

        return null;
    }

    private static (ulong Id, long Start, long End)? ReadElement(Stream stream, long position, long limit)
    {
        if (position >= limit)
        {
            return null;
        }

        stream.Position = position;
        var id = ReadVint(stream, keepMarker: true, out var idLength);
        if (idLength == 0)
        {
            return null;
        }

        var size = ReadVint(stream, keepMarker: false, out var sizeLength);
        if (sizeLength == 0)
        {
            return null;
        }

        var start = position + idLength + sizeLength;
        // An "unknown size" VINT (every data bit set) means the element runs to the end of its parent —
        // live-muxed Segments do this, and treating it as a huge size would run past the file.
        var unknown = size == (ulong.MaxValue >> (64 - (7 * sizeLength)));
        var end = unknown ? limit : Math.Min(limit, start + (long)size);
        return end < start ? null : (id, start, end);
    }

    private static ulong ReadVint(Stream stream, bool keepMarker, out int length)
    {
        var first = stream.ReadByte();
        if (first < 0)
        {
            length = 0;
            return 0;
        }

        length = 1;
        var mask = 0x80;
        while (length <= 8 && (first & mask) == 0)
        {
            mask >>= 1;
            length++;
        }

        if (length > 8)
        {
            length = 0;
            return 0;
        }

        var value = keepMarker ? (ulong)first : (ulong)(first & (mask - 1));
        for (var i = 1; i < length; i++)
        {
            var next = stream.ReadByte();
            if (next < 0)
            {
                length = 0;
                return 0;
            }

            value = (value << 8) | (byte)next;
        }

        return value;
    }

    // ---- AVI ----

    private static TimeSpan? AviDuration(Stream stream)
    {
        if (stream.Length < 56)
        {
            return null;
        }

        stream.Position = 8; // past "RIFF" + size; the form type follows
        if (ReadAscii(stream, 8, 12) != "AVI ")
        {
            return null;
        }

        // hdrl is the first LIST; avih is its first chunk.
        Span<byte> header = stackalloc byte[12];
        stream.Position = 12;
        stream.ReadExactly(header);
        if (Encoding.ASCII.GetString(header[..4]) != "LIST" || Encoding.ASCII.GetString(header[8..12]) != "hdrl")
        {
            return null;
        }

        var hdrlEnd = 20 + (long)BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
        stream.ReadExactly(header[..8]);
        if (Encoding.ASCII.GetString(header[..4]) != "avih")
        {
            return null;
        }

        Span<byte> avih = stackalloc byte[24];
        stream.ReadExactly(avih);
        var microsecondsPerFrame = BinaryPrimitives.ReadUInt32LittleEndian(avih[..4]);
        var totalFrames = BinaryPrimitives.ReadUInt32LittleEndian(avih[16..20]);
        if (microsecondsPerFrame == 0)
        {
            return null;
        }

        // OpenDML (AVI 2.0): past ~2 GB the stream continues in further `RIFF AVIX` segments, and
        // avih.TotalFrames then counts only the first one — a long file reads short by whatever spilled over.
        // The true total is in the extended header. Two files in the development library read 1252 s and
        // 715 s short before this was handled.
        var extended = OpenDmlTotalFrames(stream, 24, Math.Min(hdrlEnd, stream.Length));
        if (extended > totalFrames)
        {
            totalFrames = extended;
        }

        return TimeSpan.FromSeconds(totalFrames * (double)microsecondsPerFrame / 1_000_000);
    }

    private static uint OpenDmlTotalFrames(Stream stream, long from, long to)
    {
        var position = from;
        Span<byte> header = stackalloc byte[12];
        while (position + 8 <= to)
        {
            stream.Position = position;
            stream.ReadExactly(header[..8]);
            var fourCc = Encoding.ASCII.GetString(header[..4]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
            if (fourCc == "LIST" && position + 12 <= to)
            {
                stream.ReadExactly(header[8..12]);
                if (Encoding.ASCII.GetString(header[8..12]) == "odml")
                {
                    stream.ReadExactly(header[..8]);
                    if (Encoding.ASCII.GetString(header[..4]) == "dmlh")
                    {
                        stream.ReadExactly(header[..4]);
                        return BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
                    }
                }
            }

            position += 8 + size + (size & 1); // RIFF chunks are word-aligned
        }

        return 0;
    }

    // ---- shared readers ----

    /// <summary>
    /// HDR from the transfer function alone: 16 is PQ (SMPTE ST 2084 — HDR10, HDR10+ and the PQ-based Dolby
    /// Vision profiles), 18 is HLG. A zero means the container carried no colour information at all, which
    /// is "unknown" rather than SDR — the authoritative copy may sit in the codec bitstream, out of reach
    /// here. Which flavour of HDR it is cannot be told apart from a container header, so PQ reports the
    /// generic value unless a Dolby Vision configuration record settles it.
    /// </summary>
    private static HeaderHdr HdrFrom(int transferCharacteristics, bool dolbyVision) =>
        dolbyVision ? HeaderHdr.DolbyVision
        : transferCharacteristics switch
        {
            16 => HeaderHdr.Hdr,
            18 => HeaderHdr.Hlg,
            0 => HeaderHdr.Unknown,
            _ => HeaderHdr.Sdr,
        };

    /// <summary>"und" is the container saying it has no language, not a language.</summary>
    private static string? NormalizeLanguage(string? raw) =>
        raw is { Length: > 0 } value && !value.Equals("und", StringComparison.OrdinalIgnoreCase) ? value : null;

    private static ulong ReadUInt(Stream stream, long start, long end)
    {
        stream.Position = start;
        ulong value = 0;
        for (var i = start; i < end && i < start + 8; i++)
        {
            var next = stream.ReadByte();
            if (next < 0)
            {
                break;
            }

            value = (value << 8) | (byte)next;
        }

        return value;
    }

    private static double? ReadFloat(Stream stream, long start, long end)
    {
        var length = (int)(end - start);
        if (length is not (4 or 8))
        {
            return null;
        }

        Span<byte> buffer = stackalloc byte[8];
        stream.Position = start;
        stream.ReadExactly(buffer[..length]);
        return length == 4
            ? BinaryPrimitives.ReadSingleBigEndian(buffer[..4])
            : BinaryPrimitives.ReadDoubleBigEndian(buffer[..8]);
    }

    private static string? ReadString(Stream stream, long start, long end)
    {
        var length = end - start;
        if (length <= 0 || length > 4096)
        {
            return null;
        }

        var buffer = new byte[length];
        stream.Position = start;
        stream.ReadExactly(buffer);
        var text = Encoding.UTF8.GetString(buffer).TrimEnd('\0');
        return text.Length == 0 ? null : text;
    }

    private static string ReadAscii(Stream stream, long start, long end)
    {
        var length = (int)Math.Min(end - start, 16);
        if (length <= 0)
        {
            return string.Empty;
        }

        Span<byte> buffer = stackalloc byte[16];
        stream.Position = start;
        stream.ReadExactly(buffer[..length]);
        return Encoding.ASCII.GetString(buffer[..length]);
    }
}
