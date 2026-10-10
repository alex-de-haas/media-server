using System.Text.Json.Nodes;

namespace MediaServer.Api.Mcp;

/// <summary>One fail-closed classification for discovery and direct invocation.</summary>
internal static class McpToolAccess
{
    internal static bool IsReadOnly(JsonNode? tool)
        => tool is JsonObject definition && definition["annotations"] is JsonObject annotations
            && annotations["readOnlyHint"] is JsonValue value
            && value.TryGetValue<bool>(out var readOnly) && readOnly;

    internal static JsonArray VisibleTools(McpCaller caller)
        => new(McpToolInvoker.Tools()
            .Where(tool => caller.CanRead && (caller.CanMutate || IsReadOnly(tool)))
            .Select(tool => tool!.DeepClone()).ToArray());

    internal static bool AllowsInvocation(McpCaller caller, string? name)
        => caller.CanRead && (caller.CanMutate || McpToolInvoker.Tools()
            .Any(tool => McpProtocol.Str(tool, "name") == name && IsReadOnly(tool)));
}
