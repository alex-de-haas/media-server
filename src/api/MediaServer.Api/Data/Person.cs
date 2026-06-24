namespace MediaServer.Api.Data;

/// <summary>
/// A cast or crew member, deduplicated across the whole library by its provider identity
/// (<see cref="Provider"/> + <see cref="ProviderId"/>). Per-item credit details (character, job,
/// billing order) live on the <see cref="MediaItemPerson"/> join, not here.
/// </summary>
public sealed class Person
{
    public Guid Id { get; set; }

    /// <summary>e.g. <c>tmdb</c>.</summary>
    public required string Provider { get; set; }

    /// <summary>The person's id within <see cref="Provider"/> (TMDb person id, as a string).</summary>
    public required string ProviderId { get; set; }

    public required string Name { get; set; }

    /// <summary>Raw provider profile path (e.g. <c>/abc.jpg</c>); null when the provider has no photo.</summary>
    public string? ProfilePath { get; set; }

    /// <summary>Absolute, ready-to-render profile image URL derived from <see cref="ProfilePath"/>.</summary>
    public string? ProfileUrl { get; set; }

    /// <summary>Populated later from a person-detail fetch; not available from media credits.</summary>
    public string? Biography { get; set; }

    /// <summary>The department the provider considers this person best known for, e.g. <c>Acting</c>.</summary>
    public string? KnownForDepartment { get; set; }

    /// <summary>Birth date as the provider returns it (e.g. <c>1974-11-11</c>); from a person-detail fetch.</summary>
    public string? Birthday { get; set; }

    /// <summary>Death date as the provider returns it, or null when the person is living/unknown.</summary>
    public string? Deathday { get; set; }

    /// <summary>Place of birth from a person-detail fetch; null when unknown.</summary>
    public string? PlaceOfBirth { get; set; }

    /// <summary>
    /// When the person-detail fetch (biography, birth/death, place of birth) last ran, used to refresh it
    /// lazily on a staleness window. Distinct from <see cref="UpdatedAt"/>, which credit syncs also bump —
    /// so it cannot tell whether the detail fields have ever been fetched. Null until the first fetch.
    /// </summary>
    public DateTimeOffset? DetailsFetchedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<MediaItemPerson> Credits { get; set; } = new List<MediaItemPerson>();
}
