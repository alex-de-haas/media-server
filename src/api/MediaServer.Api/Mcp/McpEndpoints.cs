using System.Text.Json.Nodes;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.Library;
using MediaServer.Api.Pipeline;
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
    /// <summary>
    /// Where the surface lives — referenced by the pipeline, which skips the default authentication
    /// for it, so the route and that exclusion cannot drift apart.
    /// </summary>
    public const string Path = "/api/mcp";

    public static void MapMcpEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(Path, async (
            JsonNode? body,
            HttpRequest request,
            McpToolInvoker invoker,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            // A delegated token, not this app's session. The two are different credentials and the
            // difference is not cosmetic: an agent calling on an operator's behalf holds a short-TTL
            // token Core signed for *this* app, while the identity scheme in front of every other route
            // revalidates an app identity token — which Core rejects outright for a delegated one,
            // because the type is inside the signed input. Authenticating this route the ordinary way
            // refused every agent call with a 401 while browser traffic kept working.
            var caller = await McpCallerIdentity.ResolveAsync(
                request.Headers.Authorization, database, cancellationToken);
            if (caller is null)
            {
                return Results.Json(
                    new { error = "unauthorized", message = "A Hosty delegated token is required." },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var id = body?["id"]?.DeepClone();
            // Read through the same helper the tool arguments use. `GetValue<string>()` throws when the
            // member is missing or is not a string, which turns malformed client input into a 500 where
            // the protocol asks for a JSON-RPC error.
            var method = McpProtocol.Str(body, "method");

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
                    // Core said who this is; the app decides what they may do. The maintenance tools
                    // have admin-only HTTP twins, and calling their services in-process would otherwise
                    // walk around that check entirely.
                    //
                    return await invoker.CallAsync(
                        id, body?["params"], caller.AppUserId, caller.IsAdministrator, cancellationToken);

                default:
                    return Error(id, -32601, $"Method not found: {method}");
            }
        });
    }
}
