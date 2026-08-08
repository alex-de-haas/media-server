using MediaServer.Api.Probe;

namespace MediaServer.Api.Remux;

/// <summary>
/// Walks a Matroska file into the sample tables an MP4 needs, without reading a single frame of media.
///
/// The walk touches element headers only — cluster and block headers, and the small elements in
/// <c>Tracks</c> — and seeks past every payload. On the dev machine a 26 GB film costs about 27 s cold, and
/// the result is a few megabytes. That ratio is the feature: the index can be built in the background at
/// scan time, and playback then computes a container from it with nothing produced and nothing stored.
///
/// See <c>docs/features/remux-streaming/plan.md</c>. A Python prototype of the same walk lives in
/// <c>scripts/remux-prototype/</c>, along with the measurements and the traps.
/// </summary>
internal static class MatroskaIndexer
{
    private const ulong IdSegment = 0x18538067, IdInfo = 0x1549A966, IdTracks = 0x1654AE6B;
    private const ulong IdCluster = 0x1F43B675, IdTimestampScale = 0x2AD7B1, IdDuration = 0x4489;
    private const ulong IdClusterTimestamp = 0xE7, IdSimpleBlock = 0xA3, IdBlockGroup = 0xA0;
    private const ulong IdBlock = 0xA1, IdReferenceBlock = 0xFB, IdBlockDuration = 0x9B;
    private const ulong IdTrackEntry = 0xAE, IdTrackNumber = 0xD7, IdTrackType = 0x83, IdCodecId = 0x86;
    private const ulong IdCodecPrivate = 0x63A2, IdDefaultDuration = 0x23E383;
    private const ulong IdLanguage = 0x22B59C, IdName = 0x536E;
    private const ulong IdVideo = 0xE0, IdPixelWidth = 0xB0, IdPixelHeight = 0xBA;
    private const ulong IdDisplayWidth = 0x54B0, IdDisplayHeight = 0x54BA;
    private const ulong IdColour = 0x55B0, IdMatrixCoefficients = 0x55B1, IdRange = 0x55B9;
    private const ulong IdTransferCharacteristics = 0x55BA, IdPrimaries = 0x55BB;
    private const ulong IdAudio = 0xE1, IdChannels = 0x9F, IdSamplingFrequency = 0xB5;
    private const ulong IdBlockAdditionMapping = 0x41E4, IdBlockAddIdName = 0x41A4, IdBlockAddIdExtraData = 0x41ED;

    public static MatroskaIndex Build(Stream stream, CancellationToken cancellationToken = default)
    {
        var index = new MatroskaIndex { SourceLength = stream.Length };

        if (Ebml.Find(stream, 0, stream.Length, IdSegment, stopAt: null) is not { } segment)
        {
            return index;
        }

        ReadInfo(stream, index, segment);
        ReadTracks(stream, index, segment);
        ReadClusters(stream, index, segment, cancellationToken);
        return index;
    }

    private static void ReadInfo(Stream stream, MatroskaIndex index, Ebml.Element segment)
    {
        if (Ebml.Find(stream, segment.Start, segment.End, IdInfo, stopAt: IdCluster) is not { } info)
        {
            return;
        }

        for (var position = info.Start; Ebml.Read(stream, position, info.End) is { } element; position = element.End)
        {
            if (element.Id == IdTimestampScale)
            {
                index.TimestampScale = (long)Ebml.ReadUInt(stream, element.Start, element.End);
            }
            else if (element.Id == IdDuration)
            {
                index.DurationTicks = Ebml.ReadFloat(stream, element.Start, element.End) ?? 0;
            }
        }
    }

    private static void ReadTracks(Stream stream, MatroskaIndex index, Ebml.Element segment)
    {
        if (Ebml.Find(stream, segment.Start, segment.End, IdTracks, stopAt: IdCluster) is not { } tracks)
        {
            return;
        }

        var ordinal = 0;
        for (var position = tracks.Start; Ebml.Read(stream, position, tracks.End) is { } entry; position = entry.End)
        {
            if (entry.Id != IdTrackEntry)
            {
                continue;
            }

            // The track number is not known until it is read, so the entry is described into a scratch
            // object and only then given its identity.
            var number = FindTrackNumber(stream, entry);
            if (number == 0)
            {
                continue;
            }

            var track = new IndexedTrack { Number = number, Ordinal = ordinal++ };
            DescribeTrack(stream, track, entry.Start, entry.End);
            index.Tracks.Add(track);
        }
    }

