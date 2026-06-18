using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaServer.Api.Jellyfin;

/// <summary>
/// Serialization for the Jellyfin surface. Jellyfin DTOs are PascalCase (the C# property names are
/// emitted verbatim), which differs from the camelCase the internal <c>/api</c> surface uses — so these
/// options are passed explicitly to <see cref="Results.Json"/> rather than changing the global policy.
/// </summary>
public static class JellyfinJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = null,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static IResult Ok(object value) => Results.Json(value, Options);

    public static IResult Json(object value, int statusCode) => Results.Json(value, Options, statusCode: statusCode);
}
