using MediaServer.Api.Remux;

namespace MediaServer.Api.Tests.Remux;

/// <summary>
/// Who owns the bytes in a served range.
///
/// This is the line a diagnosis rests on: video from the opening minute means a player restarting, and
/// "nothing we chose" means a sample table pointing where nothing of ours lives. They are different
/// faults with opposite repairs, so the one thing this must never do is say the second by accident.
/// </summary>
public sealed class RemuxRangeOwnershipTests
{
    private static IndexedTrack Track(ulong number, IndexedTrackKind kind, params (long Timestamp, long Offset, int Size)[] samples)
    {
        var track = new IndexedTrack { Number = number, Kind = kind };
        foreach (var (timestamp, offset, size) in samples)
        {
            track.Samples.Add(new IndexedSample(timestamp, offset, size, true));
        }

        return track;
    }

    private static MatroskaIndex Index(params IndexedTrack[] tracks)
    {
        // One tick per millisecond, so a timestamp of 1000 is a second in.
        var index = new MatroskaIndex { SourceLength = 1_000_000 };
        index.Tracks.AddRange(tracks);
        return index;
    }

    [Fact]
    public void A_reordered_track_reports_its_span_forwards()
    {
        // Samples run forward through the file; presentation times do not. A video track stores
        // 0, 83, 41, 166 — reading the ends of that would print a span that runs backwards.
        var track = Track(1, IndexedTrackKind.Video,
            (0, 0, 10), (83, 10, 10), (41, 20, 10), (166, 30, 10));

        var (count, bytes, first, last) = RemuxStreamService.Span(track, 0, 40);

        Assert.Equal(4, count);
        Assert.Equal(40, bytes);
        Assert.Equal(0, first);
        Assert.Equal(166, last);
    }

    [Fact]
    public void A_sample_straddling_the_end_counts_whole()
    {
        var track = Track(1, IndexedTrackKind.Video, (0, 0, 10), (41, 10, 10));

        var (count, _, _, _) = RemuxStreamService.Span(track, 0, 15);

        Assert.Equal(2, count);
    }

    [Fact]
    public void A_range_past_every_sample_owns_nothing()
    {
        var track = Track(1, IndexedTrackKind.Video, (0, 0, 10));

        Assert.Equal((0, 0, 0, 0), RemuxStreamService.Span(track, 500, 600));
    }

    [Fact]
    public void A_range_reaching_into_the_header_says_so()
    {
        // Reporting only the silence past it would read as "the sample table points nowhere", which is
        // the one answer that means our own header is wrong.
        var index = Index(Track(1, IndexedTrackKind.Video, (0, 0, 100)));
        var spans = new[] { new RemuxStreamService.InputSpan(1000, 2000, index, 0) };
        var tracks = new[] { new Mp4Synthesizer.TrackRef(0, 1) };

        var whose = RemuxStreamService.Whose(spans, tracks, headerLength: 1000, from: 900, to: 1050);

        Assert.StartsWith("the header", whose);
        Assert.Contains("video", whose);
    }

    [Fact]
    public void A_chosen_dub_in_a_second_file_is_named_rather_than_missed()
    {
        // The sidecar's samples are offsets into a file of its own, and the wrapper in front of it
        // shifts everything after. Dropping it would report the one answer that means our bug.
        var video = Index(Track(1, IndexedTrackKind.Video, (0, 0, 100)));
        var dub = Index(Track(7, IndexedTrackKind.Audio, (5000, 0, 40), (6000, 40, 40)));
        var spans = new[]
        {
            new RemuxStreamService.InputSpan(1000, 1100, video, 0),
            new RemuxStreamService.InputSpan(1200, 1280, dub, 1),
        };
        var tracks = new[] { new Mp4Synthesizer.TrackRef(0, 1), new Mp4Synthesizer.TrackRef(1, 7) };

        var whose = RemuxStreamService.Whose(spans, tracks, headerLength: 1000, from: 1200, to: 1280);

        Assert.Contains("audio (input 1) 2 samples", whose);
        Assert.Contains("at 5-6s", whose);
        Assert.DoesNotContain("nothing we chose", whose);
    }

    [Fact]
    public void A_range_inside_an_input_that_holds_none_of_our_samples_says_exactly_that()
    {
        var index = Index(Track(1, IndexedTrackKind.Video, (0, 0, 100)));
        var spans = new[] { new RemuxStreamService.InputSpan(1000, 9000, index, 0) };
        var tracks = new[] { new Mp4Synthesizer.TrackRef(0, 1) };

        var whose = RemuxStreamService.Whose(spans, tracks, headerLength: 1000, from: 5000, to: 6000);

        Assert.Equal("nothing we chose", whose);
    }

    [Fact]
    public void Bytes_between_two_files_are_the_padding_they_are()
    {
        var index = Index(Track(1, IndexedTrackKind.Video, (0, 0, 100)));
        var spans = new[]
        {
            new RemuxStreamService.InputSpan(1000, 1100, index, 0),
            new RemuxStreamService.InputSpan(1200, 1300, index, 1),
        };
        var tracks = new[] { new Mp4Synthesizer.TrackRef(0, 1) };

        var whose = RemuxStreamService.Whose(spans, tracks, headerLength: 1000, from: 1100, to: 1200);

        Assert.Equal("padding between inputs", whose);
    }
}
