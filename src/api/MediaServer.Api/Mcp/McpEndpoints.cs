using HostySdk.App;
using System.Text.Json.Nodes;
using MediaServer.Api.Data;
using static MediaServer.Api.Mcp.McpProtocol;

namespace MediaServer.Api.Mcp;

/// <summary>
/// The app-owned MCP surface: this server's use cases as tools an agent can call.
/// </summary>
/// <remarks>
/// Core identifies the actor; this app enforces credential scopes and its own user permissions.
/// See <c>docs/features/mcp-tools/feature.md</c> for the tool contract.
/// </remarks>
public static class McpEndpoints
{
    /// <summary>
    /// Where the surface lives — referenced by the pipeline, which skips the default authentication
    /// for it, so the route and that exclusion cannot drift apart.
    /// </summary>
    public const string Path = "/api/mcp";

    public static void MapMcpEndpoints(this IEndpointRouteBuilder routes)
        => routes.MapPost(Path, HandleAsync);

    internal static async Task<IResult> HandleAsync(
        JsonNode? body,
        HttpRequest request,
        McpToolInvoker invoker,
        MediaServerDbContext database,
        HostyScopedTokenClient scopedTokens,
        CancellationToken cancellationToken)
    {
        request.HttpContext.Response.Headers.CacheControl = "no-store";
        // Introspect protocol traffic too: a previously valid credential may have been revoked.
        var method = Str(body, "method");
        var tool = method == "tools/call" ? Str(body?["params"], "name") : null;
        McpCaller? caller;
        try
        {
            caller = await McpCallerIdentity.ResolveAsync(
                request.Headers.Authorization, database, scopedTokens, cancellationToken, tool);
        }
        catch (HostyScopedTokenException)
        {
            return Results.Json(
                new { error = "introspection_unavailable", message = "Core could not validate the MCP credential. Retry later." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (caller is null)
        {
            return Results.Json(
                new { error = "unauthorized", message = "A valid Hosty MCP credential is required." },
                statusCode: StatusCodes.Status401Unauthorized);
        }
        if (!caller.CanRead)
        {
            return Results.Json(
                new { error = "insufficient_scope", message = "The MCP credential requires mcp:read." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var id = body?["id"]?.DeepClone();

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

            // A notification carries no id and must not be answered — only acknowledged.
            case "notifications/initialized":
                return Accepted();

            case "tools/list":
                return Result(id, new JsonObject { ["tools"] = McpToolAccess.VisibleTools(caller) });

            case "tools/call":
                if (!McpToolAccess.AllowsInvocation(caller, tool))
                {
                    return Failure(id, "This credential grants read-only MCP access; changing server or personal state requires an authorized assistant.");
                }

                return await invoker.CallAsync(
                    id, body?["params"], caller.AppUserId, caller.IsAdministrator, cancellationToken);

            default:
                return Error(id, -32601, $"Method not found: {method}");
        }
    }
}
