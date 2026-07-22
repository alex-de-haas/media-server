namespace MediaServer.Api.Configuration;

/// <summary>
/// Application settings injected by Hosty from the manifest <c>settings</c> block (as environment
/// variables with the declared keys) plus the catalog-root mounts. Secrets and global toggles come
/// from here, never the database (see <c>docs/features/domain-model.md</c>).
/// </summary>
public sealed class MediaServerSettings
{
    /// <summary>TMDb API key (manifest <c>TMDB_API_KEY</c>, secret).</summary>
    public string? TmdbApiKey { get; init; }

    /// <summary>Ordered supported languages; the first entry is the fallback (e.g. <c>en-US</c>).</summary>
    public IReadOnlyList<string> SupportedLanguages { get; init; } = ["en-US"];

    /// <summary>
    /// Watch region for release-date tracking (manifest <c>WATCH_REGION</c>, ISO-3166-1, default <c>US</c>).
    /// Its own axis, independent of <see cref="SupportedLanguages"/> (a metadata-language axis, not a
    /// country); a per-entry <c>WatchlistEntry.RegionOverride</c> refines it. Only release-<b>date</b>
    /// tracking uses this — certification stays region-by-language.
    /// </summary>
    public string WatchRegion { get; init; } = "US";

    public string JellyfinServerName { get; init; } = "Media Server";

    public bool JellyfinDiscoveryEnabled { get; init; }

    /// <summary>
    /// Records one structured line per playback-state request to <c>logs/playback-diagnostics.log</c>
    /// (manifest <c>PLAYBACK_DIAGNOSTICS</c>, default off). An operator toggle rather than an
    /// environment check, so the observation can be captured under the <c>docker</c> runtime the app
    /// actually ships with. Also turns on the route-level <c>requests.log</c>. Off by default: it is a
    /// diagnostic for a specific investigation, not everyday logging. See
    /// <see cref="MediaServer.Api.Jellyfin.PlaybackDiagnostics"/>.
    /// </summary>
    public bool PlaybackDiagnosticsEnabled { get; init; }

    /// <summary>Override path to the <c>ffprobe</c> binary; falls back to PATH lookup when null.</summary>
    public string? FfprobePath { get; init; }

    /// <summary>Override path to the <c>ffmpeg</c> binary (external-audio muxing); falls back to PATH lookup when null.</summary>
    public string? FfmpegPath { get; init; }

    /// <summary>Per-download rate limits forwarded to the torrent-engine; bytes/sec, 0 means unlimited.</summary>
    public int TorrentMaxDownloadSpeed { get; init; }

    public int TorrentMaxUploadSpeed { get; init; }

    /// <summary>
    /// Base URL of the external <c>torrent-engine</c> app, injected as the cross-app dependency
    /// <c>HOSTY_DEPENDENCY_TORRENT_ENGINE_URL</c>. When set, downloading is delegated to that app over
    /// HTTP/SSE; when null, downloading is disabled (see <see cref="MediaServer.Api.Torrents.DisabledTorrentEngine"/>). The
    /// engine is a required dependency — see <c>docs/ideas/torrent-engine-app.md</c>.
    /// </summary>
    public string? TorrentEngineUrl { get; init; }

    /// <summary>
    /// Base URL of the external <c>transcode-engine</c> app, injected as the cross-app dependency
    /// <c>HOSTY_DEPENDENCY_TRANSCODE_ENGINE_URL</c>. When set, transcoding is delegated to that app over
    /// HTTP/SSE; when null, transcoding is disabled (see
    /// <see cref="MediaServer.Api.Transcoding.DisabledTranscodeEngine"/>). The engine is an optional
    /// dependency — see <c>docs/ideas/transcode-engine-app.md</c>.
    /// </summary>
    public string? TranscodeEngineUrl { get; init; }

