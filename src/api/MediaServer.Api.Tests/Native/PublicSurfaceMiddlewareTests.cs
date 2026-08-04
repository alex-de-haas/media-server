using MediaServer.Api.Hosty;
using MediaServer.Api.Native;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Api.Tests.Native;

/// <summary>
/// The allowlist middleware driven through a real pipeline, not just its predicate: this exercises the
/// metadata lookup, the port comparison and the short-circuit together.
/// </summary>
/// <remarks>
/// Built as a bare <see cref="ApplicationBuilder"/> rather than through a test host on purpose. A
/// <c>TestServer</c> has no real ports — <c>Connection.LocalPort</c> is 0 there — so the one thing
/// this check is about could not be expressed against it.
/// </remarks>
public sealed class PublicSurfaceMiddlewareTests
{
    private const int PublicPort = 8096;
    private const int InternalPort = 8080;

    private static HostyOptions Hosty(bool container = true) => new()
    {
        AppId = "com.haas.media-server",
        CoreOrigin = "http://core",
        AppDataDir = "/data",
        InternalPort = InternalPort,
        JellyfinPort = PublicPort,
        RunningInContainer = container,
    };

    private static async Task<(int Status, bool Reached)> RunAsync(
        HostyOptions hosty, int localPort, bool published)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var reached = false;

        var pipeline = new ApplicationBuilder(services)
            .UsePublicSurfaceAllowlist(hosty)
            .Use(_ => context =>
            {
                reached = true;
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            })
            .Build();

        var context = new DefaultHttpContext { RequestServices = services };
        context.Connection.LocalPort = localPort;

        var metadata = published
            ? new EndpointMetadataCollection(new PublicSurfaceAttribute())
            : EndpointMetadataCollection.Empty;
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, metadata, "test"));

        await pipeline(context);
        return (context.Response.StatusCode, reached);
    }

    [Fact]
    public async Task An_unpublished_endpoint_is_refused_on_the_public_binding()
    {
        var (status, reached) = await RunAsync(Hosty(), PublicPort, published: false);

        // 404 rather than 401: an unauthenticated caller has no business learning that an
        // administration surface exists here.
        Assert.Equal(StatusCodes.Status404NotFound, status);
        Assert.False(reached);
    }

    [Fact]
    public async Task A_published_endpoint_is_served_on_the_public_binding()
    {
        var (status, reached) = await RunAsync(Hosty(), PublicPort, published: true);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.True(reached);
    }

    [Fact]
    public async Task The_internal_binding_keeps_serving_unpublished_endpoints()
    {
        // The operator surfaces are unpublished by design and must keep working where the web BFF
        // reaches them.
        var (_, reached) = await RunAsync(Hosty(), InternalPort, published: false);

        Assert.True(reached);
    }

    [Fact]
    public async Task Nothing_is_gated_when_there_is_no_public_binding()
    {
        var hosty = new HostyOptions
        {
            AppId = "com.haas.media-server",
            CoreOrigin = "http://core",
            AppDataDir = "/data",
            InternalPort = InternalPort,
            JellyfinPort = null,
            RunningInContainer = false,
        };

        var (_, reached) = await RunAsync(hosty, InternalPort, published: false);

        Assert.True(reached);
    }

    [Fact]
    public async Task A_request_on_an_unrelated_port_is_left_alone()
    {
        // Only the public binding is gated; a request that arrived anywhere else is not this check's
        // business.
        var (_, reached) = await RunAsync(Hosty(), 5000, published: false);

        Assert.True(reached);
    }
}
