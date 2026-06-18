namespace MediaServer.Api.Data;

/// <summary>
/// An operator-configured destination for content. The chosen catalog drives filename parsing,
/// target paths, naming, seeding policy, and metadata language. <c>Root</c> is a single host
/// directory on one filesystem containing sibling <c>files/</c> and <c>library/</c> subtrees so the
/// organizer can hardlink between them.
/// </summary>
public sealed class Catalog
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public CatalogType Type { get; set; }

    /// <summary>Host path; contains <c>files/</c> + <c>library/</c> on one filesystem.</summary>
    public required string Root { get; set; }

    /// <summary>e.g. <c>{Title} ({Year})</c>.</summary>
    public string NamingTemplate { get; set; } = "{Title} ({Year})";

    public bool DefaultKeepSeeding { get; set; }

    /// <summary>Optional override of the global <c>SUPPORTED_LANGUAGES</c> default.</summary>
    public string? MetadataLanguage { get; set; }

    /// <summary>
    /// Set when the health monitor first observes the root as unreachable; cleared when it returns.
    /// Used to notify the operator (and trigger a rescan) only on the offline→online transition, not
    /// on every check. Null means the root was reachable at the last check.
    /// </summary>
    public DateTimeOffset? OfflineSince { get; set; }

    /// <summary>Set when free space first crosses below the low-disk threshold; cleared when it recovers.</summary>
    public DateTimeOffset? LowDiskSince { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
