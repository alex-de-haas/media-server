using System.Buffers.Binary;
using System.Text;

namespace MediaServer.Api.Tests.Probe;

/// <summary>
/// Builds the smallest containers that still say something, so the header reader can be tested on exact
/// bytes rather than on whatever files happen to be around. Only the boxes and elements it reads are
/// written; everything else a real muxer emits is beside the point here.
/// </summary>
internal static class ContainerBuilders
{
    // ---- MP4 ----

    /// <summary>An ISO box: 4-byte big-endian size, 4-char type, payload.</summary>
    public static byte[] Box(string type, params byte[][] payload)
    {
        var body = payload.SelectMany(part => part).ToArray();
        var buffer = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)buffer.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(buffer, 4);
        body.CopyTo(buffer, 8);
        return buffer;
    }

    /// <summary>A version-0 <c>mvhd</c>: the timescale and duration the file claims.</summary>
    public static byte[] Mvhd(uint timescale, uint duration) =>
        Box("mvhd", [0, 0, 0, 0], U32(0), U32(0), U32(timescale), U32(duration));

    /// <summary>A version-1 <c>mvhd</c>, which widens the dates and the duration to 64 bits.</summary>
    public static byte[] Mvhd64(uint timescale, ulong duration) =>
        Box("mvhd", [1, 0, 0, 0], U64(0), U64(0), U32(timescale), U64(duration));

    /// <summary>A track: its handler decides the kind, and the sample entry names the codec.</summary>
    public static byte[] Trak(
        string handler,
        string sampleEntry,
        string language = "eng",
        bool enabled = true,
        int width = 0,
        int height = 0,
        string? name = null,
        int channels = 0,
        int sampleRate = 0,
        int transferCharacteristics = 0,
        bool dolbyVision = false,
        byte[]? dolbyVisionRecord = null)
    {
        // tkhd v0: version/flags(4) created(4) modified(4) id(4) reserved(4) duration(4) reserved(8)
        //          layer(2) altGroup(2) volume(2) reserved(2) matrix(36) width(4) height(4) = 84 payload
        var tkhd = Box("tkhd",
            [0, 0, 0, (byte)(enabled ? 1 : 0)],
            new byte[4 + 4 + 4 + 4 + 4 + 8 + 2 + 2 + 2 + 2 + 36],
            U32((uint)width << 16), U32((uint)height << 16));

        var packed = (ushort)(((language[0] - 0x60) << 10) | ((language[1] - 0x60) << 5) | (language[2] - 0x60));
        // mdhd v0: version/flags(4) created(4) modified(4) timescale(4) duration(4) language(2) quality(2)
        var mdhd = Box("mdhd", [0, 0, 0, 0], U32(0), U32(0), U32(1000), U32(0), U16(packed), U16(0));
        var hdlr = Box("hdlr", new byte[4], new byte[4], Encoding.ASCII.GetBytes(handler), new byte[12]);

        var sampleChildren = new List<byte[]>();
        if (transferCharacteristics > 0)
        {
            sampleChildren.Add(Box("colr", Encoding.ASCII.GetBytes("nclx"), U16(9), U16((ushort)transferCharacteristics), U16(9), [0]));
        }

        if (dolbyVision || dolbyVisionRecord is not null)
        {
            // A real record unless the test says otherwise: a profile 8.1 as a WEB-DL carries it, in the box
            // its profile belongs to.
            var record = dolbyVisionRecord ?? DolbyVisionConfigurationTests.Profile81;
            sampleChildren.Add(Box(record[2] >> 1 >= 8 ? "dvvC" : "dvcC", record));
        }

        // VisualSampleEntry / AudioSampleEntry share a 78-byte fixed header before their child boxes; the
        // audio fields (channels, sample rate) sit inside the first 28 bytes of it.
        var fixedHeader = new byte[78];
        if (handler == "soun")
        {
            BinaryPrimitives.WriteUInt16BigEndian(fixedHeader.AsSpan(16), (ushort)channels);
            BinaryPrimitives.WriteUInt32BigEndian(fixedHeader.AsSpan(24), (uint)sampleRate << 16);
        }

        var entry = Box(sampleEntry, fixedHeader, sampleChildren.SelectMany(child => child).ToArray());
        var stsd = Box("stsd", new byte[4], U32(1), entry);
        var stbl = Box("stbl", stsd);
        var minf = Box("minf", stbl);
        var mdia = Box("mdia", mdhd, hdlr, minf);

        return name is null
            ? Box("trak", tkhd, mdia)
            : Box("trak", tkhd, mdia, Box("udta", Box("name", Encoding.UTF8.GetBytes(name))));
    }

    /// <summary>Movie-level artwork, which ffprobe reports as an extra video stream at index 1.</summary>
    public static byte[] CoverArtUdta() =>
        Box("udta", Box("meta", new byte[4], Box("ilst", Box("covr", Box("data", new byte[8])))));

    public static byte[] Mp4(params byte[][] moovChildren) =>
        [.. Box("ftyp", Encoding.ASCII.GetBytes("isom"), new byte[4]), .. Box("moov", moovChildren)];

    // ---- Matroska ----

    /// <summary>An EBML element: its id bytes verbatim, then a 4-byte length VINT, then the payload.</summary>
    public static byte[] Ebml(uint id, params byte[][] payload)
    {
        var body = payload.SelectMany(part => part).ToArray();
        var idBytes = IdBytes(id);
        var size = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(size, (uint)body.Length);
        size[0] |= 0x10; // the 4-byte VINT marker
        return [.. idBytes, .. size, .. body];
    }

    private static byte[] IdBytes(uint id)
    {
        var full = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(full, id);
        var firstUsed = Array.FindIndex(full, b => b != 0);
        return full[firstUsed..];
    }

    /// <summary>An unsigned EBML integer, in as few bytes as it needs.</summary>
    public static byte[] Uint(ulong value)
    {
        if (value == 0)
        {
            return [0];
        }

        var full = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(full, value);
        return full[Array.FindIndex(full, b => b != 0)..];
    }

    public static byte[] Float(double value)
    {
        var buffer = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(buffer, value);
        return buffer;
    }

    public static byte[] Str(string value) => Encoding.UTF8.GetBytes(value);

    public static byte[] Matroska(byte[] info, params byte[][] segmentChildren) =>
        [
            .. Ebml(0x1A45DFA3, Ebml(0x4286, Uint(1))),
            .. Ebml(0x18538067, [info, .. segmentChildren]),
        ];

    public static byte[] Info(double? durationTicks, ulong timestampScale = 1_000_000, string? writingApp = null)
    {
        var children = new List<byte[]> { Ebml(0x2AD7B1, Uint(timestampScale)) };
        if (durationTicks is { } duration)
        {
            children.Add(Ebml(0x4489, Float(duration)));
        }

        if (writingApp is not null)
        {
            children.Add(Ebml(0x5741, Str(writingApp)));
        }

        return Ebml(0x1549A966, [.. children]);
    }

    public static byte[] TrackEntry(
        ulong type,
        string codec,
        string? language = null,
        string? name = null,
        bool isDefault = true,
        bool isForced = false,
        ulong width = 0,
        ulong height = 0,
        ulong channels = 0,
        int transferCharacteristics = 0,
        ulong bitsPerChannel = 0,
        byte[]? dolbyVision = null,
        bool nameOnlyMapping = false)
    {
        var children = new List<byte[]>
        {
            Ebml(0x83, Uint(type)),
            Ebml(0x86, Str(codec)),
            Ebml(0x88, Uint(isDefault ? 1u : 0u)),
            Ebml(0x55AA, Uint(isForced ? 1u : 0u)),
        };

        if (language is not null)
        {
            children.Add(Ebml(0x22B59C, Str(language)));
        }

        if (name is not null)
        {
            children.Add(Ebml(0x536E, Str(name)));
        }

        if (type == 1)
        {
            var video = new List<byte[]> { Ebml(0xB0, Uint(width)), Ebml(0xBA, Uint(height)) };
            if (transferCharacteristics > 0 || bitsPerChannel > 0)
            {
                var colour = new List<byte[]>();
                if (transferCharacteristics > 0)
                {
                    colour.Add(Ebml(0x55BA, Uint((ulong)transferCharacteristics)));
                }

                if (bitsPerChannel > 0)
                {
                    colour.Add(Ebml(0x55B2, Uint(bitsPerChannel)));
                }

                video.Add(Ebml(0x55B0, [.. colour]));
            }

            children.Add(Ebml(0xE0, [.. video]));
        }

        if (type == 2 && channels > 0)
        {
            children.Add(Ebml(0xE1, Ebml(0x9F, Uint(channels))));
        }

        // Where Matroska keeps the Dolby Vision record: a BlockAdditionMapping typed dvcC/dvvC (ffmpeg and
        // mkvmerge both write the name too) with the record verbatim in its extra data. A name-only mapping
        // stands in for a muxer that wrote no type.
        if (dolbyVision is not null)
        {
            var mapping = new List<byte[]> { Ebml(0x41A4, Str("Dolby Vision configuration")) };
            if (!nameOnlyMapping)
            {
                mapping.Add(Ebml(0x41E7, Uint(dolbyVision[2] >> 1 >= 8 ? 0x64767643u : 0x64766343u)));
            }

            mapping.Add(Ebml(0x41ED, dolbyVision));
            children.Add(Ebml(0x41E4, [.. mapping]));
        }

        return Ebml(0xAE, [.. children]);
    }

    /// <summary>A BlockAdditionMapping of some other kind — an alpha channel — which must not read as Dolby Vision.</summary>
    public static byte[] AlphaMapping() =>
        Ebml(0x41E4, Ebml(0x41A4, Str("Alpha")), Ebml(0x41E7, Uint(1)), Ebml(0x41ED, new byte[] { 1, 2, 3, 4, 5 }));

    public static byte[] Tracks(params byte[][] entries) => Ebml(0x1654AE6B, entries);

    // ---- AVI ----

    /// <summary>A RIFF chunk: 4-char id, 4-byte little-endian size, payload (word-aligned).</summary>
    public static byte[] Chunk(string fourCc, params byte[][] payload)
    {
        var body = payload.SelectMany(part => part).ToArray();
        var padded = body.Length + (body.Length & 1);
        var buffer = new byte[8 + padded];
        Encoding.ASCII.GetBytes(fourCc).CopyTo(buffer, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), (uint)body.Length);
        body.CopyTo(buffer, 8);
        return buffer;
    }

    public static byte[] List(string type, params byte[][] payload) =>
        Chunk("LIST", [.. Encoding.ASCII.GetBytes(type), .. payload.SelectMany(part => part)]);

    /// <summary>An AVI whose main header claims <paramref name="totalFrames"/>, optionally overridden by an
    /// OpenDML extended header — the shape every AVI past ~2 GB has.</summary>
    public static byte[] Avi(uint microsecondsPerFrame, uint totalFrames, uint? openDmlTotalFrames = null)
    {
        var avih = Chunk("avih",
            U32Le(microsecondsPerFrame), U32Le(0), U32Le(0), U32Le(0),
            U32Le(totalFrames), U32Le(0), U32Le(1), U32Le(0),
            U32Le(640), U32Le(480), new byte[16]);

        var hdrlChildren = new List<byte[]> { avih };
        if (openDmlTotalFrames is { } extended)
        {
            hdrlChildren.Add(List("odml", Chunk("dmlh", U32Le(extended))));
        }

        var hdrl = List("hdrl", [.. hdrlChildren]);
        var movi = List("movi", new byte[16]);
        var body = new List<byte>();
        body.AddRange(Encoding.ASCII.GetBytes("AVI "));
        body.AddRange(hdrl);
        body.AddRange(movi);
        var riff = new byte[8 + body.Count];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(riff, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(riff.AsSpan(4), (uint)body.Count);
        body.CopyTo(riff, 8);
        return riff;
    }

    private static byte[] U16(ushort value)
    {
        var buffer = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        return buffer;
    }

    private static byte[] U32(uint value)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        return buffer;
    }

    private static byte[] U64(ulong value)
    {
        var buffer = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
        return buffer;
    }

    private static byte[] U32Le(uint value)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        return buffer;
    }
}
