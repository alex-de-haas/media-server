using System.Net;
using System.Text;
using MediaServer.Api.Configuration;
using MediaServer.Api.Transcoding;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Transcoding;

/// <summary>
/// Pins the job request's **wire format** against a stubbed transport. The <c>Wire*</c> records serialize
/// their member names verbatim, so a rename made for readability does not fail — the engine simply cannot
/// bind the field, leaves it null, and the job runs with something other than what was asked for. These
/// assertions read the raw body for that reason: round-tripping it back through the same records would
/// agree with any renaming and prove nothing.
/// </summary>
public sealed class RemoteTranscodeEngineWireTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"jobId":"j1","inputPath":"in.mkv","outputPath":"out.mkv","durationSeconds":1,"inputSizeBytes":1}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private static async Task<string> PostAsync(params EngineAudioTarget[] audioTargets)
    {
        var handler = new StubHandler();
        using var engine = new RemoteTranscodeEngine(
            new HttpClient(handler) { BaseAddress = new Uri("http://engine.local/") },
            new MediaServerSettings(),
            NullLogger<RemoteTranscodeEngine>.Instance);

        await engine.CreateAsync(
            new TranscodeJobRequest(
                "movies", "in.mkv", "movies", "out.mkv", "copy", "auto", null,
                AudioStreamIndexes: [1],
                AudioTargets: audioTargets.Length == 0 ? null : audioTargets),
            CancellationToken.None);

        return handler.RequestBody!;
    }

    [Fact]
    public async Task The_dolby_vision_mode_travels_under_the_engines_name()
    {
        var handler = new StubHandler();
        using var engine = new RemoteTranscodeEngine(
            new HttpClient(handler) { BaseAddress = new Uri("http://engine.local/") },
            new MediaServerSettings(),
            NullLogger<RemoteTranscodeEngine>.Instance);

        await engine.CreateAsync(
            new TranscodeJobRequest("movies", "in.mkv", "movies", "out.mkv", "copy", "auto", null, DolbyVision: "toProfile81"),
            CancellationToken.None);

        Assert.Contains("\"dolbyVision\":\"toProfile81\"", handler.RequestBody);
    }

    [Fact]
    public async Task A_job_that_keeps_dolby_vision_sends_null_so_an_older_engine_sees_nothing_new()
    {
        var body = await PostAsync();

        Assert.Contains("\"dolbyVision\":null", body);
    }

    private sealed class HardwareHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    [Theory]
    [InlineData("""{"vaapiAvailable":false,"tools":{"dolbyVisionConversion":true,"doviTool":"2.3.3","mkvtoolnix":"82.0"}}""", true)]
    [InlineData("""{"vaapiAvailable":false,"tools":{"dolbyVisionConversion":false,"doviTool":null,"mkvtoolnix":null}}""", false)]
    // An engine from before the tools block reports no tooling rather than failing the read.
    [InlineData("""{"vaapiAvailable":false,"renderDevices":[]}""", false)]
    [InlineData("not json", false)]
    public async Task The_tooling_is_read_from_the_engines_hardware_report(string hardware, bool expected)
    {
        using var engine = new RemoteTranscodeEngine(
            new HttpClient(new HardwareHandler(hardware)) { BaseAddress = new Uri("http://engine.local/") },
            new MediaServerSettings(),
            NullLogger<RemoteTranscodeEngine>.Instance);

        var tooling = await engine.GetToolingAsync(CancellationToken.None);

        Assert.Equal(expected, tooling.DolbyVisionConversion);
    }

    [Fact]
    public async Task An_audio_targets_bitrate_travels_as_bitrate_in_kbps()
    {
        var body = await PostAsync(new EngineAudioTarget(0, 1, "eac3", 640));

        // The engine's AudioTargetRequest names this field `bitrate`. `bitrateKbps` — the spelling the
        // domain type uses, where the unit belongs in the name — would be dropped on arrival, and ffmpeg
        // would scale its own default (448k for 5.1) instead of the 640k the dialog asked for.
        Assert.Contains("\"bitrate\":640", body);
        Assert.DoesNotContain("bitrateKbps", body);
        Assert.Contains("\"codec\":\"eac3\"", body);
        Assert.Contains("\"streamIndex\":1", body);
    }

    [Fact]
    public async Task An_omitted_bitrate_is_sent_as_null_so_the_engine_scales_its_own()
    {
        var body = await PostAsync(new EngineAudioTarget(0, 1, "eac3"));

        Assert.Contains("\"bitrate\":null", body);
    }

    [Fact]
    public async Task A_job_with_no_audio_targets_sends_none()
    {
        var body = await PostAsync();

        Assert.Contains("\"audioTargets\":null", body);
    }

    private static async Task<string> PostExtractionAsync(params EngineExtractionOutput[] outputs)
    {
        var handler = new StubHandler();
        using var engine = new RemoteTranscodeEngine(
            new HttpClient(handler) { BaseAddress = new Uri("http://engine.local/") },
            new MediaServerSettings(),
            NullLogger<RemoteTranscodeEngine>.Instance);

        await engine.CreateAsync(
            new TranscodeJobRequest(
                "movies", "in.mkv", OutputMountLabel: null, OutputRelativePath: null,
                "copy", "auto", null, Outputs: outputs),
            CancellationToken.None);

        return handler.RequestBody!;
    }

    [Fact]
    public async Task An_extractions_outputs_travel_under_the_names_the_engine_binds()
    {
        var body = await PostExtractionAsync(
            new EngineExtractionOutput("movies", "movie.rus.mka", 3, Codec: null, Language: "rus", Title: "AniDUB"));

        // The engine's OutputRequest names the destination `path`, not `relativePath` — the spelling the
        // domain type uses. A rename here is silent: the engine binds nothing, the entry arrives with an
        // empty path, and the job fails on something that reads nothing like a renamed field.
        Assert.Contains("\"path\":\"movie.rus.mka\"", body);
        Assert.DoesNotContain("relativePath", body);
        Assert.Contains("\"mountLabel\":\"movies\"", body);
        Assert.Contains("\"streamIndex\":3", body);
        Assert.Contains("\"language\":\"rus\"", body);
        Assert.Contains("\"title\":\"AniDUB\"", body);
        // A stream copy names no codec, which is what every extraction but a text conversion asks for.
        Assert.Contains("\"codec\":null", body);
    }

    [Fact]
    public async Task An_extraction_composes_no_output_of_its_own()
    {
        var body = await PostExtractionAsync(new EngineExtractionOutput(null, "movie.eng.srt", 5, "srt"));

        // outputPath and outputs are mutually exclusive on the engine: sending both is a 400, so an
        // extraction must send a null one.
        Assert.Contains("\"outputPath\":null", body);
        Assert.Contains("\"codec\":\"srt\"", body);
    }

    [Fact]
    public async Task An_ordinary_job_sends_no_outputs()
    {
        var body = await PostAsync();

        Assert.Contains("\"outputs\":null", body);
    }
}
