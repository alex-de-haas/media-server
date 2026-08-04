using MediaServer.Api.Hosty;
using MediaServer.Api.Native;

namespace MediaServer.Api.Tests.Native;

/// <summary>
/// The public binding shares one route table with the internal one, so the allowlist is the only thing
/// standing between catalog administration and the internet. These pin the decision itself.
/// </summary>
public sealed class PublicSurfaceTests
{
    private const int Public = 8096;
    private const int Internal = 8080;

    [Fact]
    public void Refuses_an_unpublished_endpoint_on_the_public_binding()
    {
        Assert.True(PublicSurface.ShouldRefuse(Public, Public, Internal, published: false));
    }

    [Fact]
    public void Serves_a_published_endpoint_on_the_public_binding()
    {
        Assert.False(PublicSurface.ShouldRefuse(Public, Public, Internal, published: true));
    }

    [Fact]
    public void Leaves_the_internal_binding_alone()
    {
        // The operator surfaces are unpublished by design and must keep working where the web BFF
        // reaches them.
        Assert.False(PublicSurface.ShouldRefuse(Internal, Public, Internal, published: false));
    }

    [Fact]
    public void Does_not_gate_when_both_surfaces_share_one_port()
    {
        // Splitting them would be a guess rather than a decision, and guessing here would silently
        // 404 the internal surface.
        Assert.False(PublicSurface.ShouldRefuse(Public, Public, Public, published: false));
    }

    [Fact]
    public void Container_bind_ports_come_from_the_image_not_from_the_injected_host_ports()
    {
        // Under docker, HOSTY_PORT_* is the published host port; Kestrel listens on the container
        // ports. Matching a request against the host port would gate nothing at all.
        var hosty = new HostyOptions
        {
            AppId = "com.haas.media-server",
            CoreOrigin = "http://core",
            AppDataDir = "/data",
            InternalPort = 41001,
            JellyfinPort = 41002,
            RunningInContainer = true,
        };

        Assert.Equal(8096, hosty.PublicBindPort);
        Assert.Equal(8080, hosty.InternalBindPort);
    }

    [Fact]
    public void Dev_bind_ports_are_the_loopback_ports_core_assigned()
    {
        var hosty = new HostyOptions
        {
            AppId = "com.haas.media-server",
            CoreOrigin = "http://core",
            AppDataDir = "/data",
            InternalPort = 41001,
            JellyfinPort = 41002,
            RunningInContainer = false,
        };

        Assert.Equal(41002, hosty.PublicBindPort);
        Assert.Equal(41001, hosty.InternalBindPort);
    }
}
