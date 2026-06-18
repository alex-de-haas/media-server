using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;

namespace MediaServer.Api.Pipeline;

/// <summary>
/// Shared mutable state for one drive of an ingest item. The entities are tracked by the per-drive
/// scoped <see cref="MediaServerDbContext"/>, so stages mutate them and the orchestrator persists.
/// </summary>
public sealed class IngestContext
{
    public required IngestItem Item { get; init; }

    public required Catalog Catalog { get; init; }

    public Download? Download { get; init; }

    public required List<SourceFile> SourceFiles { get; init; }

    public required CatalogPaths Paths { get; init; }
}
