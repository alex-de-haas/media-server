namespace MediaServer.Api.Data;

/// <summary>
/// Per-user tracking subscription (layer 1): puts a <see cref="TrackedTitle"/> on that user's calendar.
/// Unique per <c>(AppUserId, TrackedTitleId)</c>.
/// </summary>
public sealed class WatchlistEntry
{
    public Guid Id { get; set; }

    public int AppUserId { get; set; }

    public Guid TrackedTitleId { get; set; }

    /// <summary>
    /// Series episode tracking is opt-in; <c>null</c> = off (the default for a new series entry, and
    /// always for movies). A non-null scope gates which <see cref="ReleaseType.EpisodeAir"/> rows exist.
    /// </summary>
    public SeriesMonitorScope? MonitorScope { get; set; }

    /// <summary>Season numbers watched when <see cref="MonitorScope"/> is <see cref="SeriesMonitorScope.Seasons"/>. JSON column.</summary>
    public List<int>? MonitoredSeasons { get; set; }

    /// <summary>Overrides the effective watch region (<c>WATCH_REGION</c>) for this entry.</summary>
    public string? RegionOverride { get; set; }

    /// <summary>Optional personal note.</summary>
    public string? Note { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public AppUser? AppUser { get; set; }

    public TrackedTitle? TrackedTitle { get; set; }
}
