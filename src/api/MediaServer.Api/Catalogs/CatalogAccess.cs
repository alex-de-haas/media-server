using MediaServer.Api.Data;

namespace MediaServer.Api.Catalogs;

public sealed class ReadOnlyCatalogException(string name)
    : Exception($"Catalog \"{name}\" is read-only. This operation would change its files.");

public static class CatalogAccess
{
    public static void RequireWritable(Catalog catalog)
    {
        if (catalog.ReadOnly) throw new ReadOnlyCatalogException(catalog.Name);
    }
}
