using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using MediaServer.Api.Probe;
using MediaServer.Api.Realtime;
using MediaServer.Api.Torrents;

namespace MediaServer.Api.Tests.Pipeline;

/// <summary>Records the candidates/metadata a test wants the identify/enrich stages to receive.</summary>
public sealed class FakeMetadataProvider : IMetadataProvider
{
    public string Key => "tmdb";

    public Func<MediaQuery, IReadOnlyList<MetadataCandidate>> OnSearch { get; set; } = _ => [];

    public Task<IReadOnlyList<MetadataCandidate>> SearchAsync(MediaQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(OnSearch(query));

    public Task<IReadOnlyList<ProviderMetadata>> FetchAsync(ProviderRef reference, IReadOnlyList<string> languages, CancellationToken cancellationToken)
    {
        var records = languages.Select(language => new ProviderMetadata(
            reference, language, $"Title {language}", "Original Title", "en",
            "Overview", "Tagline", ["Drama"], null, 7.5, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(120).Ticks, "{}")).ToList();
        return Task.FromResult<IReadOnlyList<ProviderMetadata>>(records);
    }

    public Task<IReadOnlyList<RemoteImage>> GetImagesAsync(ProviderRef reference, IReadOnlyList<string> languages, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RemoteImage>>([new RemoteImage(ImageType.Primary, "en", "https://image/poster.jpg", 0)]);
}

/// <summary>Returns a fixed probe result without invoking ffprobe.</summary>
public sealed class FakeMediaProbe : IMediaProbe
{
    public Task<ProbeResult> ProbeAsync(string absolutePath, CancellationToken cancellationToken) =>
        Task.FromResult(new ProbeResult("matroska", TimeSpan.FromMinutes(120).Ticks, 8_000_000, 1_000_000,
        [
            new ProbedStream(StreamType.Video, 0, "h264", "High", null, 1920, 1080, 23.976, 8, null, null, null, true, false),
            new ProbedStream(StreamType.Audio, 1, "aac", null, "en", null, null, null, null, null, 6, 48000, true, false),
        ]));
}

/// <summary>No-op torrent engine: the pipeline reads <see cref="Download.State"/>, not the engine.</summary>
public sealed class FakeTorrentEngine : ITorrentEngine
{
    public event EventHandler<string>? MetadataReceived { add { } remove { } }
    public event EventHandler<string>? DownloadCompleted { add { } remove { } }
    public event EventHandler<string>? DownloadErrored { add { } remove { } }

    public TorrentDescriptor Inspect(TorrentSource source) => new("hash", "Name", 0, false, []);
    public Task<TorrentDescriptor> AddAsync(TorrentSource source, string saveDirectory, TorrentLimits limits, bool autoStart, CancellationToken cancellationToken) =>
        Task.FromResult(new TorrentDescriptor("hash", "Name", 0, false, []));
    public Task PauseAsync(string infoHash, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ResumeAsync(string infoHash, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(string infoHash, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task RemoveAsync(string infoHash, bool deleteFiles, CancellationToken cancellationToken) => Task.CompletedTask;
    public TorrentSnapshot? GetSnapshot(string infoHash) => null;
    public IReadOnlyList<TorrentSnapshot> GetAllSnapshots() => [];
    public IReadOnlyList<TorrentFileInfo> GetFiles(string infoHash) => [];
}

/// <summary>Swallows realtime broadcasts.</summary>
public sealed class NullRealtimeNotifier : IRealtimeNotifier
{
    public Task DownloadProgressAsync(DownloadProgress progress, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DownloadStateChangedAsync(DownloadStateChanged change, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task IngestStageChangedAsync(IngestStageChanged change, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task JobChangedAsync(string eventName, JobEvent job, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
