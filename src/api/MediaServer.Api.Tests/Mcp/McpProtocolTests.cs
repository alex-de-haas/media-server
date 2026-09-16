using MediaServer.Api.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Api.Tests.Mcp;

/// <summary>
/// The transport responses a client reads before any tool is involved.
/// </summary>
/// <remarks>
/// Asserted on the executed HTTP response rather than the result object: the defect was an empty 200,
/// which is a perfectly ordinary <see cref="IResult"/> and only wrong once it is on the wire.
/// </remarks>
public sealed class McpProtocolTests
{
    [Fact]
    public async Task A_notification_is_acknowledged_with_202_and_no_body()
    {
        // Codex's client accepts only 202 here. The empty 200 this route used to answer passed every
        // lenient client and dropped the whole server from a Codex session during the handshake.
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = body;

        await McpProtocol.Accepted().ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status202Accepted, context.Response.StatusCode);
        Assert.Equal(0, body.Length);
        Assert.Null(context.Response.ContentType);
    }
}