    private static ulong FindTrackNumber(Stream stream, Ebml.Element entry)
    {
        var found = Ebml.Find(stream, entry.Start, entry.End, IdTrackNumber, stopAt: null);
        return found is { } element ? Ebml.ReadUInt(stream, element.Start, element.End) : 0;
    }

    private static void DescribeTrack(Stream stream, IndexedTrack track, long from, long to)
    {
        for (var position = from; Ebml.Read(stream, position, to) is { } element; position = element.End)
        {
            switch (element.Id)
            {
                case IdTrackType:
                    track.Kind = (IndexedTrackKind)Ebml.ReadUInt(stream, element.Start, element.End);
                    break;
                case IdCodecId:
                    track.CodecId = Ebml.ReadString(stream, element.Start, element.End) ?? string.Empty;
                    break;
                case IdCodecPrivate:
                    // Already the payload an MP4 configuration box wants — hvcC for HEVC, avcC for AVC.
                    track.CodecPrivate = Ebml.ReadBytes(stream, element.Start, element.End);
                    break;
                case IdDefaultDuration:
                    track.DefaultDuration = (long)Ebml.ReadUInt(stream, element.Start, element.End);
                    break;
                case IdLanguage:
                    track.Language = Ebml.ReadString(stream, element.Start, element.End);
                    break;
                case IdName:
                    track.Name = Ebml.ReadString(stream, element.Start, element.End);
                    break;
                case IdVideo:
                    DescribeVideo(stream, track, element.Start, element.End);
                    break;
                case IdAudio:
                    DescribeAudio(stream, track, element.Start, element.End);
                    break;
                case IdBlockAdditionMapping:
                    DescribeBlockAdditions(stream, track, element.Start, element.End);
                    break;
            }
        }
    }

    private static void DescribeVideo(Stream stream, IndexedTrack track, long from, long to)
    {
        for (var position = from; Ebml.Read(stream, position, to) is { } element; position = element.End)
        {
            switch (element.Id)
            {
                case IdPixelWidth:
                    track.Width = (int)Ebml.ReadUInt(stream, element.Start, element.End);
                    break;
                case IdPixelHeight:
                    track.Height = (int)Ebml.ReadUInt(stream, element.Start, element.End);
                    break;
                case IdDisplayWidth:
                    track.DisplayWidth = (int)Ebml.ReadUInt(stream, element.Start, element.End);
                    break;
                case IdDisplayHeight:
                    track.DisplayHeight = (int)Ebml.ReadUInt(stream, element.Start, element.End);
                    break;
                case IdColour:
                    DescribeColour(stream, track, element.Start, element.End);
                    break;
            }
        }
    }

    private static void DescribeColour(Stream stream, IndexedTrack track, long from, long to)
    {
        for (var position = from; Ebml.Read(stream, position, to) is { } element; position = element.End)
        {
            switch (element.Id)
            {
                case IdPrimaries:
                    track.ColourPrimaries = (int)Ebml.ReadUInt(stream, element.Start, element.End);
                    break;
                case IdTransferCharacteristics:
                    track.TransferCharacteristics = (int)Ebml.ReadUInt(stream, element.Start, element.End);
                    break;
                case IdMatrixCoefficients:
                    track.MatrixCoefficients = (int)Ebml.ReadUInt(stream, element.Start, element.End);
                    break;
                case IdRange:
                    // 1 is broadcast range, 2 is full; anything else is "unspecified".
                    track.FullRange = Ebml.ReadUInt(stream, element.Start, element.End) == 2;
                    break;
            }
        }
    }

    private static void DescribeAudio(Stream stream, IndexedTrack track, long from, long to)
    {
        for (var position = from; Ebml.Read(stream, position, to) is { } element; position = element.End)
        {
            switch (element.Id)
            {
                case IdChannels:
                    track.Channels = (int)Ebml.ReadUInt(stream, element.Start, element.End);
                    break;
                case IdSamplingFrequency:
                    track.SampleRate = Ebml.ReadFloat(stream, element.Start, element.End) ?? 0;
                    break;
            }
        }
    }

