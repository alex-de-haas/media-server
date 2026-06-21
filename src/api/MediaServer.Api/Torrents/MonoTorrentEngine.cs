using System.Collections.Concurrent;
using System.Net;
using MediaServer.Api.Configuration;
using MediaServer.Api.Hosty;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Connections;

namespace MediaServer.Api.Torrents;

/// <summary>
/// MonoTorrent-backed <see cref="ITorrentEngine"/> and hosted service. Owns the <see cref="ClientEngine"/>,
/// enables DHT/PEX/LSD and protocol encryption, binds the injected raw <c>HOSTY_PORT_TORRENT</c>, and
/// persists fast-resume/metadata under the app data dir so downloads survive restarts.
/// </summary>
public sealed class MonoTorrentEngine : ITorrentEngine, IHostedService, IDisposable
{
    private readonly MediaServerSettings _settings;
    private readonly HostyOptions _hosty;
    private readonly ILogger<MonoTorrentEngine> _logger;
    private readonly ConcurrentDictionary<string, TorrentManager> _managers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _completionRaised = new(StringComparer.OrdinalIgnoreCase);

    private ClientEngine? _engine;

    public MonoTorrentEngine(MediaServerSettings settings, HostyOptions hosty, ILogger<MonoTorrentEngine> logger)
    {
        _settings = settings;
        _hosty = hosty;
        _logger = logger;
    }

    public event EventHandler<string>? MetadataReceived;
    public event EventHandler<string>? DownloadCompleted;
    public event EventHandler<string>? DownloadErrored;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var port = _settings.TorrentPort ?? 6881;
        var bindAddress = TryParseBindAddress(_settings.TorrentBindAddress);
        var cacheDirectory = Path.Combine(_hosty.AppDataDir, "torrent-engine");
        Directory.CreateDirectory(cacheDirectory);

        var builder = new EngineSettingsBuilder
        {
            CacheDirectory = cacheDirectory,
            AllowPortForwarding = _settings.TorrentEnablePortMapping,
            AllowLocalPeerDiscovery = true,
            AutoSaveLoadFastResume = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            AllowedEncryption = [EncryptionType.RC4Header, EncryptionType.RC4Full, EncryptionType.PlainText],
            MaximumDownloadRate = _settings.TorrentMaxDownloadSpeed,
            MaximumUploadRate = _settings.TorrentMaxUploadSpeed,
            ListenEndPoints = new Dictionary<string, IPEndPoint>
            {
                ["ipv4"] = new IPEndPoint(bindAddress ?? IPAddress.Any, port),
                ["ipv6"] = new IPEndPoint(IPAddress.IPv6Any, port),
            },
            DhtEndPoint = new IPEndPoint(bindAddress ?? IPAddress.Any, port),
        };

