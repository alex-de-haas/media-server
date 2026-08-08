using MediaServer.Api.Remux;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Remux;

public sealed class RemuxIndexStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("remux-index-tests").FullName;

    private RemuxIndexStore Store() => new(_root, NullLogger<RemuxIndexStore>.Instance);

    private string WriteSource(string content = "source")
    {
        var path = Path.Combine(_root, "source.mkv");
        File.WriteAllText(path, content);
        return path;
    }

    private static MatroskaIndex SampleIndex()
    {
        var index = new MatroskaIndex { SourceLength = 4096, TimestampScale = 1_000_000, DurationTicks = 31_540 };
        var video = new IndexedTrack
        {
            Number = 1,
            Kind = IndexedTrackKind.Video,
            CodecId = "V_MPEGH/ISO/HEVC",
            CodecPrivate = [0x01, 0x22, 0x20],
            DolbyVisionConfiguration = [0x01, 0x00, 0x10, 0x35, 0x10],
            Language = "eng",
            Name = "Main",
            DefaultDuration = 41_666_666,
            Width = 3840,
            Height = 2160,
            ColourPrimaries = 9,
            TransferCharacteristics = 16,
            MatrixCoefficients = 9,
            LacedBlocks = 0,
        };

        // Timestamps that step backwards, because frames stored out of display order are the case the
        // signed delta exists for.
        video.Samples.Add(new IndexedSample(0, 1000, 40, true));
        video.Samples.Add(new IndexedSample(83, 1040, 12, false));
        video.Samples.Add(new IndexedSample(41, 1052, 9, false));
        video.Samples.Add(new IndexedSample(166, 1061, 31, false));

        var audio = new IndexedTrack
        {
            Number = 2,
            Kind = IndexedTrackKind.Audio,
            CodecId = "A_AC3",
            Channels = 6,
            SampleRate = 48000,
            LacedBlocks = 3,
        };
        audio.Samples.Add(new IndexedSample(0, 2000, 1536, true));
        audio.Samples.Add(new IndexedSample(32, 3536, 1536, true));

        index.Tracks.Add(video);
        index.Tracks.Add(audio);
        return index;
    }

    [Fact]
    public void An_index_survives_a_round_trip_unchanged()
    {
        var source = WriteSource();
        var store = Store();
        var original = SampleIndex();

        store.Save(Guid.NewGuid(), source, original);
        var id = Guid.NewGuid();
        store.Save(id, source, original);
        var loaded = store.Load(id, source);

        Assert.NotNull(loaded);
        Assert.Equal(original.TimestampScale, loaded.TimestampScale);
        Assert.Equal(original.DurationTicks, loaded.DurationTicks);
        Assert.Equal(original.Tracks.Count, loaded.Tracks.Count);

        foreach (var (before, after) in original.Tracks.Zip(loaded.Tracks))
        {
            Assert.Equal(before.Number, after.Number);
            Assert.Equal(before.Kind, after.Kind);
            Assert.Equal(before.CodecId, after.CodecId);
            Assert.Equal(before.CodecPrivate, after.CodecPrivate);
            Assert.Equal(before.DolbyVisionConfiguration, after.DolbyVisionConfiguration);
            Assert.Equal(before.Language, after.Language);
            Assert.Equal(before.Name, after.Name);
            Assert.Equal(before.DefaultDuration, after.DefaultDuration);
            Assert.Equal(before.Width, after.Width);
            Assert.Equal(before.TransferCharacteristics, after.TransferCharacteristics);
            Assert.Equal(before.Channels, after.Channels);
            Assert.Equal(before.SampleRate, after.SampleRate);
            Assert.Equal(before.LacedBlocks, after.LacedBlocks);
            Assert.Equal(before.Samples, after.Samples);
        }
    }

    [Fact]
    public void An_index_is_refused_when_the_source_has_changed_length()
    {
        var source = WriteSource("source");
        var store = Store();
        var id = Guid.NewGuid();
        store.Save(id, source, SampleIndex());

        File.WriteAllText(source, "source, but longer");

        Assert.Null(store.Load(id, source));
        Assert.False(store.IsCurrent(id, source));
    }

    [Fact]
    public void An_index_is_refused_when_the_source_has_been_rewritten()
    {
        var source = WriteSource("source");
        var store = Store();
        var id = Guid.NewGuid();
        store.Save(id, source, SampleIndex());

        // Same length, different file: a re-encode that happens to match in size still invalidates.
        File.WriteAllText(source, "SOURCE");
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddHours(1));

        Assert.Null(store.Load(id, source));
    }

    [Fact]
    public void An_index_is_accepted_while_its_source_is_untouched()
    {
        var source = WriteSource();
        var store = Store();
        var id = Guid.NewGuid();
        store.Save(id, source, SampleIndex());

        Assert.True(store.IsCurrent(id, source));
        Assert.NotNull(store.Load(id, source));
    }

    [Fact]
    public void A_truncated_index_is_not_an_index()
    {
        var source = WriteSource();
        var store = Store();
        var id = Guid.NewGuid();
        store.Save(id, source, SampleIndex());

        var path = store.PathFor(id);
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);

        Assert.Null(store.Load(id, source));
    }

    [Fact]
    public void Something_that_is_not_an_index_is_not_read_as_one()
    {
        var source = WriteSource();
        var store = Store();
        var id = Guid.NewGuid();
        Directory.CreateDirectory(Path.GetDirectoryName(store.PathFor(id))!);
        File.WriteAllText(store.PathFor(id), "this is not an index at all");

        Assert.Null(store.Load(id, source));
        Assert.False(store.IsCurrent(id, source));
    }

    [Fact]
    public void A_stored_index_leaves_no_partial_file_behind()
    {
        var source = WriteSource();
        var store = Store();
        var id = Guid.NewGuid();
        store.Save(id, source, SampleIndex());

        Assert.True(File.Exists(store.PathFor(id)));
        Assert.False(File.Exists(store.PathFor(id) + ".partial"));
    }

    [Fact]
    public void Deleting_removes_the_index_and_any_partial()
    {
        var source = WriteSource();
        var store = Store();
        var id = Guid.NewGuid();
        store.Save(id, source, SampleIndex());
        File.WriteAllText(store.PathFor(id) + ".partial", "interrupted");

        store.Delete(id);

        Assert.False(File.Exists(store.PathFor(id)));
        Assert.False(File.Exists(store.PathFor(id) + ".partial"));
    }

    [Fact]
    public void Deleting_an_index_that_is_not_there_is_not_an_error()
    {
        Store().Delete(Guid.NewGuid());
    }

    [Fact]
    public void Stored_lists_what_has_been_built()
    {
        var source = WriteSource();
        var store = Store();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        store.Save(first, source, SampleIndex());
        store.Save(second, source, SampleIndex());
        File.WriteAllText(Path.Combine(_root, "remux-index", "not-a-guid.idx"), "stray");

        var stored = store.Stored().ToHashSet();

        Assert.Contains(first, stored);
        Assert.Contains(second, stored);
        Assert.Equal(2, stored.Count);
    }

    [Fact]
    public void Stored_is_empty_before_anything_is_built()
    {
        Assert.Empty(Store().Stored());
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
