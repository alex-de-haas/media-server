namespace MediaServer.Api.Hosty;

/// <summary>
/// Strongly-typed view over the <c>HOSTY_*</c> runtime environment that Hosty Core injects
/// into the <c>api</c> service. The app must never hard-code ports, origins, or paths —
/// everything is resolved from here once at startup.
/// </summary>
public sealed class HostyOptions
{
    /// <summary>The app's stable reverse-DNS id (token audience for identity validation).</summary>
    public required string AppId { get; init; }

    public string? ServiceKey { get; init; }

    /// <summary>Service token used as the bearer when calling Core's internal/app APIs.</summary>
    public string? ServiceToken { get; init; }

    /// <summary>Process-to-Core origin (e.g. <c>http://host.docker.internal:3001</c>).</summary>
    public required string CoreOrigin { get; init; }

    /// <summary>Browser-facing Core origin; only relevant to the web service, kept for parity.</summary>
    public string? CorePublicOrigin { get; init; }

    /// <summary>
    /// Public origin for the Jellyfin endpoint (cloudflared), injected as
    /// <c>HOSTY_PUBLIC_ORIGIN_JELLYFIN</c>. Surfaced to the UI as the server URL to enter in Infuse;
    /// null under standalone local runs without ingress.
    /// </summary>
    public string? JellyfinPublicOrigin { get; init; }

    /// <summary>Primary app data directory; the SQLite DB lives under it and Hosty backs it up.</summary>
    public required string AppDataDir { get; init; }

    /// <summary>
    /// App cache directory for derived, rebuildable data — remux indexes — which Hosty never backs
    /// up. Equals the data directory when Core injected no cache path (a Core predating the cache
    /// contract, or a standalone run), which keeps the pre-cache layout: everything under data,
    /// everything backed up.
    /// </summary>
    public string AppCacheDir => AppCacheDirOverride ?? AppDataDir;

    /// <summary>Raw <c>HOSTY_APP_CACHE_DIR</c> value; see <see cref="AppCacheDir"/>.</summary>
    public string? AppCacheDirOverride { get; init; }

    /// <summary>Loopback port Core assigned to the internal management surface (dev profile).</summary>
    public int? InternalPort { get; init; }

    /// <summary>Loopback port Core assigned to the public Jellyfin surface (dev profile).</summary>
    public int? JellyfinPort { get; init; }

    /// <summary>True when running inside a container (docker profile); set by the .NET base image.</summary>
    public bool RunningInContainer { get; init; }

    /// <summary>
    /// Container bind ports, fixed by the manifest's <c>containerPort</c>s and the image's
    /// <c>ASPNETCORE_URLS</c>. Under <c>docker</c>, <c>HOSTY_PORT_*</c> is the published <em>host</em>
    /// port, not what Kestrel listens on, so the bind port cannot be read from the environment there.
    /// </summary>
    private const int ContainerInternalPort = 8080;
    private const int ContainerJellyfinPort = 8096;

    /// <summary>
    /// The port Kestrel actually listens on for the public surface, which is what a request must be
    /// matched against to know whether it arrived from outside. Null when no public surface is bound.
    /// </summary>
    public int? PublicBindPort => RunningInContainer ? ContainerJellyfinPort : JellyfinPort;

    /// <summary>The port Kestrel listens on for the internal surface. Null when it is not bound.</summary>
    public int? InternalBindPort => RunningInContainer ? ContainerInternalPort : InternalPort;

    /// <summary>True only when Core has provisioned a service token, i.e. we run under Core.</summary>
    public bool IsCoreManaged => !string.IsNullOrWhiteSpace(ServiceToken);

    /// <summary>
    /// Server URL to enter in a Jellyfin client (e.g. Infuse). Prefers the public ingress origin;
    /// for non-container (dev localCommand) runs without ingress, falls back to the local loopback
    /// Jellyfin surface so same-machine clients have a usable URL. Null otherwise — including
    /// container runs without a public origin, where <c>HOSTY_PORT_JELLYFIN</c> is the published
    /// host port and <c>localhost</c> would be misleading for non-host clients.
    /// </summary>
    public string? JellyfinServerUrl =>
        !string.IsNullOrWhiteSpace(JellyfinPublicOrigin)
            ? JellyfinPublicOrigin
            : (!RunningInContainer && JellyfinPort is { } port ? $"http://localhost:{port}" : null);

    public string DatabasePath => Path.Combine(AppDataDir, "media-server.db");

    public static HostyOptions FromConfiguration(IConfiguration configuration, string contentRoot)
    {
        string? Read(string key) => configuration[key] is { Length: > 0 } value ? value : null;
        int? ReadPort(string key) => int.TryParse(Read(key), out var port) ? port : null;

        return new HostyOptions
        {
            // Fall back to sane defaults so the app still boots for standalone local runs
            // (outside Core); identity validation simply stays disabled without a service token.
            AppId = Read("HOSTY_APP_ID") ?? "com.haas.media-server",
            ServiceKey = Read("HOSTY_APP_SERVICE_KEY"),
            ServiceToken = Read("HOSTY_APP_SERVICE_TOKEN"),
            CoreOrigin = Read("HOSTY_CORE_ORIGIN") ?? "http://localhost:3001",
            CorePublicOrigin = Read("HOSTY_CORE_PUBLIC_ORIGIN"),
            JellyfinPublicOrigin = Read("HOSTY_PUBLIC_ORIGIN_JELLYFIN"),
            AppDataDir = Read("HOSTY_APP_DATA_DIR") ?? Path.Combine(contentRoot, "data"),
            AppCacheDirOverride = Read("HOSTY_APP_CACHE_DIR"),
            InternalPort = ReadPort("HOSTY_PORT_INTERNAL"),
            JellyfinPort = ReadPort("HOSTY_PORT_JELLYFIN"),
            RunningInContainer = string.Equals(Read("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase),
        };
    }
}