        _engine = new ClientEngine(builder.ToSettings());
        _logger.LogInformation("Torrent engine started on port {Port} (port mapping: {PortMapping}).", port, _settings.TorrentEnablePortMapping);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_engine is not null)
        {
            try
            {
                await _engine.StopAllAsync(TimeSpan.FromSeconds(10));
                await _engine.SaveStateAsync();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Error while stopping the torrent engine.");
            }
        }
    }

    public TorrentDescriptor Inspect(TorrentSource source)
    {
        switch (source)
        {
            case TorrentSource.Magnet magnet:
            {
                if (!MagnetLink.TryParse(magnet.Uri, out var link))
                {
                    throw new ArgumentException("Invalid magnet link.", nameof(source));
                }

                return new TorrentDescriptor(HashOf(link.InfoHashes), link.Name, link.Size, HasMetadata: false, []);
            }

            case TorrentSource.File file:
            {
                var torrent = Torrent.Load(file.Content.AsSpan());
                var files = MapFiles(torrent.Files, torrent.Name);
                return new TorrentDescriptor(HashOf(torrent.InfoHashes), torrent.Name, torrent.Size, HasMetadata: true, files);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(source));
        }
    }

    public async Task<TorrentDescriptor> AddAsync(
        TorrentSource source, string saveDirectory, TorrentLimits limits, bool autoStart, CancellationToken cancellationToken)
    {
        var engine = RequireEngine();
        Directory.CreateDirectory(saveDirectory);

        var torrentSettings = new TorrentSettingsBuilder
        {
            AllowDht = true,
            AllowPeerExchange = true,
            CreateContainingDirectory = true,
            MaximumDownloadRate = limits.MaxDownloadRate,
            MaximumUploadRate = limits.MaxUploadRate,
        }.ToSettings();

        TorrentManager manager;
        TorrentDescriptor descriptor;

        switch (source)
        {
            case TorrentSource.Magnet magnet:
            {
                if (!MagnetLink.TryParse(magnet.Uri, out var link))
                {
                    throw new ArgumentException("Invalid magnet link.", nameof(source));
                }

                manager = await engine.AddAsync(link, saveDirectory, torrentSettings);
                descriptor = new TorrentDescriptor(HashOf(link.InfoHashes), link.Name, link.Size, HasMetadata: false, []);
                break;
            }

            case TorrentSource.File file:
            {
                var torrent = await Torrent.LoadAsync(file.Content.AsMemory());
                manager = await engine.AddAsync(torrent, saveDirectory, torrentSettings);
                descriptor = new TorrentDescriptor(
                    HashOf(torrent.InfoHashes), torrent.Name, torrent.Size, HasMetadata: true, MapFiles(torrent.Files, torrent.Name));
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(source));
        }

        _managers[descriptor.InfoHash] = manager;
        manager.TorrentStateChanged += OnTorrentStateChanged;

        if (autoStart)
        {
            await manager.StartAsync();
        }

        if (!descriptor.HasMetadata)
        {
            _ = WaitForMetadataAsync(descriptor.InfoHash, manager);
        }
        else
        {
            RaiseMetadata(descriptor.InfoHash);
        }

        return descriptor;
    }

    public async Task PauseAsync(string infoHash, CancellationToken cancellationToken)
    {
        if (_managers.TryGetValue(infoHash, out var manager))
        {
            await manager.PauseAsync();
        }
    }

    public async Task ResumeAsync(string infoHash, CancellationToken cancellationToken)
    {
        if (_managers.TryGetValue(infoHash, out var manager))
        {
            await manager.StartAsync();
        }
    }

    public async Task StopAsync(string infoHash, CancellationToken cancellationToken)
    {
        if (_managers.TryGetValue(infoHash, out var manager))
        {
            await manager.StopAsync();
        }
    }

    public async Task RemoveAsync(string infoHash, bool deleteFiles, CancellationToken cancellationToken)
    {
        var engine = RequireEngine();
        if (!_managers.TryGetValue(infoHash, out var manager))
        {
            return;
        }

        // A manager must be stopped before it can be unregistered. Stop it first (idempotent: a manager
        // already stopped/stopping or errored just falls through), then remove. Doing the bookkeeping only
        // after a successful removal avoids leaking a half-removed manager if stop/remove throws.
        try
        {
            if (manager.State is not (TorrentState.Stopped or TorrentState.Stopping or TorrentState.Error))
            {
                await manager.StopAsync(TimeSpan.FromSeconds(10));
            }
        }
        catch (TorrentException exception)
        {
            _logger.LogWarning(exception, "Stopping torrent {InfoHash} before removal failed; removing anyway.", infoHash);
        }

        // DownloadedDataOnly deletes the downloaded files; KeepAllData leaves them on disk (the move-based
        // organizer takes ownership of the playable file). CacheDataOnly is always set so the fast-resume
        // cache is cleared too — otherwise a re-added torrent trusts stale "complete" resume data and skips
        // re-downloading even though the files are gone. See torrents-and-organizer Removal Semantics.
        var mode = (deleteFiles ? RemoveMode.DownloadedDataOnly : RemoveMode.KeepAllData) | RemoveMode.CacheDataOnly;
        await engine.RemoveAsync(manager, mode);

        _managers.TryRemove(infoHash, out _);
        manager.TorrentStateChanged -= OnTorrentStateChanged;
        _completionRaised.TryRemove(infoHash, out _);
    }

    public TorrentSnapshot? GetSnapshot(string infoHash) =>
        _managers.TryGetValue(infoHash, out var manager) ? ToSnapshot(infoHash, manager) : null;

    public IReadOnlyList<TorrentSnapshot> GetAllSnapshots() =>
        _managers.Select(pair => ToSnapshot(pair.Key, pair.Value)).ToList();

    public IReadOnlyList<TorrentFileInfo> GetFiles(string infoHash) =>
        _managers.TryGetValue(infoHash, out var manager) && manager.HasMetadata
            ? MapManagerFiles(manager)
            : [];

    private async Task WaitForMetadataAsync(string infoHash, TorrentManager manager)
    {
        try
        {
            await manager.WaitForMetadataAsync();
            RaiseMetadata(infoHash);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed waiting for metadata of {InfoHash}.", infoHash);
        }
    }

    private void RaiseMetadata(string infoHash) => MetadataReceived?.Invoke(this, infoHash);

    private void OnTorrentStateChanged(object? sender, TorrentStateChangedEventArgs args)
    {
        if (sender is not TorrentManager manager)
        {
            return;
        }

        var infoHash = HashOf(manager.InfoHashes);

        if (args.NewState == TorrentState.Error)
        {
            DownloadErrored?.Invoke(this, infoHash);
            return;
        }

        // MonoTorrent transitions Downloading → Seeding the moment a torrent completes; a freshly
        // re-added complete torrent also lands in Seeding after hashing. Raise completion once.
        if ((args.NewState == TorrentState.Seeding || manager.Complete) && _completionRaised.TryAdd(infoHash, 0))
        {
            DownloadCompleted?.Invoke(this, infoHash);
        }
    }

    private static TorrentSnapshot ToSnapshot(string infoHash, TorrentManager manager)
    {
        var downloaded = manager.Monitor.DataBytesReceived;
        var uploaded = manager.Monitor.DataBytesSent;
        var ratio = downloaded > 0 ? Math.Round(uploaded / (double)downloaded, 3) : 0;
        var size = manager.Torrent?.Size ?? manager.MagnetLink?.Size ?? 0;

        return new TorrentSnapshot(
            infoHash,
            manager.Name,
            manager.State.ToString(),
            manager.Complete,
            Math.Round(manager.Progress, 2),
            manager.Monitor.DownloadRate,
            manager.Monitor.UploadRate,
            ratio,
            manager.OpenConnections,
            size);
    }

    private static IReadOnlyList<TorrentFileInfo> MapManagerFiles(TorrentManager manager)
    {
        // RelativePath is expressed against the save directory (the catalog files/ dir), so the
        // organizer can rebuild the absolute path as Combine(filesDir, RelativePath).
        var files = new List<TorrentFileInfo>(manager.Files.Count);
        for (var index = 0; index < manager.Files.Count; index++)
        {
            var file = manager.Files[index];
            var relative = NormalizeRelative(Path.GetRelativePath(manager.SavePath, file.FullPath));
            files.Add(new TorrentFileInfo(index, relative, file.Length));
        }

        return files;
    }

    private static IReadOnlyList<TorrentFileInfo> MapFiles(IList<ITorrentFile> torrentFiles, string torrentName)
    {
        // CreateContainingDirectory places single- and multi-file torrents under <name>/, so the
        // on-disk path relative to files/ is <name>/<file.Path>.
        var files = new List<TorrentFileInfo>(torrentFiles.Count);
        for (var index = 0; index < torrentFiles.Count; index++)
        {
            var file = torrentFiles[index];
            var relative = NormalizeRelative(Path.Combine(torrentName, file.Path));
            files.Add(new TorrentFileInfo(index, relative, file.Length));
        }

        return files;
    }

    private static string NormalizeRelative(string path) => path.Replace('\\', '/');

    private static string HashOf(InfoHashes infoHashes) => infoHashes.V1OrV2.ToHex();

    private static IPAddress? TryParseBindAddress(string? address) =>
        !string.IsNullOrWhiteSpace(address) && IPAddress.TryParse(address, out var parsed) ? parsed : null;

    private ClientEngine RequireEngine() =>
        _engine ?? throw new InvalidOperationException("Torrent engine has not started.");

    public void Dispose() => _engine?.Dispose();
}
