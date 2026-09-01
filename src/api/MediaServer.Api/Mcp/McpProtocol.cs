using System.Text.Json.Nodes;

namespace MediaServer.Api.Mcp;

/// <summary>
/// The JSON-RPC shapes an MCP client expects, and the two contracts every tool here answers under.
/// </summary>
/// <remarks>
/// Hand-rolled rather than taken from an SDK, following the reference app: the surface is three
/// methods, and keeping it inline leaves the parts specific to this server — the window, the note, the
/// per-tool argument handling — as the visible content of the file.
/// </remarks>
internal static class McpProtocol
{
    public const string ProtocolVersion = "2025-06-18";

    /// <summary>
    /// Stamps a list result with the window that produced it.
    /// </summary>
    /// <remarks>
    /// The reason a "no" from this server can be trusted. A library or a pipeline is larger than any
    /// answer, so a result that does not say what it was cut to lets "there are no failures" mean "none
    /// among the rows I was handed" — a false statement about the host rather than a report about the
    /// query. <paramref name="total"/> is counted before the window, which is what tells a full page
    /// from a complete answer.
    /// </remarks>
    public static JsonObject WithWindow(JsonObject payload, int returned, int total, int limit, int offset)
    {
        payload["window"] = new JsonObject
        {
            ["limit"] = limit,
            ["offset"] = offset,
            ["returned"] = returned,
            ["total"] = total,
            // Exact rather than inferred from a full page: these queries count before they cut, so the
            // number left behind is known and does not have to be guessed at.
            ["truncated"] = offset + returned < total,
        };
        return payload;
    }

    /// <summary>Attaches a note saying which kind of nothing an empty result is.</summary>
    /// <remarks>
    /// Absence is the answer most easily misread. "Nothing matched" and "nothing has been looked at"
    /// are different facts, and only the first is about the library — so where the difference is
    /// knowable, the result says it rather than leaving an empty array to be interpreted.
    /// </remarks>
    public static JsonObject WithNote(JsonObject payload, string? note)
    {
        if (!string.IsNullOrEmpty(note))
        {
            payload["note"] = note;
        }

        return payload;
    }

    public static JsonObject Tool(string name, string description, JsonObject properties, params string[] required)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
        if (required.Length > 0)
        {
            schema["required"] = new JsonArray([.. required.Select(value => (JsonNode)value!)]);
        }

        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = schema,
            // Declared, never assumed: the Hosty connector's filter is fail-closed, so a tool with no
            // annotation is treated as possibly mutating and is not exported at all. An unannotated
            // surface is not a permissive one — it is an invisible one.
            ["annotations"] = new JsonObject
            {
                ["readOnlyHint"] = true,
                ["destructiveHint"] = false,
                ["idempotentHint"] = true,
            },
        };
    }

    public static JsonObject Prop(string type, string description)
        => new() { ["type"] = type, ["description"] = description };

    public static IResult Result(JsonNode? id, JsonNode payload) =>
        Results.Json(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = payload });

    /// <summary>A tool that failed, reported as a result the model can read and recover from.</summary>
    /// <remarks>
    /// Not a JSON-RPC error: that is a protocol fault and ends the turn, where a tool refusing an
    /// argument is something the model can correct and retry. Conflating the two teaches a client the
    /// wrong recovery.
    /// </remarks>
    public static IResult Failure(JsonNode? id, string message) =>
        Result(id, new JsonObject
        {
            ["isError"] = true,
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }),
        });

    public static IResult Content(JsonNode? id, JsonObject payload) =>
        Result(id, new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = payload.ToJsonString(),
            }),
        });

    public static IResult Error(JsonNode? id, int code, string message) =>
        Results.Json(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        });

    // Read through JsonValue.TryGetValue rather than GetValue<JsonElement>: the latter only works for a
    // node that came from parsing, and throws for one built in code. Over the wire every argument is
    // parsed, so the difference never shows in production — it showed the first time a test constructed
    // arguments directly, and a helper that works only when exercised the usual way is a trap.
    public static string? Str(JsonNode? arguments, string name)
        => arguments?[name] is JsonValue value && value.TryGetValue<string>(out var parsed) ? parsed : null;

    public static int? Int(JsonNode? arguments, string name)
        => arguments?[name] is JsonValue value && value.TryGetValue<int>(out var parsed) ? parsed : null;

    public static bool? Bool(JsonNode? arguments, string name)
        => arguments?[name] is JsonValue value && value.TryGetValue<bool>(out var parsed) ? parsed : null;

    public static Guid? Id(JsonNode? arguments, string name)
        => Guid.TryParse(Str(arguments, name), out var value) ? value : null;

    /// <summary>Comma-separated values, blanks dropped.</summary>
    public static IReadOnlyList<string>? Csv(JsonNode? arguments, string name)
    {
        var raw = Str(arguments, name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 0 ? parts : null;
    }
}

/// <summary>
/// A refusal the caller should read as-is, rather than as a malformed argument.
/// </summary>
/// <remarks>
/// "This call carried no Hosty user" and "that status does not exist" are both refusals, but only the
/// second is about what the model typed. Wrapping the first in "those arguments could not be read"
/// would send it looking for a typo it did not make.
/// </remarks>
internal sealed class McpRefusedException(string message) : Exception(message);
