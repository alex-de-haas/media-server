using System.Globalization;
using System.Text.Json;

namespace MediaServer.Api.Collections;

/// <summary>
/// The franchise/collection a movie belongs to, parsed from a provider payload: the provider id (so a
/// collection is shared across its movies), the display name, and the collection's own artwork paths.
/// </summary>
public sealed record CollectionInfo(string ProviderId, string Name, string? PosterPath, string? BackdropPath);

/// <summary>
/// Pure parser for the <c>belongs_to_collection</c> object inside a cached TMDb movie detail payload
/// (<see cref="Data.MetadataRecord.Raw"/>). No database access, so it is straightforward to unit test.
/// TMDb returns this object on a movie's default detail response (it is not gated behind append_to_response).
/// </summary>
public static class CollectionMetadata
{
    /// <summary>
    /// How many owned movies a franchise needs before it is surfaced as a browsable collection (web grid and
    /// Infuse BoxSet alike). A single owned movie is not a franchise. Shared so the two surfaces never drift.
    /// </summary>
    public const int MinOwnedMovies = 2;

    private const string ImageBase = "https://image.tmdb.org/t/p/original";

    /// <summary>
    /// Parses the collection a movie belongs to. Returns null for null/blank/invalid JSON, a payload with no
    /// <c>belongs_to_collection</c> (most movies), or one missing a usable id and name.
    /// </summary>
    public static CollectionInfo? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("belongs_to_collection", out var collection) ||
                collection.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (JsonInt(collection, "id") is not { } id ||
                EmptyToNull(JsonString(collection, "name")) is not { } name)
            {
                return null;
            }

            return new CollectionInfo(
                id.ToString(CultureInfo.InvariantCulture),
                name,
                EmptyToNull(JsonString(collection, "poster_path")),
                EmptyToNull(JsonString(collection, "backdrop_path")));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Builds the absolute image URL from a raw provider path; null when there is none.</summary>
    public static string? ImageUrl(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : ImageBase + path;

    private static string? JsonString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? JsonInt(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number)
            ? number
            : null;

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
