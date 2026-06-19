using MediaServer.Api.Data;

namespace MediaServer.Api.Catalogs;

public sealed record CreateCatalogRequest(
    string Name,
    CatalogType Type,
    string Root,
    string? NamingTemplate,
    bool DefaultKeepSeeding,
    string? MetadataLanguage);

public sealed record UpdateCatalogRequest(
    string? Name,
    string? NamingTemplate,
    bool? DefaultKeepSeeding,
    string? MetadataLanguage);

public sealed record CatalogResponse(
    Guid Id,
    string Name,
    CatalogType Type,
    string Root,
    string NamingTemplate,
    bool DefaultKeepSeeding,
    string? MetadataLanguage,
    long FreeBytes,
    bool Online,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static CatalogResponse From(Catalog catalog, long freeBytes, bool online) => new(
        catalog.Id,
        catalog.Name,
        catalog.Type,
        catalog.Root,
        catalog.NamingTemplate,
        catalog.DefaultKeepSeeding,
        catalog.MetadataLanguage,
        freeBytes,
        online,
        catalog.CreatedAt,
        catalog.UpdatedAt);
}

/// <summary>
/// A catalog-root mount the operator may place catalogs under. <see cref="Path"/> is the absolute
/// base the app sees (a container path under docker, the host path under the dev runtime);
/// <see cref="Label"/> is a friendly name derived from it for the UI's mount picker.
/// </summary>
public sealed record CatalogMountResponse(string Label, string Path);

/// <summary>One catalog's footprint within a volume. <see cref="UsedBytes"/> is the sum of its tracked
/// media-source sizes — approximate (hardlinked files/↔library/ count once; in-flight partials and
/// non-media extras are not counted).</summary>
public sealed record CatalogUsageEntry(Guid Id, string Name, CatalogType Type, long UsedBytes);

/// <summary>
/// Storage usage for one volume that holds catalogs: each catalog's footprint plus the volume's free
/// space. The UI scales the bar to <c>Σ(used) + free</c>, so non-catalog usage is intentionally not
/// reported — it answers "how much have my catalogs written, and how much can I still write".
/// <see cref="FreeBytes"/> is a volume-level fact, shared by catalogs that sit on the same volume.
/// </summary>
public sealed record CatalogVolumeUsageResponse(
    string Label,
    long FreeBytes,
    IReadOnlyList<CatalogUsageEntry> Catalogs);

/// <summary>Thrown when catalog configuration fails validation; mapped to HTTP 400 by the endpoint.</summary>
public sealed class CatalogValidationException(string message) : Exception(message);

/// <summary>Thrown when a catalog can't be deleted because downloads/ingest still reference it;
/// mapped to HTTP 409 (Conflict) by the endpoint.</summary>
public sealed class CatalogInUseException(string message) : Exception(message);
