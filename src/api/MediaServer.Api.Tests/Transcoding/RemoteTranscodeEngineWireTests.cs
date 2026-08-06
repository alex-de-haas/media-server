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
}
