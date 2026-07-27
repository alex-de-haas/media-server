using MediaServer.Api.Data;
using MediaServer.Api.Transcoding;

namespace MediaServer.Api.Tests.Transcoding;

/// <summary>
/// Mapping a track rename onto the output stream that will carry it. The engine addresses streams by
/// (input ordinal, absolute index), and a sidecar being merged becomes an input of its own — so where an
/// edit lands depends on whether its track is inside the video or beside it.
/// </summary>
public sealed class StreamMetadataEditTests
{
    private static MediaStream Embedded(int index, StreamType type = StreamType.Audio) =>
        new() { Id = Guid.NewGuid(), StreamType = type, Index = index };

    private static MediaStream Sidecar(int index, StreamType type = StreamType.Audio) =>
        new() { Id = Guid.NewGuid(), StreamType = type, Index = index, IsExternal = true, ExternalPath = $"x/{index}.mka" };

    private static MediaSource SourceWith(params MediaStream[] streams) =>
        new() { Id = Guid.NewGuid(), Container = "mkv", Path = "x/movie.mkv", Streams = streams };

    private static CreateTranscodeRequest Request(params StreamMetadataEdit[] edits) =>
        new(Guid.NewGuid(), null, null, null, MetadataEdits: edits);

    [Fact]
    public void No_edits_produce_no_overrides() =>
        Assert.Null(TranscodeService.ResolveMetadataOverrides(
            new CreateTranscodeRequest(Guid.NewGuid(), null, null, null), SourceWith(), []));

    [Fact]
    public void An_embedded_track_is_addressed_within_the_video_by_its_own_index()
    {
        var track = Embedded(3);
        var source = SourceWith(Embedded(0, StreamType.Video), track);

        var result = Assert.Single(TranscodeService.ResolveMetadataOverrides(
            Request(new StreamMetadataEdit(track.Id, Title: "Original")), source, [])!);

        Assert.Equal(0, result.Input);
        Assert.Equal(3, result.StreamIndex);
        Assert.Equal("Original", result.Title);
        Assert.Null(result.Language);
    }

    [Fact]
    public void A_merged_sidecar_is_addressed_as_its_own_input()
    {
        // Each sidecar becomes a separate ffmpeg input holding one track, so it is index 0 of input N.
        var first = Sidecar(1000);
        var second = Sidecar(1001);
        var source = SourceWith(Embedded(0, StreamType.Video));

        var results = TranscodeService.ResolveMetadataOverrides(
            Request(new StreamMetadataEdit(second.Id, "rus", "MVO wMedia")), source, [first, second])!;

        var result = Assert.Single(results);
        Assert.Equal(2, result.Input);
        Assert.Equal(0, result.StreamIndex);
        Assert.Equal("rus", result.Language);
    }

    [Fact]
    public void An_edit_naming_a_sidecar_that_is_not_being_merged_is_refused()
    {
        // It has no output stream to write to; silently dropping it would look like the rename worked.
        var unmerged = Sidecar(1000);
        var source = SourceWith(Embedded(0, StreamType.Video));

        var error = Assert.Throws<TranscodeRequestException>(() => TranscodeService.ResolveMetadataOverrides(
            Request(new StreamMetadataEdit(unmerged.Id, Title: "Nope")), source, []));
        Assert.Contains("neither in this version", error.Message);
    }

    [Fact]
    public void An_edit_naming_an_unknown_track_is_refused() =>
        Assert.Throws<TranscodeRequestException>(() => TranscodeService.ResolveMetadataOverrides(
            Request(new StreamMetadataEdit(Guid.NewGuid(), Title: "Nope")), SourceWith(), []));

    [Fact]
    public void An_edit_that_sets_nothing_is_refused()
    {
        var track = Embedded(1);
        var error = Assert.Throws<TranscodeRequestException>(() => TranscodeService.ResolveMetadataOverrides(
            Request(new StreamMetadataEdit(track.Id)), SourceWith(track), []));
        Assert.Contains("language or a title", error.Message);
    }

    [Fact]
    public void Embedded_and_merged_edits_travel_together()
    {
        var embedded = Embedded(1);
        var sidecar = Sidecar(1000);
        var source = SourceWith(Embedded(0, StreamType.Video), embedded);

        var results = TranscodeService.ResolveMetadataOverrides(
            Request(
                new StreamMetadataEdit(embedded.Id, Title: "Original"),
                new StreamMetadataEdit(sidecar.Id, Title: "Дубляж")),
            source, [sidecar])!;

        Assert.Equal(2, results.Count);
        Assert.Contains(results, entry => entry is { Input: 0, StreamIndex: 1, Title: "Original" });
        Assert.Contains(results, entry => entry is { Input: 1, StreamIndex: 0, Title: "Дубляж" });
    }
}
