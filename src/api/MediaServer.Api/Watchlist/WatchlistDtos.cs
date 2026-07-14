using MediaServer.Api.Data;

namespace MediaServer.Api.Watchlist;

// UI-facing DTOs for the internal `/api/watchlist` + `/api/reminders` surface (camelCase, enums by
// name). All reads are scoped to the authenticated AppUser; the shared TrackedTitle/TrackedRelease data
// is joined in but never mutated on another user's behalf.

/// <summary>The next dated release event of a tracked title, for list rows.</summary>
public sealed record NextReleaseDto(ReleaseType Type, DateOnly Date, int? Season, int? Episode);

/// <summary>Owned-vs-aired projection for a library-linked series (read-time, never persisted).</summary>
public sealed record LibraryGapDto(int OwnedEpisodes, int AiredEpisodes, int MissingAired);

/// <summary>A user's reminder, with its resolved display state for the reminders drawer pill.</summary>
public sealed record ReminderDto(
    Guid Id,
    Guid TrackedTitleId,
    string Title,
    string? PosterUrl,
    MediaKind Kind,
    ReleaseType ReleaseType,
    int LeadDays,
    string NotifyAt,
    bool Active,
    // scheduled | recurring | released | pending
    string State,
    DateOnly? Date);

/// <summary>One tracked title on the user's watchlist (a WatchlistEntry joined with its shared title).</summary>
public sealed record WatchlistItemDto(
    Guid Id,
    Guid TrackedTitleId,
    MediaKind Kind,
    string Title,
    int? Year,
    string? PosterUrl,
    string Provider,
    string ProviderId,
    string? ProductionStatus,
    bool InLibrary,
    Guid? LibraryItemId,
    SeriesMonitorScope? MonitorScope,
    IReadOnlyList<int>? MonitoredSeasons,
    string? RegionOverride,
    string? Note,
    NextReleaseDto? NextRelease,
    bool HasDates,
    LibraryGapDto? LibraryGap,
    IReadOnlyList<ReminderDto> Reminders);

/// <summary>A dated release event on the user's calendar.</summary>
public sealed record CalendarEventDto(
    Guid ReleaseId,
    Guid EntryId,
    Guid TrackedTitleId,
    MediaKind Kind,
    string Title,
    string? PosterUrl,
    ReleaseType Type,
    DateOnly Date,
    DateOnly? PreviousDate,
    int? Season,
    int? Episode,
    string? Note,
    bool HasReminder,
    bool InLibrary);

/// <summary>Canonical provider reference in request bodies, e.g. <c>{ "provider": "tmdb", "id": "27205" }</c>.</summary>
public sealed record ProviderRefBody(string Provider, string Id);

/// <summary>Body for <c>POST /api/watchlist</c>. Title/Year/PosterUrl are optional display hints from the
/// search candidate so the row renders instantly; the immediate sync refreshes them right after.</summary>
public sealed record AddWatchlistRequest(
    ProviderRefBody ProviderRef,
    MediaKind Kind,
    SeriesMonitorScope? MonitorScope,
    List<int>? MonitoredSeasons,
    string? RegionOverride,
    string? Note,
    string? Title,
    int? Year,
    string? PosterUrl);

/// <summary>Body for <c>PATCH /api/watchlist/{id}</c>; only supplied fields change. Because null is a
/// meaningful value for scope (tracking off), the flags say which fields are present.</summary>
public sealed record UpdateWatchlistRequest(
    bool? SetMonitorScope,
    SeriesMonitorScope? MonitorScope,
    List<int>? MonitoredSeasons,
    bool? SetRegionOverride,
    string? RegionOverride,
    bool? SetNote,
    string? Note);

/// <summary>Body for <c>POST /api/reminders</c>: the title by id or provider ref (Kind required with a
/// ref so remind-implies-track can create the title).</summary>
public sealed record CreateReminderRequest(
    Guid? TrackedTitleId,
    ProviderRefBody? ProviderRef,
    MediaKind? Kind,
    ReleaseType ReleaseType,
    int LeadDays,
    string? NotifyAt,
    string? Title,
    int? Year,
    string? PosterUrl);

/// <summary>Body for <c>PATCH /api/reminders/{id}</c>.</summary>
public sealed record UpdateReminderRequest(int? LeadDays, string? NotifyAt, bool? Active);

/// <summary>
/// The resolved state returned by reminder create: <c>scheduled</c> (with the date), <c>alreadyReleased</c>
/// (with the date), or <c>pending</c> (no date known yet — binds when the sync stores one). For a series,
/// <see cref="Detail"/> summarizes pre-existing aired episodes ("already airing — up to S2E10").
/// </summary>
public sealed record ReminderResolutionDto(ReminderDto Reminder, string State, DateOnly? Date, string? Detail)
{
    public const string Scheduled = "scheduled";
    public const string AlreadyReleased = "alreadyReleased";
    public const string Pending = "pending";
}
