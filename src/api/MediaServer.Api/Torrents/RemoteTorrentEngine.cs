using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaServer.Api.Configuration;

namespace MediaServer.Api.Torrents;

/// <summary>
/// <see cref="ITorrentEngine"/> backed by the external <c>torrent-engine</c> app over its HTTP control
/// API + SSE stream. Downloading runs in that (VPN-isolated) app; this client mirrors its live state
/// into local caches from the event stream and re-raises the engine events the coordinator consumes.
/// Parsing (<see cref="Inspect"/>) stays local — it needs no network and yields the info hash before a
/// download row is created. Selected only when <c>TorrentEngineUrl</c> is configured.
/// </summary>
public sealed class RemoteTorrentEngine : ITorrentEngine, IHostedService, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly MediaServerSettings _settings;
    private readonly ILogger<RemoteTorrentEngine> _logger;
    private readonly ConcurrentDictionary<string, TorrentSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<TorrentFileInfo>> _files = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cts;
    private Task? _streamLoop;

    public RemoteTorrentEngine(HttpClient http, MediaServerSettings settings, ILogger<RemoteTorrentEngine> logger)
    {
        _http = http;
        _settings = settings;
        _logger = logger;
    }

    public event EventHandler<string>? MetadataReceived;
    public event EventHandler<string>? DownloadCompleted;
    public event EventHandler<string>? DownloadErrored;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Seed the snapshot cache so progress/list calls work immediately after a restart while the
        // event stream is still connecting.
        try
        {
            var snapshots = await _http.GetFromJsonAsync<List<TorrentSnapshot>>("/downloads", Json, cancellationToken);
            foreach (var snapshot in snapshots ?? [])
            {
                _snapshots[snapshot.InfoHash] = snapshot;
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not seed torrent state from {Url}; will populate from the event stream.", _settings.TorrentEngineUrl);
        }

        _cts = new CancellationTokenSource();
        _streamLoop = Task.Run(() => ConsumeEventsAsync(_cts.Token));
        _logger.LogInformation("Remote torrent engine bound to {Url}.", _settings.TorrentEngineUrl);
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

    public TorrentDescriptor Inspect(TorrentSource source) => LocalTorrentInspector.Inspect(source);

    public async Task<TorrentDescriptor> AddAsync(
        TorrentSource source, string saveDirectory, TorrentLimits limits, bool autoStart, CancellationToken cancellationToken)
    {
        var request = new AddDownloadRequest(
            (source as TorrentSource.Magnet)?.Uri,
            source is TorrentSource.File file ? Convert.ToBase64String(file.Content) : null,
            ToMountRelative(saveDirectory, _settings.CatalogMountRoots),
            limits.MaxDownloadRate,
            limits.MaxUploadRate,
            autoStart);

        using var response = await _http.PostAsJsonAsync("/downloads", request, Json, cancellationToken);

        // A re-add after a media-server restart hits a torrent the engine already has: treat 409 as
        // success so resume is idempotent.
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var existing = LocalTorrentInspector.Inspect(source);
            SeedInitial(existing);
            return existing;
        }

        response.EnsureSuccessStatusCode();
        var descriptor = await response.Content.ReadFromJsonAsync<TorrentDescriptor>(Json, cancellationToken)
            ?? throw new InvalidOperationException("torrent-engine returned an empty descriptor.");
        SeedInitial(descriptor);
        return descriptor;
    }

    public Task PauseAsync(string infoHash, CancellationToken cancellationToken) => PostAsync($"/downloads/{infoHash}/pause", cancellationToken);

    public Task ResumeAsync(string infoHash, CancellationToken cancellationToken) => PostAsync($"/downloads/{infoHash}/resume", cancellationToken);

    public Task StopAsync(string infoHash, CancellationToken cancellationToken) => PostAsync($"/downloads/{infoHash}/stop", cancellationToken);

    public async Task RemoveAsync(string infoHash, bool deleteFiles, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.DeleteAsync($"/downloads/{infoHash}?deleteFiles={deleteFiles.ToString().ToLowerInvariant()}", cancellationToken);
            // Treat a missing torrent as already-removed.
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                response.EnsureSuccessStatusCode();
            }
        }
        finally
        {
            _snapshots.TryRemove(infoHash, out _);
            _files.TryRemove(infoHash, out _);
        }
    }

    public TorrentSnapshot? GetSnapshot(string infoHash) => _snapshots.GetValueOrDefault(infoHash);

    public IReadOnlyList<TorrentSnapshot> GetAllSnapshots() => _snapshots.Values.ToList();

    public IReadOnlyList<TorrentFileInfo> GetFiles(string infoHash) => _files.GetValueOrDefault(infoHash) ?? [];

    private async Task PostAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync(path, content: null, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    private void SeedInitial(TorrentDescriptor descriptor)
    {
        _snapshots.TryAdd(descriptor.InfoHash, new TorrentSnapshot(
            descriptor.InfoHash, descriptor.Name, "Downloading", Complete: false, 0, 0, 0, 0, 0, descriptor.TotalSize ?? 0));
        if (descriptor.Files.Count > 0)
        {
            _files[descriptor.InfoHash] = descriptor.Files;
        }
    }

    /// <summary>Translates the absolute local save directory (<c>{catalogRoot}/.incoming/{id}</c>) into a
    /// path relative to the shared downloads mount, which the engine resolves against its own mount root
    /// (the same host path). Falls back to the trailing <c>.incoming/{id}</c> segments.</summary>
    internal static string ToMountRelative(string saveDirectory, IReadOnlyList<string> mountRoots)
    {
        var full = Path.GetFullPath(saveDirectory);
        foreach (var root in mountRoots)
        {
            var rootFull = Path.GetFullPath(root);
            if (string.Equals(full, rootFull, StringComparison.Ordinal) ||
                full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return Path.GetRelativePath(rootFull, full).Replace('\\', '/');
            }
        }

        var parent = Path.GetFileName(Path.GetDirectoryName(full)) ?? string.Empty;
        return $"{parent}/{Path.GetFileName(full)}".TrimStart('/');
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
                        await HandleEventAsync(data, cancellationToken);
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
                _logger.LogWarning(exception, "Torrent event stream dropped; reconnecting.");
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

    private async Task HandleEventAsync(string data, CancellationToken cancellationToken)
    {
        RemoteEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<RemoteEvent>(data, Json);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Could not parse a torrent event: {Data}", data);
            return;
        }

        if (evt is null || string.IsNullOrEmpty(evt.InfoHash))
        {
            return;
        }

        if (evt.Snapshot is not null)
        {
            _snapshots[evt.InfoHash] = evt.Snapshot;
        }

        switch (evt.Type)
        {
            case "metadata-received":
                await CacheFilesAsync(evt.InfoHash, cancellationToken);
                MetadataReceived?.Invoke(this, evt.InfoHash);
                break;
            case "completed":
                await CacheFilesAsync(evt.InfoHash, cancellationToken);
                DownloadCompleted?.Invoke(this, evt.InfoHash);
                break;
            case "errored":
                DownloadErrored?.Invoke(this, evt.InfoHash);
                break;
            // "progress": snapshot already cached above.
        }
    }

    // Fetch + cache the file list before raising the event, so the coordinator's synchronous GetFiles
    // (called from its handlers) sees them.
    private async Task CacheFilesAsync(string infoHash, CancellationToken cancellationToken)
    {
        try
        {
            var files = await _http.GetFromJsonAsync<List<TorrentFileInfo>>($"/downloads/{infoHash}/files", Json, cancellationToken);
            if (files is not null)
            {
                _files[infoHash] = files;
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not fetch files for {InfoHash}.", infoHash);
        }
    }

    public void Dispose() => _cts?.Dispose();

    private sealed record AddDownloadRequest(
        string? Magnet, string? TorrentBase64, string? SavePath, int MaxDownloadRate, int MaxUploadRate, bool AutoStart);

    private sealed record RemoteEvent(string Type, string InfoHash, TorrentSnapshot? Snapshot);
}