    /// <summary>
    /// Where Matroska keeps the Dolby Vision configuration: a <c>BlockAdditionMapping</c> whose name says so
    /// and whose extra data is the <c>dvcC</c>/<c>dvvC</c> payload verbatim. It is carried into the MP4 as
    /// it stands — nothing about the profile is inferred, and nothing is read out of the RPU.
    /// </summary>
    private static void DescribeBlockAdditions(Stream stream, IndexedTrack track, long from, long to)
    {
        string? name = null;
        byte[]? extra = null;

        for (var position = from; Ebml.Read(stream, position, to) is { } element; position = element.End)
        {
            if (element.Id == IdBlockAddIdName)
            {
                name = Ebml.ReadString(stream, element.Start, element.End);
            }
            else if (element.Id == IdBlockAddIdExtraData)
            {
                extra = Ebml.ReadBytes(stream, element.Start, element.End, max: 4096);
            }
        }

        if (extra is not null && name is not null && name.Contains("Dolby Vision", StringComparison.OrdinalIgnoreCase))
        {
            track.DolbyVisionConfiguration = extra;
        }
    }

    private static void ReadClusters(
        Stream stream, MatroskaIndex index, Ebml.Element segment, CancellationToken cancellationToken)
    {
        var byNumber = index.Tracks.ToDictionary(track => track.Number);

        for (var position = segment.Start;
             Ebml.Read(stream, position, segment.End) is { } element;
             position = element.End)
        {
            if (element.Id != IdCluster)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            ReadCluster(stream, byNumber, element.Start, element.End);
        }
    }

    private static void ReadCluster(
        Stream stream, Dictionary<ulong, IndexedTrack> tracks, long from, long to)
    {
        long clusterTimestamp = 0;

        for (var position = from; Ebml.Read(stream, position, to) is { } element; position = element.End)
        {
            switch (element.Id)
            {
                case IdClusterTimestamp:
                    clusterTimestamp = (long)Ebml.ReadUInt(stream, element.Start, element.End);
                    break;
                case IdSimpleBlock:
                    ReadBlock(stream, tracks, element, clusterTimestamp, keyframeFromFlags: true);
                    break;
                case IdBlockGroup:
                    ReadBlockGroup(stream, tracks, element, clusterTimestamp);
                    break;
            }
        }
    }

    /// <summary>
    /// A <c>BlockGroup</c> carries the keyframe answer differently from a <c>SimpleBlock</c>: there is no
    /// flag, and a frame is a keyframe exactly when the group holds no <c>ReferenceBlock</c>. Getting this
    /// wrong would not stop playback — it would put the wrong entries in the sync table and make seeking
    /// land in the wrong places.
    /// </summary>
    private static void ReadBlockGroup(
        Stream stream, Dictionary<ulong, IndexedTrack> tracks, Ebml.Element group, long clusterTimestamp)
    {
        var references = false;
        long duration = 0;
        Ebml.Element? block = null;

        for (var position = group.Start;
             Ebml.Read(stream, position, group.End) is { } element;
             position = element.End)
        {
            if (element.Id == IdReferenceBlock)
            {
                references = true;
            }
            else if (element.Id == IdBlockDuration)
            {
                duration = (long)Ebml.ReadUInt(stream, element.Start, element.End);
            }
            else if (element.Id == IdBlock)
            {
                block = element;
            }
        }

        if (block is { } found)
        {
            ReadBlock(
                stream, tracks, found, clusterTimestamp,
                keyframeFromFlags: false, isKeyframe: !references, duration: duration);
        }
    }

