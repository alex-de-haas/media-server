using System.Diagnostics;
using MediaServer.Api.Remux;

namespace MediaServer.Api.Tests.Remux;

/// <summary>
/// What a single range request actually costs, split into its two halves.
///
/// Not a test — gated on an environment variable and skipped otherwise. It exists because production
/// answered a byte-range request in seven seconds and nothing in the suite measures time, only
/// correctness. Guessing which half dominates is what this replaces.
/// </summary>
public sealed class RemuxCostBenchmark
{
    [Fact]
    public void Split()
    {
        if (Environment.GetEnvironmentVariable("REMUX_BENCH") is null) { return; }

        // Roughly "2012": one 4K video track, eight audio, four text subtitles, over 2h38m.
        var index = Synthetic(videoSamples: 227_000, audioTracks: 8, audioSamples: 297_000, subtitleTracks: 4, cues: 2_200);
        var samples = index.Tracks.Sum(track => track.Samples.Count);

        using var file = new MemoryStream();
        var stamp = new RemuxIndexFormat.Stamp(33_736_302_908, DateTimeOffset.UnixEpoch);

        var writing = Stopwatch.StartNew();
        RemuxIndexFormat.Write(file, index, stamp);
        writing.Stop();

        var bytes = file.ToArray();
        var reading = Stopwatch.StartNew();
        var loaded = RemuxIndexFormat.Read(new MemoryStream(bytes))!.Value.Index;
        reading.Stop();

        var source = new SpinningDisk(index.SourceLength);
        var tracks = loaded.Tracks
            .Select(track => new Mp4Synthesizer.TrackRef(0, track.Number))
            .ToList();

        var building = Stopwatch.StartNew();
        var built = Mp4Synthesizer.Build(
            [new Mp4Synthesizer.Input(loaded, source)], tracks, VideoSignalling.DolbyVision);
        building.Stop();

        Console.WriteLine($"BENCH samples={samples:N0} index={bytes.Length / 1024 / 1024}MB "
            + $"read={reading.ElapsedMilliseconds}ms build={building.ElapsedMilliseconds}ms "
            + $"header={(built?.Header.Length ?? 0) / 1024 / 1024}MB");
        Console.WriteLine($"BENCH source I/O: {source.Seeks:N0} seeks, {source.Reads:N0} reads, "
            + $"{source.Spent.TotalSeconds:F1}s of seek latency on a spinning disk");

        Assert.True(reading.ElapsedMilliseconds >= 0);
    }

    /// <summary>A source that charges for seeking, the way a spinning disk does, and counts what it was asked.</summary>
    private sealed class SpinningDisk(long length) : Stream
    {
        public int Seeks { get; private set; }
        public int Reads { get; private set; }
        public TimeSpan Spent { get; private set; }

        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _position;
            set
            {
                if (value != _position)
                {
                    // A 7200rpm seek plus half a rotation, which is the figure this library's discs give.
                    Seeks++;
                    Spent += TimeSpan.FromMilliseconds(12);
                }

                _position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            _position += count;
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => length + offset,
            _ => offset,
        };
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static MatroskaIndex Synthetic(
        int videoSamples, int audioTracks, int audioSamples, int subtitleTracks = 0, int cues = 0)
    {
        var index = new MatroskaIndex { SourceLength = 33_736_302_908, TimestampScale = 1_000_000 };
        var random = new Random(7);
        long offset = 0;

        var video = new IndexedTrack
        {
            Number = 1, Ordinal = 0, Kind = IndexedTrackKind.Video,
            CodecId = "V_MPEGH/ISO/HEVC", CodecPrivate = [0x01, 0x22, 0x20, 0x00],
            Width = 3840, Height = 1600, DefaultDuration = 41_708_333,
        };

        for (var i = 0; i < videoSamples; i++)
        {
            offset += 40_000 + random.Next(120_000);
            video.Samples.Add(new IndexedSample(i * 42L, offset, 40_000 + random.Next(120_000), i % 48 == 0));
        }

        index.Tracks.Add(video);

        for (var t = 0; t < audioTracks; t++)
        {
            var audio = new IndexedTrack
            {
                Number = (ulong)(2 + t), Ordinal = 1 + t, Kind = IndexedTrackKind.Audio,
                CodecId = "A_AC3", Channels = 6, SampleRate = 48000,
            };

            long at = 0;
            for (var i = 0; i < audioSamples; i++)
            {
                at += 2_000 + random.Next(4_000);
                audio.Samples.Add(new IndexedSample(i * 32L, at, 1_536, true));
            }

            index.Tracks.Add(audio);
        }

        for (var t = 0; t < subtitleTracks; t++)
        {
            var text = new IndexedTrack
            {
                Number = (ulong)(20 + t), Ordinal = 9 + t, Kind = IndexedTrackKind.Subtitle,
                CodecId = "S_TEXT/UTF8", SampleDurations = [],
            };

            long at = 0;
            for (var i = 0; i < cues; i++)
            {
                at += 1_000_000 + random.Next(4_000_000);
                text.Samples.Add(new IndexedSample(i * 4_000L, at, 60 + random.Next(80), true));
                text.SampleDurations!.Add(3_000);
            }

            index.Tracks.Add(text);
        }

        return index;
    }
}
