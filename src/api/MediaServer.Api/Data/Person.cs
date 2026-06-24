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

    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<MediaItemPerson> Credits { get; set; } = new List<MediaItemPerson>();
}
