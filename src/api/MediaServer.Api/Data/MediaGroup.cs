namespace MediaServer.Api.Data;

/// <summary>A server-wide manual selection or saved title query, independent of TMDb franchises.</summary>
public sealed class MediaGroup
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Kind { get; set; }
    public required string CatalogType { get; set; }
    public required string RulesJson { get; set; }
}

/// <summary>Explicit membership survives tombstoning and cascades away on a hard purge.</summary>
public sealed class MediaGroupMember
{
    public Guid MediaGroupId { get; set; }
    public Guid MediaItemId { get; set; }
}
