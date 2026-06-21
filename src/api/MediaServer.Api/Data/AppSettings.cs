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
    /// normalized. See <c>NameParser</c> and <c>docs/features/metadata.md</c>.
    /// </summary>
    public List<string> CustomReleaseGroups { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; }
}
