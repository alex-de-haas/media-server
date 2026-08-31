namespace MediaServer.Api.Data;

/// <summary>
/// Operator-editable application settings — a single row (<see cref="SingletonId"/>). Unlike
/// <c>MediaServerSettings</c> (secrets/global toggles injected from the Hosty manifest), these are
/// mutable from the in-app Settings page and persisted so Hosty backup/restore covers them.
/// </summary>
public sealed class AppSettings
{
    /// <summary>The one and only row's id; every read/write targets this key.</summary>
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>
    /// Custom release-group / tag tokens stripped from a file name before identification (e.g.
    /// <c>LostFilm.TV</c>, <c>RARBG</c>). Matched case-insensitively as whole words after the name is
    /// normalized. See <c>NameParser</c> and <c>docs/features/metadata/feature.md</c>.
    /// </summary>
    public List<string> CustomReleaseGroups { get; set; } = [];

    /// <summary>
    /// How far the incremental metadata refresh has followed the provider's change list. Null until the
    /// first nightly pass, which records the instant it started watching rather than reaching backwards
    /// for a history it was never following.
    /// </summary>
    public DateTimeOffset? MetadataChangesSyncedThrough { get; set; }

    /// <summary>
    /// Media items whose enrich failed during an incremental refresh, carried to the next run.
    /// </summary>
    /// <remarks>
    /// The marker above says "changes up to here have been applied", and a title the provider timed out
    /// on has not been. Holding the marker back instead would grow the window every night until it hit
    /// the provider's limit and re-refreshed a fortnight, forever, over one unreachable title — so the
    /// marker moves and the exceptions are named here.
    /// </remarks>
    public List<string> MetadataRefreshRetries { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; }
}
