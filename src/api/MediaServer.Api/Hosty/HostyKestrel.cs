namespace MediaServer.Api.Hosty;

public static class HostyKestrel
{
    /// <summary>
    /// Binds Kestrel to the ports the runtime profile dictates:
    /// <list type="bullet">
    /// <item><b>dev (localCommand):</b> Core assigns loopback ports and injects
    /// <c>HOSTY_PORT_INTERNAL</c> / <c>HOSTY_PORT_JELLYFIN</c>; the app listens on exactly those.</item>
    /// <item><b>docker:</b> the container listens on the fixed <c>containerPort</c>s and Core
    /// publishes <c>hostPort:containerPort</c>. <c>HOSTY_PORT_*</c> there is the host port, not the
    /// bind port, so we ignore it and let the image's <c>ASPNETCORE_URLS</c> drive Kestrel.</item>
    /// </list>
    /// </summary>
    public static void ConfigureUrls(IWebHostBuilder webHost, HostyOptions hosty)
    {
        if (hosty.RunningInContainer)
        {
            // The docker image sets ASPNETCORE_URLS (M4); leave Kestrel's default handling alone.
            return;
        }

        var urls = new List<string>(2);
        if (hosty.InternalPort is { } internalPort)
        {
            urls.Add($"http://localhost:{internalPort}");
        }

        if (hosty.JellyfinPort is { } jellyfinPort)
        {
            urls.Add($"http://localhost:{jellyfinPort}");
        }

        if (urls.Count > 0)
        {
            webHost.UseUrls([.. urls]);
        }
    }
}
