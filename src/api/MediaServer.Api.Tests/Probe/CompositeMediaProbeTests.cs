using System.Net;
using System.Text;
using MediaServer.Api.Configuration;
using MediaServer.Api.Probe;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static MediaServer.Api.Tests.Probe.ContainerBuilders;

namespace MediaServer.Api.Tests.Probe;

/// <summary>
/// How the two providers combine. The engine leads so an attached deployment behaves as it always has; the
/// header reader follows so an absent or failing one degrades instead of parking an ingest.
/// </summary>
public sealed class CompositeMediaProbeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("composite-probe").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string WriteMatroska(double durationTicks, string? writingApp = null)
    {
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.mkv");
        File.WriteAllBytes(path, Matroska(
            Info(durationTicks, writingApp: writingApp),
            Tracks(TrackEntry(1, "V_MPEGH/ISO/HEVC", "eng", width: 1920, height: 1080))));
        return path;
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>Captures what was logged, so the divergence report can be asserted on rather than assumed.</summary>
    private sealed class CapturingLogger : ILogger<CompositeMediaProbe>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (level == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    private RemoteMediaProbe Remote(HttpStatusCode status, string body, out StubHandler handler)
    {
        handler = new StubHandler(status, body);
        return new RemoteMediaProbe(
            new HttpClient(handler) { BaseAddress = new Uri("http://engine.local/") },
            new MediaServerSettings { CatalogMountRoots = [new CatalogMount("media", _root)] },
            NullLogger<RemoteMediaProbe>.Instance);
    }

    private static string EngineJson(double seconds) => $$"""
        {"container":"mkv","durationSeconds":{{seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
         "sizeBytes":10,"streams":[{"index":0,"kind":"Video","codec":"hevc","profile":"Main 10",
         "hdr":"Hdr10","isDefault":true,"isForced":false}]}
        """;

    private static CompositeMediaProbe Composite(RemoteMediaProbe? remote, ILogger<CompositeMediaProbe>? logger = null) =>
        new(remote, new HeaderMediaProbe(NullLogger<HeaderMediaProbe>.Instance), logger ?? NullLogger<CompositeMediaProbe>.Instance);

    [Fact]
    public async Task The_engines_answer_wins_when_it_can_give_one()
    {
        var path = WriteMatroska(137_463);
        var probe = Composite(Remote(HttpStatusCode.OK, EngineJson(7506.291), out _));

        var result = await probe.ProbeAsync(path, CancellationToken.None);

        Assert.Equal(ProbeSource.Engine, result.Source);
        // The profile is something only the engine knows — proof the richer answer is the one kept.
        Assert.Equal("Main 10", Assert.Single(result.Streams).Profile);
    }

    [Fact]
    public async Task A_refusing_engine_degrades_to_the_container_header()
    {
        var path = WriteMatroska(137_463);
        var probe = Composite(Remote(HttpStatusCode.BadRequest, "{}", out _));

        var result = await probe.ProbeAsync(path, CancellationToken.None);

        Assert.Equal(ProbeSource.Header, result.Source);
        Assert.Equal(137.463, result.DurationTicks / (double)TimeSpan.TicksPerSecond, 3);
    }

    [Fact]
    public async Task With_no_engine_configured_the_header_answers_and_nothing_is_asked()
    {
        var path = WriteMatroska(137_463);

        var result = await Composite(remote: null).ProbeAsync(path, CancellationToken.None);

        Assert.Equal(ProbeSource.Header, result.Source);
    }

    [Fact]
    public async Task A_file_neither_can_read_still_fails()
    {
        // Degrading is not the same as inventing: when nothing can be read, the caller has to hear about it.
        var path = Path.Combine(_root, "notes.ts");
        await File.WriteAllTextAsync(path, "not media");
        var probe = Composite(Remote(HttpStatusCode.BadRequest, "{}", out _));

        await Assert.ThrowsAsync<InvalidOperationException>(() => probe.ProbeAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task A_material_disagreement_is_logged_with_what_it_takes_to_group_it()
    {
        // The header claims 137.463 s; the engine says 300 s. A pattern by writing application is what found
        // the OpenDML defect, so the report has to name it.
        var path = WriteMatroska(137_463, writingApp: "mkvmerge v82.0");
        var logger = new CapturingLogger();
        var probe = Composite(Remote(HttpStatusCode.OK, EngineJson(300), out _), logger);

        await probe.ProbeAsync(path, CancellationToken.None);

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("mkvmerge v82.0", warning);
        Assert.Contains("137.463", warning);
        Assert.Contains("300.000", warning);
    }

    [Fact]
    public async Task Container_noise_is_not_reported_as_a_disagreement()
    {
        // Measured, the natural spread between a header and ffprobe tops out at 57 ms on a 2 h file: the
        // video and audio tracks are not the same length. Logging that would bury the real defects.
        var path = WriteMatroska(137_463);
        var logger = new CapturingLogger();
        var probe = Composite(Remote(HttpStatusCode.OK, EngineJson(137.52), out _), logger);

        await probe.ProbeAsync(path, CancellationToken.None);

        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public async Task Nothing_is_reported_when_only_one_provider_could_answer()
    {
        // A container the header reader does not support gives it nothing to disagree with.
        var path = Path.Combine(_root, "clip.ts");
        await File.WriteAllTextAsync(path, "not media");
        var logger = new CapturingLogger();
        var probe = Composite(Remote(HttpStatusCode.OK, EngineJson(300), out _), logger);

        await probe.ProbeAsync(path, CancellationToken.None);

        Assert.Empty(logger.Warnings);
    }
}
