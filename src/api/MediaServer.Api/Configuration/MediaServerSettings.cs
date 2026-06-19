namespace MediaServer.Api.Configuration;

/// <summary>
/// Application settings injected by Hosty from the manifest <c>settings</c> block (as environment
/// variables with the declared keys) plus the catalog-root mounts. Secrets and global toggles come
/// from here, never the database (see <c>docs/planning/domain-model.md</c>).
/// </summary>
public sealed class MediaServerSettings
{
    /// <summary>TMDb API key (manifest <c>TMDB_API_KEY</c>, secret).</summary>
    public string? TmdbApiKey { get; init; }

    /// <summary>Ordered supported languages; the first entry is the fallback (e.g. <c>en-US</c>).</summary>
    public IReadOnlyList<string> SupportedLanguages { get; init; } = ["en-US"];

    public string JellyfinServerName { get; init; } = "Media Server";

    public bool JellyfinDiscoveryEnabled { get; init; }

    /// <summary>Override path to the <c>ffprobe</c> binary; falls back to PATH lookup when null.</summary>
    public string? FfprobePath { get; init; }

    public bool TorrentEnablePortMapping { get; init; } = true;

    public string? TorrentBindAddress { get; init; }

    /// <summary>Bytes/sec; 0 means unlimited.</summary>
    public int TorrentMaxDownloadSpeed { get; init; }

    public int TorrentMaxUploadSpeed { get; init; }

    /// <summary>Raw L4 torrent port injected as <c>HOSTY_PORT_TORRENT</c>; null falls back to a default.</summary>
    public int? TorrentPort { get; init; }

    /// <summary>
    /// Catalog-root mounts from <c>HOSTY_MOUNT_CATALOGROOTS</c>. Each operator-configured catalog
    /// root must live within one of these (when provided). May be empty under standalone local runs.
    /// </summary>
    public IReadOnlyList<string> CatalogMountRoots { get; init; } = [];

    public static MediaServerSettings FromConfiguration(IConfiguration configuration)
    {
        string? Read(string key) => configuration[key] is { Length: > 0 } value ? value.Trim() : null;
        bool ReadBool(string key, bool fallback) =>
            bool.TryParse(Read(key), out var parsed) ? parsed : fallback;
        int ReadInt(string key, int fallback) =>
            int.TryParse(Read(key), out var parsed) ? parsed : fallback;

        var languages = (Read("SUPPORTED_LANGUAGES") ?? "en-US")
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new MediaServerSettings
        {
            TmdbApiKey = Read("TMDB_API_KEY"),
            SupportedLanguages = languages.Length > 0 ? languages : ["en-US"],
            JellyfinServerName = Read("JELLYFIN_SERVER_NAME") ?? "Media Server",
            JellyfinDiscoveryEnabled = ReadBool("JELLYFIN_DISCOVERY_ENABLED", false),
            FfprobePath = Read("FFPROBE_PATH"),
            TorrentEnablePortMapping = ReadBool("TORRENT_ENABLE_PORT_MAPPING", true),
            TorrentBindAddress = Read("TORRENT_BIND_ADDRESS"),
            TorrentMaxDownloadSpeed = ReadInt("TORRENT_MAX_DOWNLOAD_SPEED", 0),
            TorrentMaxUploadSpeed = ReadInt("TORRENT_MAX_UPLOAD_SPEED", 0),
            TorrentPort = int.TryParse(Read("HOSTY_PORT_TORRENT"), out var port) ? port : null,
            CatalogMountRoots = ParseMountRoots(Read("HOSTY_MOUNT_CATALOGROOTS")),
        };
    }

    /// <summary>
    /// Parses the catalog-root mount injection. Core joins multiple mount paths with a comma into
    /// <c>HOSTY_MOUNT_{KEY}</c> and rejects mount host paths containing a comma (or ':'), so comma is
    /// the canonical separator here — splitting on it is safe and required for multi-mount setups.
    /// Newline, ';', and <c>os.PathSeparator</c> are also accepted defensively, as are optional
    /// <c>label=path</c> pairs.
    /// </summary>
    internal static IReadOnlyList<string> ParseMountRoots(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var separators = new[] { ',', '\n', '\r', ';', Path.PathSeparator };
        return raw
            .Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry =>
            {
                var equals = entry.IndexOf('=');
                return equals >= 0 ? entry[(equals + 1)..].Trim() : entry;
            })
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