    /// <summary>
    /// Catalog-root mounts from <c>HOSTY_MOUNT_CATALOGROOTS</c>, each carrying its operator-chosen
    /// <see cref="CatalogMount.Label"/> and host/container <see cref="CatalogMount.Path"/>. Each
    /// operator-configured catalog root must live within one of these (when provided). May be empty under
    /// standalone local runs. The label is the only key shared with the torrent-engine's downloads mounts
    /// (Hosty configures each app's mounts independently), so it is what we send the engine to pick the
    /// matching download root — see <see cref="MediaServer.Api.Torrents.RemoteTorrentEngine"/>.
    /// </summary>
    public IReadOnlyList<CatalogMount> CatalogMountRoots { get; init; } = [];

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

        // A watch region must be a bare ISO-3166-1 alpha-2 code; anything else falls back to US rather
        // than sending TMDb a region it will silently ignore.
        var watchRegion = Read("WATCH_REGION")?.ToUpperInvariant();
        if (watchRegion is not { Length: 2 } || !watchRegion.All(char.IsAsciiLetterUpper))
        {
            watchRegion = "US";
        }

        return new MediaServerSettings
        {
            TmdbApiKey = Read("TMDB_API_KEY"),
            SupportedLanguages = languages.Length > 0 ? languages : ["en-US"],
            WatchRegion = watchRegion,
            JellyfinServerName = Read("JELLYFIN_SERVER_NAME") ?? "Media Server",
            JellyfinDiscoveryEnabled = ReadBool("JELLYFIN_DISCOVERY_ENABLED", false),
            PlaybackDiagnosticsEnabled = ReadBool("PLAYBACK_DIAGNOSTICS", false),
            FfprobePath = Read("FFPROBE_PATH"),
            FfmpegPath = Read("FFMPEG_PATH"),
            TorrentMaxDownloadSpeed = ReadInt("TORRENT_MAX_DOWNLOAD_SPEED", 0),
            TorrentMaxUploadSpeed = ReadInt("TORRENT_MAX_UPLOAD_SPEED", 0),
            TorrentEngineUrl = Read("HOSTY_DEPENDENCY_TORRENT_ENGINE_URL"),
            TranscodeEngineUrl = Read("HOSTY_DEPENDENCY_TRANSCODE_ENGINE_URL"),
            CatalogMountRoots = ParseMountRoots(Read("HOSTY_MOUNT_CATALOGROOTS")),
        };
    }

    /// <summary>
    /// Parses the catalog-root mount injection. Core joins the mounts with a comma into
    /// <c>HOSTY_MOUNT_{KEY}</c> as <c>label=path</c> entries (and rejects mount host paths containing a
    /// comma or ':'), so comma is the canonical separator and each entry's first <c>'='</c> splits its
    /// label from its path. A host path may itself contain <c>'='</c>, so we split on the <b>first</b> one
    /// only; an entry with no <c>'='</c> (an older Core that injected bare paths) or a blank explicit label
    /// falls back to the path's base name as its label, and an entry with no usable label is skipped (Core
    /// never injects an empty label). Newline, ';', and <c>os.PathSeparator</c> are also accepted
    /// defensively, and duplicate paths are dropped (first label wins).
    /// </summary>
    internal static IReadOnlyList<CatalogMount> ParseMountRoots(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var separators = new[] { ',', '\n', '\r', ';', Path.PathSeparator };
        // On Windows paths are case-insensitive, so dedupe with an OS-appropriate comparer to avoid keeping
        // the same mount twice under different casing.
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        var mounts = new List<CatalogMount>();
        foreach (var entry in raw.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = entry.IndexOf('=');
            var path = (equals >= 0 ? entry[(equals + 1)..] : entry).Trim();
            if (path.Length == 0 || !seen.Add(path))
            {
                continue;
            }

            var label = equals >= 0 ? entry[..equals].Trim() : string.Empty;
            if (label.Length == 0)
            {
                label = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }

            if (label.Length == 0)
            {
                continue;
            }

            mounts.Add(new CatalogMount(label, path));
        }

        return mounts;
    }
}

/// <summary>A catalog-root mount: its Hosty <paramref name="Label"/> (the per-bind key shared with the
/// torrent-engine's downloads mounts) and host/container <paramref name="Path"/>.</summary>
public sealed record CatalogMount(string Label, string Path);
