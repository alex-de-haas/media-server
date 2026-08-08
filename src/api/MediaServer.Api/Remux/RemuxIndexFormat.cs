using System.Buffers.Binary;
using System.Text;

namespace MediaServer.Api.Remux;

/// <summary>
/// Reads and writes a <see cref="MatroskaIndex"/> as a file.
///
/// The shape of the data is what makes this worth encoding rather than serialising: within one track the
/// timestamps and the offsets both climb, and the steps between them are small and repetitive. Storing the
/// steps as variable-length integers instead of the values as fixed-width ones is the difference between
/// about twenty bytes a sample and about eight, which over a feature film is megabytes rather than tens of
/// them.
///
/// The header carries what the index was built from — the source's length and last-write time — so a file
/// that has been replaced or re-encoded invalidates its own index without anything else having to notice.
/// </summary>
internal static class RemuxIndexFormat
{
    private static readonly byte[] Magic = "MSRX"u8.ToArray();

    /// <summary>Bumping this invalidates every stored index, which is the point: a format change must not
    /// be readable as the old one.</summary>
    internal const ushort Version = 2;

    internal sealed record Stamp(long SourceLength, DateTimeOffset SourceModified)
    {
        public bool Matches(FileInfo file) =>
            file.Exists
            && file.Length == SourceLength
            // Second precision: file systems and copies disagree about anything finer, and a source that
            // changed within the same second also changed its length in every case that matters here.
            && Math.Abs((file.LastWriteTimeUtc - SourceModified.UtcDateTime).TotalSeconds) < 1;
    }

    public static void Write(Stream stream, MatroskaIndex index, Stamp stamp)
    {
        var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(stamp.SourceLength);
        writer.Write(stamp.SourceModified.UtcTicks);
        writer.Write(index.TimestampScale);
        writer.Write(index.DurationTicks);
        writer.Write(index.Tracks.Count);

        foreach (var track in index.Tracks)
        {
            writer.Write(track.Number);
            writer.Write(track.Ordinal);
            writer.Write((int)track.Kind);
            writer.Write(track.CodecId);
            WriteNullable(writer, track.Language);
            WriteNullable(writer, track.Name);
            WriteBytes(writer, track.CodecPrivate);
            WriteBytes(writer, track.DolbyVisionConfiguration);
            writer.Write(track.DefaultDuration);
            writer.Write(track.Width);
            writer.Write(track.Height);
            writer.Write(track.DisplayWidth);
            writer.Write(track.DisplayHeight);
            writer.Write(track.ColourPrimaries);
            writer.Write(track.TransferCharacteristics);
            writer.Write(track.MatrixCoefficients);
            writer.Write(track.FullRange);
            writer.Write(track.SampleRate);
            writer.Write(track.Channels);
            writer.Write(track.LacedBlocks);

            writer.Write(track.Samples.Count);
            long previousTimestamp = 0, previousOffset = 0;
            foreach (var sample in track.Samples)
            {
                // Timestamps step backwards wherever frames are stored out of display order, so the
                // timestamp delta is signed; an offset within one track only ever climbs.
                WriteSigned(writer, sample.Timestamp - previousTimestamp);
                WriteUnsigned(writer, (ulong)(sample.Offset - previousOffset));
                // The keyframe flag rides in the low bit rather than costing a byte of its own.
                WriteUnsigned(writer, ((ulong)sample.Size << 1) | (sample.IsKeyframe ? 1UL : 0UL));
                previousTimestamp = sample.Timestamp;
                previousOffset = sample.Offset;
            }

            // Only subtitles state how long a sample is shown, so the list is written as present-or-not
            // rather than as a zero for every frame of a film.
            writer.Write(track.SampleDurations is not null);
            if (track.SampleDurations is { } durations)
            {
                foreach (var duration in durations)
                {
                    WriteUnsigned(writer, (ulong)duration);
                }
            }
        }
    }

