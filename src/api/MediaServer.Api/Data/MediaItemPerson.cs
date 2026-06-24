namespace MediaServer.Api.Data;

/// <summary>
/// One credit linking a <see cref="Person"/> to a <see cref="MediaItem"/>. A person can have several rows
/// for the same item (e.g. an actor who also directed): one <see cref="PersonRole.Cast"/> row carrying the
/// <see cref="Character"/>, plus a <see cref="PersonRole.Crew"/> row carrying the <see cref="Job"/>.
/// </summary>
public sealed class MediaItemPerson
{
    public Guid Id { get; set; }

    public Guid MediaItemId { get; set; }

    public Guid PersonId { get; set; }

    public PersonRole Role { get; set; }

    /// <summary>The portrayed character; set for <see cref="PersonRole.Cast"/> rows.</summary>
    public string? Character { get; set; }

    /// <summary>The crew job, e.g. <c>Director</c>; set for <see cref="PersonRole.Crew"/> rows.</summary>
    public string? Job { get; set; }

    /// <summary>The crew department, e.g. <c>Directing</c>; set for <see cref="PersonRole.Crew"/> rows.</summary>
    public string? Department { get; set; }

    /// <summary>Billing/display order within the item (provider billing order for cast).</summary>
    public int Order { get; set; }

    public MediaItem? MediaItem { get; set; }

    public Person? Person { get; set; }
}
