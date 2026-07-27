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

    /// <summary>
    /// The Hosty catalog-root mount this catalog lives under (<c>HOSTY_MOUNT_CATALOGROOTS</c>). Together
    /// with <see cref="MountRelativePath"/> it is the catalog's durable identity: the label is the same
    /// under every runtime profile, while the absolute path Hosty injects for it is not (host paths under
    /// <c>dev</c>, container paths under <c>docker</c>). Null only for standalone runs where no mounts are
    /// injected and the operator gave a free-text absolute root.
    /// </summary>
    public string? MountLabel { get; set; }

    /// <summary>
    /// Path of the catalog relative to its mount root, posix-style; empty when the catalog *is* the mount
    /// root. Non-null exactly when <see cref="MountLabel"/> is.
    /// </summary>
    public string? MountRelativePath { get; set; }

    /// <summary>
    /// The absolute path of the catalog root <b>in the current runtime</b>: contains <c>.incoming/</c> plus
    /// the published tree, on one filesystem. Derived from <see cref="MountLabel"/> +
    /// <see cref="MountRelativePath"/> and re-resolved at every startup (see
    /// <see cref="MediaServer.Api.Catalogs.CatalogAnchorService"/>) — it is a cache of "where is this
    /// catalog right now", never the identity. Stored so the whole app can keep reading one absolute path.
    /// </summary>
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
