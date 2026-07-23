namespace MediaServer.Api.Data;

/// <summary>
/// One client playback session for one item, used to count a viewing once however many times the
/// watched threshold is crossed.
/// </summary>
/// <remarks>
/// Without this, rewinding past the threshold and watching forward again re-counted the same
/// viewing: crossing 90% marks the item played, dropping back below clears the flag (it is a genuine
/// resume point), and the next crossing increments <see cref="UserItemData.PlayCount"/> again.
/// Observed on 2026-07-22 — one continuous session took an episode from 0 to 3 plays.
///
/// Keyed by the client's <c>PlaySessionId</c>, which Infuse echoes from our PlaybackInfo response and
/// keeps stable across <c>Sessions/Playing</c>, <c>Progress</c> and <c>Stopped</c> (verified across
/// 443 of 445 observed reports). Rows are disposable: losing one can at worst re-count a viewing, so
/// they are purged on age rather than tracked precisely.
/// </remarks>
public sealed class PlaybackSession
{
    public Guid Id { get; set; }

    public int AppUserId { get; set; }

    public Guid MediaItemId { get; set; }

    /// <summary>The client's <c>PlaySessionId</c>; unique per user and item.</summary>
    public string SessionKey { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }

    /// <summary>Last report seen for this session; drives age-based cleanup.</summary>
    public DateTimeOffset LastReportAt { get; set; }

    /// <summary>
    /// True once a report placed playback below the watched threshold. A session that has only ever
    /// been at or above it has shown no crossing — the client resumed there — so completing it is
    /// not evidence of a viewing.
    /// </summary>
    public bool ObservedBelowThreshold { get; set; }

    /// <summary>Set when this session already counted a viewing; a later crossing must not count another.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// The <see cref="PlaybackHistoryEntry"/> this session's completion created, once per-play history
    /// exists. Lets a restart or a repeated report reuse the same decision rather than merely
    /// suppressing a second count — the difference matters once a completion also enqueues outbound
    /// work, because re-deriving it would enqueue twice.
    /// </summary>
    public Guid? HistoryEntryId { get; set; }
}
