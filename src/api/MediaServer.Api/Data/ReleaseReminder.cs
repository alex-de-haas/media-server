namespace MediaServer.Api.Data;

/// <summary>
/// Per-user reminder (layer 2) targeting a title + release type — not a specific date — so the same
/// object covers a known date, a not-yet-announced one, and one already passed. Unique per
/// <c>(AppUserId, TrackedTitleId, ReleaseType)</c>.
/// </summary>
public sealed class ReleaseReminder
{
    public Guid Id { get; set; }

    public int AppUserId { get; set; }

    public Guid TrackedTitleId { get; set; }

    /// <summary>The kind of release awaited (<see cref="ReleaseType.EpisodeAir"/> for series).</summary>
    public ReleaseType ReleaseType { get; set; }

    /// <summary><c>0</c> = on the day, <c>N</c> = N days before.</summary>
    public int LeadDays { get; set; }

    /// <summary>Preferred local time-of-day (app timezone), default 09:00.</summary>
    public TimeOnly NotifyAt { get; set; } = new(9, 0);

    /// <summary>Soft-disable without deleting; also flipped off when a series reminder retires.</summary>
    public bool Active { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public AppUser? AppUser { get; set; }

    public TrackedTitle? TrackedTitle { get; set; }

    public ICollection<ReminderDelivery> Deliveries { get; set; } = new List<ReminderDelivery>();
}
