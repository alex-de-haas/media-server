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

/// <summary>Thrown when catalog configuration fails validation; mapped to HTTP 400 by the endpoint.</summary>
public sealed class CatalogValidationException(string message) : Exception(message);
