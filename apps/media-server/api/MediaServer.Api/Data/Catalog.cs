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

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