    /// <summary>
    /// Reads only what says whether an index is still good for its source. Answering "is there a usable
    /// index" this way costs a few dozen bytes instead of decoding a few hundred thousand samples.
    /// </summary>
    public static Stamp? ReadStamp(Stream stream)
    {
        var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        try
        {
            if (!reader.ReadBytes(4).AsSpan().SequenceEqual(Magic) || reader.ReadUInt16() != Version)
            {
                return null;
            }

            return new Stamp(reader.ReadInt64(), new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero));
        }
        catch (Exception exception) when (exception is EndOfStreamException or IOException)
        {
            return null;
        }
    }

    /// <summary>Returns null when the file is not an index, or not one this build can read.</summary>
    public static (MatroskaIndex Index, Stamp Stamp)? Read(Stream stream)
    {
        var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        try
        {
            if (!reader.ReadBytes(4).AsSpan().SequenceEqual(Magic) || reader.ReadUInt16() != Version)
            {
                return null;
            }

            var stamp = new Stamp(reader.ReadInt64(), new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero));
            var index = new MatroskaIndex
            {
                SourceLength = stamp.SourceLength,
                TimestampScale = reader.ReadInt64(),
                DurationTicks = reader.ReadDouble(),
            };

            var trackCount = reader.ReadInt32();
            for (var i = 0; i < trackCount; i++)
            {
                var track = new IndexedTrack { Number = reader.ReadUInt64() };
                track.Ordinal = reader.ReadInt32();
                track.Kind = (IndexedTrackKind)reader.ReadInt32();
                track.CodecId = reader.ReadString();
                track.Language = ReadNullable(reader);
                track.Name = ReadNullable(reader);
                track.CodecPrivate = ReadBytes(reader);
                track.DolbyVisionConfiguration = ReadBytes(reader);
                track.DefaultDuration = reader.ReadInt64();
                track.Width = reader.ReadInt32();
                track.Height = reader.ReadInt32();
                track.DisplayWidth = reader.ReadInt32();
                track.DisplayHeight = reader.ReadInt32();
                track.ColourPrimaries = reader.ReadInt32();
                track.TransferCharacteristics = reader.ReadInt32();
                track.MatrixCoefficients = reader.ReadInt32();
                track.FullRange = reader.ReadBoolean();
                track.SampleRate = reader.ReadDouble();
                track.Channels = reader.ReadInt32();
                track.LacedBlocks = reader.ReadInt32();

                var sampleCount = reader.ReadInt32();
                track.Samples.Capacity = sampleCount;
                long timestamp = 0, offset = 0;
                for (var s = 0; s < sampleCount; s++)
                {
                    timestamp += ReadSigned(reader);
                    offset += (long)ReadUnsigned(reader);
                    var packed = ReadUnsigned(reader);
                    track.Samples.Add(new IndexedSample(
                        timestamp, offset, (int)(packed >> 1), (packed & 1) == 1));
                }

                if (reader.ReadBoolean())
                {
                    var durations = new List<long>(sampleCount);
                    for (var s = 0; s < sampleCount; s++)
                    {
                        durations.Add((long)ReadUnsigned(reader));
                    }

                    track.SampleDurations = durations;
                }

                index.Tracks.Add(track);
            }

            return (index, stamp);
        }
        catch (Exception exception) when (exception is EndOfStreamException or IOException)
        {
            // A truncated index — a build that was interrupted, most likely — is not an error to report
            // upward. It is simply not an index, and the caller rebuilds.
            return null;
        }
    }

    private static void WriteNullable(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
        {
            writer.Write(value);
        }
    }

    private static string? ReadNullable(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadString() : null;

    private static void WriteBytes(BinaryWriter writer, byte[]? value)
    {
        writer.Write(value?.Length ?? -1);
        if (value is not null)
        {
            writer.Write(value);
        }
    }

    private static byte[]? ReadBytes(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        return length < 0 ? null : reader.ReadBytes(length);
    }

    private static void WriteUnsigned(BinaryWriter writer, ulong value)
    {
        while (value >= 0x80)
        {
            writer.Write((byte)(value | 0x80));
            value >>= 7;
        }

        writer.Write((byte)value);
    }

    private static ulong ReadUnsigned(BinaryReader reader)
    {
        ulong value = 0;
        var shift = 0;
        while (true)
        {
            var next = reader.ReadByte();
            value |= (ulong)(next & 0x7F) << shift;
            if ((next & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
            if (shift > 63)
            {
                throw new EndOfStreamException("varint too long");
            }
        }
    }

    // Zigzag, so that a small negative step costs one byte rather than ten.
    private static void WriteSigned(BinaryWriter writer, long value) =>
        WriteUnsigned(writer, (ulong)((value << 1) ^ (value >> 63)));

    private static long ReadSigned(BinaryReader reader)
    {
        var raw = ReadUnsigned(reader);
        return (long)(raw >> 1) ^ -(long)(raw & 1);
    }
}
