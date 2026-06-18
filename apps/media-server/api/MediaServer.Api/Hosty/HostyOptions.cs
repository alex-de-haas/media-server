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

    /// <summary>Primary app data directory; the SQLite DB and caches live under it.</summary>
    public required string AppDataDir { get; init; }

    /// <summary>Loopback port Core assigned to the internal management surface (dev profile).</summary>
    public int? InternalPort { get; init; }

    /// <summary>Loopback port Core assigned to the public Jellyfin surface (dev profile).</summary>
    public int? JellyfinPort { get; init; }

    /// <summary>True when running inside a container (docker profile); set by the .NET base image.</summary>
    public bool RunningInContainer { get; init; }

    /// <summary>True only when Core has provisioned a service token, i.e. we run under Core.</summary>
    public bool IsCoreManaged => !string.IsNullOrWhiteSpace(ServiceToken);

    /// <summary>
    /// Server URL to enter in a Jellyfin client (e.g. Infuse). Prefers the public ingress origin;
    /// falls back to the local loopback Jellyfin surface (dev profile) so same-machine clients have
    /// a usable URL even without ingress. Null only when neither is available.
    /// </summary>
    public string? JellyfinServerUrl =>
        JellyfinPublicOrigin ?? (JellyfinPort is { } port ? $"http://localhost:{port}" : null);

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
            InternalPort = ReadPort("HOSTY_PORT_INTERNAL"),
            JellyfinPort = ReadPort("HOSTY_PORT_JELLYFIN"),
            RunningInContainer = string.Equals(Read("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase),
        };
    }
}
