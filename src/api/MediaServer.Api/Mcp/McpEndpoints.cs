using System.Text.Json.Nodes;
using System.Security.Claims;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.Library;
using MediaServer.Api.Pipeline;
using Microsoft.EntityFrameworkCore;
using static MediaServer.Api.Mcp.McpProtocol;

namespace MediaServer.Api.Mcp;

/// <summary>
/// The app-owned MCP surface: this server's use cases as tools an agent can call.
/// </summary>
/// <remarks>
/// <para>
/// Authenticated by the same scheme as the rest of <c>/api</c>, deliberately. Core answers "who is
/// this" and this app answers "what may they do" — an MCP endpoint with an identity system of its own
/// would be a second answer to the first question, and the one more likely to be wrong.
/// </para>
/// <para>
/// The tools are shaped by the question an operator asked, not by the route that answers it: there are
/// about eighty routes and a tool per route would be a worse interface than none. See
/// <c>docs/features/mcp-tools/plan.md</c>.
/// </para>
/// </remarks>
public static class McpEndpoints
{
    public static void MapMcpEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/mcp", async (
            JsonNode? body,
            ClaimsPrincipal principal,
            McpToolInvoker invoker,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            var id = body?["id"]?.DeepClone();
            var method = body?["method"]?.GetValue<string>();

            switch (method)
            {
                case "initialize":
                    return Result(id, new JsonObject
                    {
                        ["protocolVersion"] = ProtocolVersion,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject
                        {
                            ["name"] = Environment.GetEnvironmentVariable("HOSTY_APP_ID") ?? "com.haas.media-server",
                            ["version"] = Environment.GetEnvironmentVariable("HOSTY_APP_VERSION") ?? "0",
                        },
                        ["instructions"] =
                            "This host's media library, its download pipeline, and what it is waiting for. "
                            + "Every list says the window that produced it: a result is only complete when "
                            + "its window says so. An empty result says which kind of nothing it is where "
                            + "that is knowable — 'nothing matched' and 'nothing has been scanned' are "
                            + "different answers and only one is about the library.",
                    });

                // A notification carries no id and must not be answered.
                case "notifications/initialized":
                    return Results.Ok();

                case "tools/list":
                    return Result(id, new JsonObject { ["tools"] = McpToolInvoker.Tools() });

                case "tools/call":
                    var appUserId = await principal.ResolveAppUserIdAsync(database, cancellationToken);
                    return await invoker.CallAsync(id, body?["params"], appUserId, cancellationToken);

                default:
                    return Error(id, -32601, $"Method not found: {method}");
            }
        }).RequireAuthorization();
    }
}
