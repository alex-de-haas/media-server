using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaServer.Api.Configuration;

namespace MediaServer.Api.Transcoding;

/// <summary>
/// <see cref="ITranscodeEngine"/> backed by the external <c>transcode-engine</c> app over its HTTP control
/// API + SSE stream. Transcoding runs in that container (with the host's <c>/dev/dri</c> passed through);
/// this client mirrors its live state into a local cache from the event stream and re-raises the engine
/// events a consumer surface consumes. Selected only when <c>TranscodeEngineUrl</c> is configured.
/// </summary>
public sealed class RemoteTranscodeEngine : ITranscodeEngine, IHostedService, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Short timeout for control calls. The shared HttpClient has no timeout (the SSE stream is
    // long-lived), so each non-streaming request gets its own deadline to avoid hanging a caller if the
    // engine becomes unresponsive.
    private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly MediaServerSettings _settings;
    private readonly ILogger<RemoteTranscodeEngine> _logger;
    private readonly ConcurrentDictionary<string, JobSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cts;
    private Task? _streamLoop;

    public RemoteTranscodeEngine(HttpClient http, MediaServerSettings settings, ILogger<RemoteTranscodeEngine> logger)
    {
        _http = http;
        _settings = settings;
        _logger = logger;
    }

    public event EventHandler<string>? JobStarted;
    public event EventHandler<string>? JobCompleted;
    public event EventHandler<string>? JobFailed;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Seed the snapshot cache so list/progress calls work immediately after a restart while the event
        // stream is still connecting.
        try
        {
            using var cts = ControlCts(cancellationToken);
            var snapshots = await _http.GetFromJsonAsync<List<JobSnapshot>>("/jobs", Json, cts.Token);
            foreach (var snapshot in snapshots ?? [])
            {
                _snapshots[snapshot.JobId] = snapshot;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Could not seed transcode state from {Url}; will populate from the event stream.", _settings.TranscodeEngineUrl);
        }

        _cts = new CancellationTokenSource();
        _streamLoop = Task.Run(() => ConsumeEventsAsync(_cts.Token));
        _logger.LogInformation("Remote transcode engine bound to {Url}.", _settings.TranscodeEngineUrl);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_streamLoop is not null)
        {
            try
            {
                await _streamLoop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
            {
                // Best-effort shutdown.
            }
        }
    }

    public async Task<JobDescriptor> CreateAsync(TranscodeJobRequest request, CancellationToken cancellationToken)
    {
        var wire = new WireCreateJobRequest(
            request.InputMountLabel,
            request.InputRelativePath,
            request.OutputMountLabel,
            request.OutputRelativePath,
            request.VideoCodec,
            request.HardwareAcceleration,
            request.QualityLevel,
            request.MaxHeight,
            request.AudioStreamIndexes,
            request.SubtitleStreamIndexes,
            request.DefaultAudioStreamIndex,
            request.DefaultSubtitleStreamIndex,
            request.AdditionalInputs?
                .Select(input => new WireAdditionalInput(
                    input.MountLabel, input.RelativePath, input.AudioStreamIndexes, input.SubtitleStreamIndexes))
                .ToList(),
            request.MetadataOverrides?
                .Select(entry => new WireMetadataOverride(entry.Input, entry.StreamIndex, entry.Language, entry.Title))
                .ToList(),
            request.AudioTargets?
                .Select(entry => new WireAudioTarget(entry.Input, entry.StreamIndex, entry.Codec, entry.BitrateKbps))
                .ToList(),
            request.Outputs?
                .Select(entry => new WireOutput(
                    entry.MountLabel, entry.RelativePath, entry.StreamIndex, entry.Codec, entry.Language, entry.Title))
                .ToList(),
            request.DolbyVision);

        using var cts = ControlCts(cancellationToken);
        using var response = await _http.PostAsJsonAsync("/jobs", wire, Json, cts.Token);

        // Surface the engine's own error (e.g. an unknown mountLabel or a missing input) instead of a bare
        // status code, so a caller gets an actionable message.
        if (!response.IsSuccessStatusCode)
        {
            var detail = await ReadEngineErrorAsync(response, cts.Token);
            throw new InvalidOperationException(detail is null
                ? $"transcode-engine rejected the job ({(int)response.StatusCode})."
                : $"transcode-engine rejected the job: {detail}");
        }

        var descriptor = await response.Content.ReadFromJsonAsync<JobDescriptor>(Json, cts.Token)
            ?? throw new InvalidOperationException("transcode-engine returned an empty descriptor.");
        SeedInitial(descriptor);
        return descriptor;
    }

    public Task CancelAsync(string jobId, CancellationToken cancellationToken) => PostAsync($"/jobs/{jobId}/cancel", cancellationToken);

    public async Task RemoveAsync(string jobId, bool deleteOutput, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = ControlCts(cancellationToken);
            using var response = await _http.DeleteAsync($"/jobs/{jobId}?deleteOutput={deleteOutput.ToString().ToLowerInvariant()}", cts.Token);
            // Treat a missing job as already-removed.
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                response.EnsureSuccessStatusCode();
            }
        }
        finally
        {
            _snapshots.TryRemove(jobId, out _);
        }
    }

    public JobSnapshot? GetSnapshot(string jobId) => _snapshots.GetValueOrDefault(jobId);

    public async Task<TranscodeTooling> GetToolingAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var cts = ControlCts(cancellationToken);
            var hardware = await _http.GetFromJsonAsync<WireHardware>("/hardware", Json, cts.Token);
            return new TranscodeTooling(hardware?.Tools?.DolbyVisionConversion == true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Informational: an engine that cannot answer offers no tooling, and the job it would have refused
            // is simply not offered.
            _logger.LogDebug(exception, "Could not read the transcode engine's tooling from {Url}.", _settings.TranscodeEngineUrl);
            return TranscodeTooling.None;
        }
    }

    public IReadOnlyList<JobSnapshot> GetAllSnapshots() => _snapshots.Values.ToList();

    private async Task PostAsync(string path, CancellationToken cancellationToken)
    {
        using var cts = ControlCts(cancellationToken);
        using var response = await _http.PostAsync(path, content: null, cts.Token);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    // Best-effort read of the engine's `{ "error": "..." }` body on a non-success response.
    private static async Task<string?> ReadEngineErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<EngineError>(Json, cancellationToken);
            return string.IsNullOrWhiteSpace(body?.Error) ? null : body.Error;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private void SeedInitial(JobDescriptor descriptor) =>
        _snapshots.TryAdd(descriptor.JobId, new JobSnapshot(
            descriptor.JobId,
            // An extraction has no single output to be named after, so — like the engine's own snapshot — it
            // is named for what it reads.
            System.IO.Path.GetFileName(descriptor.OutputPath ?? descriptor.InputPath),
            "Queued", Complete: false, 0, 0, 0, 0, EtaSeconds: null));

    // Links the caller's token with a per-request deadline for control (non-streaming) calls.
    private static CancellationTokenSource ControlCts(CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ControlTimeout);
        return cts;
    }

    private async Task ConsumeEventsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var response = await _http.GetAsync("/events", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream);

                string? data = null;
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line is null)
                    {
                        break;
                    }

                    if (line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        data = line[5..].Trim();
                    }
                    else if (line.Length == 0 && data is not null)
                    {
                        HandleEvent(data);
                        data = null;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Transcode event stream dropped; reconnecting.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void HandleEvent(string data)
    {
        RemoteEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<RemoteEvent>(data, Json);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Could not parse a transcode event: {Data}", data);
            return;
        }

        if (evt is null || string.IsNullOrEmpty(evt.JobId))
        {
            return;
        }

        if (evt.Snapshot is not null)
        {
            _snapshots[evt.JobId] = evt.Snapshot;
        }

        switch (evt.Type)
        {
            case "started":
                JobStarted?.Invoke(this, evt.JobId);
                break;
            case "completed":
                JobCompleted?.Invoke(this, evt.JobId);
                break;
            case "errored":
                JobFailed?.Invoke(this, evt.JobId);
                break;
            // "progress": snapshot already cached above.
        }
    }

    public void Dispose()
    {
        // The same instance is registered three ways (RemoteTranscodeEngine, ITranscodeEngine, and the hosted
        // service), so the DI container can dispose it more than once. Take the CTS atomically so Dispose is
        // idempotent — a second pass must not Cancel/Dispose an already-disposed source.
        if (Interlocked.Exchange(ref _cts, null) is { } cts)
        {
            cts.Cancel();
            cts.Dispose();
        }

        _http.Dispose(); // Owned: created per-instance in Program.cs.
    }

    // The Wire* records below are the engine's JSON schema, not this app's vocabulary: every member name is
    // serialized verbatim (camelCased by JsonSerializerDefaults.Web) and has to keep matching the engine's
    // own contract types. Renaming one for readability silently drops the field rather than failing — a
    // positional record parameter the engine cannot bind simply stays null — so clarity belongs in a comment
    // here, and the names that carry units live on the domain types these are mapped from.
    private sealed record WireCreateJobRequest(
        string? InputMountLabel, string InputPath, string? OutputMountLabel, string? OutputPath, string VideoCodec, string HardwareAcceleration, string? QualityLevel,
        int? MaxHeight = null, IReadOnlyList<int>? AudioStreamIndexes = null, IReadOnlyList<int>? SubtitleStreamIndexes = null,
        int? DefaultAudioStreamIndex = null, int? DefaultSubtitleStreamIndex = null,
        IReadOnlyList<WireAdditionalInput>? AdditionalInputs = null,
        IReadOnlyList<WireMetadataOverride>? MetadataOverrides = null,
        IReadOnlyList<WireAudioTarget>? AudioTargets = null,
        IReadOnlyList<WireOutput>? Outputs = null,
        string? DolbyVision = null);

    /// <summary>The engine's <c>GET /hardware</c>, read for its <c>tools</c> block alone.</summary>
    private sealed record WireHardware(WireTools? Tools);

    private sealed record WireTools(bool DolbyVisionConversion, string? DoviTool, string? Mkvtoolnix);

    /// <summary>The engine's <c>outputs</c> entry — one stream written to its own file. <c>Codec</c> is left
    /// null for a stream copy, which is what every extraction but a text-subtitle conversion asks for.</summary>
    private sealed record WireOutput(
        string? MountLabel, string Path, int StreamIndex, string? Codec, string? Language, string? Title);

    private sealed record WireMetadataOverride(int Input, int StreamIndex, string? Language, string? Title);

    /// <summary><c>Bitrate</c> is in <b>kbps</b>, which is the unit the engine's <c>bitrate</c> field takes —
    /// unlike <c>MediaStream.Bitrate</c>, which is bits per second. It is mapped from
    /// <see cref="EngineAudioTarget.BitrateKbps"/>, where the unit is stated in the name.</summary>
    private sealed record WireAudioTarget(int Input, int StreamIndex, string Codec, int? Bitrate);

    private sealed record WireAdditionalInput(
        string? MountLabel, string Path, IReadOnlyList<int>? AudioStreamIndexes, IReadOnlyList<int>? SubtitleStreamIndexes);

    private sealed record EngineError(string? Error);

    private sealed record RemoteEvent(string Type, string JobId, JobSnapshot? Snapshot);
}