    private static void ReadBlock(
        Stream stream,
        Dictionary<ulong, IndexedTrack> tracks,
        Ebml.Element block,
        long clusterTimestamp,
        bool keyframeFromFlags,
        bool isKeyframe = false,
        long duration = 0)
    {
        stream.Position = block.Start;
        var number = Ebml.ReadVint(stream, keepMarker: false, out var numberLength);
        if (numberLength == 0)
        {
            return;
        }

        var high = stream.ReadByte();
        var low = stream.ReadByte();
        if (low < 0)
        {
            return;
        }

        var relative = (short)((high << 8) | low);
        var flags = stream.ReadByte();
        if (flags < 0)
        {
            return;
        }

        if (!tracks.TryGetValue(number, out var track))
        {
            return;
        }

        var payloadStart = block.Start + numberLength + 3;
        var payloadLength = (int)(block.End - payloadStart);
        if (payloadLength <= 0)
        {
            return;
        }

        var timestamp = clusterTimestamp + relative;
        var key = keyframeFromFlags ? (flags & 0x80) != 0 : isKeyframe;

        var frames = Delace(stream, payloadStart, payloadLength, flags);
        if (frames.Count > 1)
        {
            track.LacedBlocks++;
        }

        foreach (var (offset, size) in frames)
        {
            track.Samples.Add(new IndexedSample(timestamp, offset, size, key));
            if (duration > 0)
            {
                // Only a track that states durations grows the list, and then every sample gets an entry
                // so the two stay index-aligned.
                (track.SampleDurations ??= [.. Enumerable.Repeat(0L, track.Samples.Count - 1)]).Add(duration);
            }
            else
            {
                track.SampleDurations?.Add(0);
            }
        }
    }

    /// <summary>
    /// Splits a block into its frames. A laced block holds several, and this library's own files lace audio
    /// — fixed lacing on AC-3 and E-AC-3, EBML lacing on DTS — while material remuxed by ffmpeg never does,
    /// which is exactly how the need for this went unnoticed at first.
    ///
    /// The frames stay contiguous in the source, so each is still a plain (offset, size): only the
    /// arithmetic differs, and the lacing header is skipped rather than served.
    /// </summary>
    private static List<(long Offset, int Size)> Delace(
        Stream stream, long payloadStart, int payloadLength, int flags)
    {
        var lacing = (flags >> 1) & 0x03;
        if (lacing == 0)
        {
            return [(payloadStart, payloadLength)];
        }

        stream.Position = payloadStart;
        var countByte = stream.ReadByte();
        if (countByte < 0)
        {
            return [(payloadStart, payloadLength)];
        }

        var count = countByte + 1;              // stored as N-1
        var consumed = 1;
        var sizes = new List<int>(count);

        switch (lacing)
        {
            case 2:                             // fixed: equal parts, no size table
                var body = payloadLength - consumed;
                if (count == 0 || body % count != 0)
                {
                    // "Equal parts" that do not divide evenly are not equal parts. Slicing anyway would
                    // truncate every frame and quietly lose the remainder.
                    return [(payloadStart, payloadLength)];
                }

                for (var i = 0; i < count; i++)
                {
                    sizes.Add(body / count);
                }

                break;

            case 1:                             // Xiph: 255-terminated sums, last frame implied
                for (var i = 0; i < count - 1; i++)
                {
                    var total = 0;
                    while (true)
                    {
                        var next = stream.ReadByte();
                        if (next < 0)
                        {
                            return [(payloadStart, payloadLength)];
                        }

                        consumed++;
                        total += next;
                        if (next != 255)
                        {
                            break;
                        }
                    }

                    sizes.Add(total);
                }

                sizes.Add(payloadLength - consumed - sizes.Sum());
                break;

            default:                            // EBML: first absolute, the rest signed deltas
                var first = (long)Ebml.ReadVint(stream, keepMarker: false, out var firstLength);
                if (firstLength == 0)
                {
                    return [(payloadStart, payloadLength)];
                }

                consumed += firstLength;
                sizes.Add((int)first);
                for (var i = 0; i < count - 2; i++)
                {
                    var raw = (long)Ebml.ReadVint(stream, keepMarker: false, out var length);
                    if (length == 0)
                    {
                        return [(payloadStart, payloadLength)];
                    }

                    consumed += length;
                    // A signed VINT is biased by half the range its width can represent.
                    raw -= (1L << ((7 * length) - 1)) - 1;
                    sizes.Add((int)(sizes[^1] + raw));
                }

                if (count > 1)
                {
                    sizes.Add(payloadLength - consumed - sizes.Sum());
                }

                break;
        }

        var frames = new List<(long, int)>(sizes.Count);
        var cursor = payloadStart + consumed;
        foreach (var size in sizes)
        {
            if (size <= 0 || cursor + size > payloadStart + payloadLength)
            {
                // A lacing header that does not add up is not worth guessing at; the block is taken whole
                // rather than sliced into nonsense.
                return [(payloadStart, payloadLength)];
            }

            frames.Add((cursor, size));
            cursor += size;
        }

        return frames;
    }
}
