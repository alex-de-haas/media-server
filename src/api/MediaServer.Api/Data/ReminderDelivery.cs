namespace MediaServer.Api.Data;

/// <summary>
/// The sent ledger: one row per reminder per concrete release event it fired for. Makes a movie
/// reminder one-shot and a series episode reminder recurring through the same mechanism — dispatch
/// only ever delivers a <c>(reminder, release)</c> pair that has no row here. Unique per
/// <c>(ReminderId, TrackedReleaseId)</c>.
/// </summary>
public sealed class ReminderDelivery
{
    public Guid Id { get; set; }

    public Guid ReminderId { get; set; }

    /// <summary>The concrete release event this delivery fired for.</summary>
    public Guid TrackedReleaseId { get; set; }

    public DateTimeOffset SentAt { get; set; }

    public ReleaseReminder? Reminder { get; set; }

    public TrackedRelease? TrackedRelease { get; set; }
}
