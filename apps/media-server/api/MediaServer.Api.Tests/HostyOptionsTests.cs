using MediaServer.Api.Hosty;
using Microsoft.Extensions.Configuration;

namespace MediaServer.Api.Tests;

public sealed class HostyOptionsTests
{
    private static HostyOptions Build(Dictionary<string, string?> environment) =>
        HostyOptions.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(environment).Build(),
            contentRoot: "/srv/app");

    [Fact]
    public void Reads_injected_hosty_environment()
    {
        var options = Build(new Dictionary<string, string?>
        {
            ["HOSTY_APP_ID"] = "com.haas.media-server",
            ["HOSTY_APP_SERVICE_KEY"] = "api",
            ["HOSTY_APP_SERVICE_TOKEN"] = "hosty_app_service.1.abc.def",
            ["HOSTY_CORE_ORIGIN"] = "http://host.docker.internal:3001",
            ["HOSTY_APP_DATA_DIR"] = "/data/app",
            ["HOSTY_PORT_INTERNAL"] = "41001",
            ["HOSTY_PORT_JELLYFIN"] = "41002",
        });

        Assert.Equal("com.haas.media-server", options.AppId);
        Assert.Equal("api", options.ServiceKey);
        Assert.Equal("http://host.docker.internal:3001", options.CoreOrigin);
        Assert.Equal("/data/app", options.AppDataDir);
        Assert.Equal(41001, options.InternalPort);
        Assert.Equal(41002, options.JellyfinPort);
        Assert.True(options.IsCoreManaged);
        Assert.False(options.RunningInContainer);
        Assert.Equal(Path.Combine("/data/app", "media-server.db"), options.DatabasePath);
    }

    [Fact]
    public void Falls_back_to_defaults_when_run_standalone()
    {
        var options = Build(new Dictionary<string, string?>());

        Assert.Equal("com.haas.media-server", options.AppId);
        Assert.Equal("http://localhost:3001", options.CoreOrigin);
        Assert.Equal(Path.Combine("/srv/app", "data"), options.AppDataDir);
        Assert.Null(options.InternalPort);
        Assert.Null(options.JellyfinPort);
        Assert.False(options.IsCoreManaged);
    }

    [Fact]
    public void Detects_container_runtime()
    {
        var options = Build(new Dictionary<string, string?>
        {
            ["DOTNET_RUNNING_IN_CONTAINER"] = "true",
        });

        Assert.True(options.RunningInContainer);
    }
}
