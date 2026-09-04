using System.Net;
using System.Text;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Probe;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Probe;

/// <summary>
/// The engine-backed provider, against a stubbed transport. Covers what the app has to get right on its
/// side of the contract: addressing files by media mount, translating the engine's vocabulary into the one
/// the library stores, and declining rather than throwing when the engine cannot answer — the whole point
/// being that a caller can then fall back.
/// </summary>
public sealed class RemoteMediaProbeTests
{
    private const string Root = "/library/movies";

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("the engine is not listening");
    }

    private static MediaServerSettings Settings() => new()
    {
        CatalogMountRoots = [new CatalogMount("movies", Root)],
    };

    private static (RemoteMediaProbe Probe, StubHandler Handler) Probe(HttpStatusCode status, string body)
    {
        var handler = new StubHandler(status, body);
        return (
            new RemoteMediaProbe(
                new HttpClient(handler) { BaseAddress = new Uri("http://engine.local/") },
                Settings(),
                NullLogger<RemoteMediaProbe>.Instance),
            handler);
    }

    private const string OneVideo = """
        {
          "container": "mkv", "durationSeconds": 7506.291, "bitrate": 7699019, "sizeBytes": 7223885097,
          "streams": [
            { "index": 0, "kind": "Video", "codec": "hevc", "profile": "Main 10", "language": "eng",
              "title": null, "isDefault": true, "isForced": false, "bitrate": 7500000,
              "width": 1920, "height": 1080, "frameRate": 23.976023, "bitDepth": 10, "hdr": "Hdr10",
              "channels": null, "sampleRate": null }
          ]
        }
        """;

    private const string DolbyVisionVideo = """
        {
          "container": "mkv", "durationSeconds": 7506.291, "bitrate": 7699019, "sizeBytes": 7223885097,
          "streams": [
            { "index": 0, "kind": "Video", "codec": "hevc", "profile": "Main 10", "language": "eng",
              "title": null, "isDefault": true, "isForced": false, "bitrate": 7500000,
              "width": 3840, "height": 2160, "frameRate": 23.976023, "bitDepth": 10, "hdr": "DolbyVision",
              "channels": null, "sampleRate": null,
              "dolbyVision": { "profile": 7, "level": 6, "blSignalCompatibilityId": 6,
                               "rpuPresent": true, "elPresent": true, "blPresent": true } }
          ]
        }
        """;

    [Fact]
    public async Task Reads_the_dolby_vision_record_the_engine_reports()
    {
        var (probe, _) = Probe(HttpStatusCode.OK, DolbyVisionVideo);

        var result = await probe.TryProbeAsync($"{Root}/Starship Troopers (1997)/Starship Troopers (1997).mkv", CancellationToken.None);

        var video = Assert.Single(result!.Streams);
        Assert.Equal("Dolby Vision", video.HdrFormat);
        Assert.Equal(new DolbyVisionDetail(7, 6, 6, ElPresent: true), video.DolbyVision);
    }

    [Fact]
    public async Task A_stream_without_a_record_has_no_dolby_vision_detail()
    {
        // An engine from before the field, or a stream that is not Dolby Vision: the label stands on its own.
        var (probe, _) = Probe(HttpStatusCode.OK, OneVideo);

        var result = await probe.TryProbeAsync($"{Root}/TRON Legacy (2010)/TRON Legacy (2010).mkv", CancellationToken.None);

        Assert.Null(Assert.Single(result!.Streams).DolbyVision);
    }

    [Fact]
    public async Task Addresses_the_file_by_its_media_mount()
    {
        var (probe, handler) = Probe(HttpStatusCode.OK, OneVideo);

        await probe.TryProbeAsync($"{Root}/TRON Legacy (2010)/TRON Legacy (2010).mkv", CancellationToken.None);

        // The engine resolves paths against its own mounts, so it is sent a label and a relative path —
        // never this app's absolute one, which means nothing on the other side.
        Assert.Contains("\"mountLabel\":\"movies\"", handler.RequestBody);
        Assert.Contains("TRON Legacy (2010)/TRON Legacy (2010).mkv", handler.RequestBody);
        Assert.DoesNotContain(Root, handler.RequestBody);
    }

    [Fact]
    public async Task Translates_the_engines_answer_into_the_stored_vocabulary()
    {
        var (probe, _) = Probe(HttpStatusCode.OK, OneVideo);

        var result = (await probe.TryProbeAsync($"{Root}/movie.mkv", CancellationToken.None))!;

        Assert.Equal(ProbeSource.Engine, result.Source);
        Assert.Equal("mkv", result.Container);
        Assert.InRange(
            result.DurationTicks,
            TimeSpan.FromSeconds(7506.290).Ticks,
            TimeSpan.FromSeconds(7506.292).Ticks);
        var video = Assert.Single(result.Streams);
        Assert.Equal(StreamType.Video, video.Type);
        Assert.Equal("hevc", video.Codec);
        Assert.Equal("Main 10", video.Profile);
        Assert.Equal(10, video.BitDepth);
        Assert.Equal(23.976, video.FrameRate);
        Assert.Equal(7_500_000, video.Bitrate);
    }

    [Fact]
    public async Task A_stream_the_engine_states_no_bitrate_for_keeps_none()
    {
        // The engine answers null for a container that records no per-track rate and carries no BPS tag.
        // Splitting the file's overall bitrate across its streams would fill the gap with a number nothing
        // measured, and a caller cannot tell that apart from a real one.
        const string Json = """
            {"container":"mkv","durationSeconds":100,"bitrate":8000000,"sizeBytes":1,"streams":[
              {"index":0,"kind":"Audio","codec":"dts","isDefault":true,"isForced":false}]}
            """;
        var (probe, _) = Probe(HttpStatusCode.OK, Json);

        var result = (await probe.TryProbeAsync($"{Root}/movie.mkv", CancellationToken.None))!;

        Assert.Null(Assert.Single(result.Streams).Bitrate);
        Assert.Equal(8_000_000, result.Bitrate);
    }

    [Theory]
    [InlineData("DolbyVision", "Dolby Vision")]
    [InlineData("Hdr10Plus", "HDR10+")]
    [InlineData("Hdr10", "HDR10")]
    [InlineData("Hlg", "HLG")]
    [InlineData("Sdr", "SDR")]
    // The engine should never send Unknown — that member exists for the header reader — but an unfamiliar
    // value is treated as unknown rather than asserted as SDR.
    [InlineData("Unknown", null)]
    [InlineData("SomethingNewer", null)]
    public async Task Maps_every_hdr_value_the_engine_can_report(string engineValue, string? expected)
    {
        var json = $$"""
            {"container":"mkv","durationSeconds":1,"sizeBytes":1,"streams":[
              {"index":0,"kind":"Video","hdr":"{{engineValue}}","isDefault":true,"isForced":false}]}
            """;
        var (probe, _) = Probe(HttpStatusCode.OK, json);

        var result = (await probe.TryProbeAsync($"{Root}/movie.mkv", CancellationToken.None))!;

        Assert.Equal(expected, Assert.Single(result.Streams).HdrFormat);
    }

    [Fact]
    public async Task Keeps_stream_indexes_including_the_synthesized_cover_art_entry()
    {
        // The engine reports ffprobe's numbering, artwork included. Job creation and client track selection
        // address streams by these, so they are carried through untouched.
        const string Json = """
            {"container":"m4v","durationSeconds":1,"sizeBytes":1,"streams":[
              {"index":0,"kind":"Video","codec":"h264","isDefault":true,"isForced":false},
              {"index":1,"kind":"Video","codec":"png","isDefault":false,"isForced":false},
              {"index":2,"kind":"Audio","codec":"aac","language":"eng","isDefault":true,"isForced":false},
              {"index":3,"kind":"Audio","codec":"aac","language":"rus","isDefault":false,"isForced":false}]}
            """;
        var (probe, _) = Probe(HttpStatusCode.OK, Json);

        var result = (await probe.TryProbeAsync($"{Root}/movie.m4v", CancellationToken.None))!;

        Assert.Equal([0, 1, 2, 3], result.Streams.Select(stream => stream.Index));
        Assert.Equal("rus", result.Streams[3].Language);
    }

    [Fact]
    public async Task Drops_stream_kinds_the_library_does_not_model_without_disturbing_the_indexes()
    {
        const string Json = """
            {"container":"mp4","durationSeconds":1,"sizeBytes":1,"streams":[
              {"index":0,"kind":"Video","isDefault":true,"isForced":false},
              {"index":1,"kind":"Other","isDefault":false,"isForced":false},
              {"index":2,"kind":"Audio","isDefault":false,"isForced":false}]}
            """;
        var (probe, _) = Probe(HttpStatusCode.OK, Json);

        var result = (await probe.TryProbeAsync($"{Root}/movie.mp4", CancellationToken.None))!;

        Assert.Equal([0, 2], result.Streams.Select(stream => stream.Index));
    }

    [Fact]
    public async Task A_file_outside_every_mount_is_declined_without_asking_the_engine()
    {
        // The engine can only see what is bound into it; asking about anything else would fail there anyway.
        var (probe, handler) = Probe(HttpStatusCode.OK, OneVideo);

        var result = await probe.TryProbeAsync("/elsewhere/movie.mkv", CancellationToken.None);

        Assert.Null(result);
        Assert.Null(handler.RequestBody);
    }

    [Fact]
    public async Task A_refusal_is_declined_rather_than_thrown_so_the_caller_can_fall_back()
    {
        var (probe, _) = Probe(HttpStatusCode.BadRequest, """{"error":"not media"}""");

        Assert.Null(await probe.TryProbeAsync($"{Root}/movie.mkv", CancellationToken.None));
    }

    [Fact]
    public async Task An_unreachable_engine_is_declined_rather_than_thrown()
    {
        var probe = new RemoteMediaProbe(
            new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("http://engine.local/") },
            Settings(),
            NullLogger<RemoteMediaProbe>.Instance);

        Assert.Null(await probe.TryProbeAsync($"{Root}/movie.mkv", CancellationToken.None));
    }

    [Fact]
    public async Task Unreadable_output_is_declined_rather_than_thrown()
    {
        var (probe, _) = Probe(HttpStatusCode.OK, "not json at all");

        Assert.Null(await probe.TryProbeAsync($"{Root}/movie.mkv", CancellationToken.None));
    }
}
