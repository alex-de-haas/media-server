namespace MediaServer.Api.Data;

/// <summary>
/// Global release-tracking subject: one row per canonical provider identity (deduped across users).
/// A title tracked by several users is stored and synced once; the per-user subscription lives in
/// <see cref="WatchlistEntry"/>. See <c>docs/features/release-tracking.md</c>.
/// </summary>
public sealed class TrackedTitle
{
    public Guid Id { get; set; }

    /// <summary>Constrained subset: <see cref="MediaKind.Movie"/> or <see cref="MediaKind.Series"/>.</summary>
    public MediaKind Kind { get; set; }

    /// <summary>Canonical provider, e.g. <c>tmdb</c>. Unique together with <see cref="IdentityProviderId"/>.</summary>
    public required string IdentityProvider { get; set; }

    public required string IdentityProviderId { get; set; }

    /// <summary>Provider dictionary, mirrors <see cref="MediaItem.Providers"/>. Stored as a JSON column.</summary>
    public Dictionary<string, string> Providers { get; set; } = new();

    /// <summary>FK to the library item once identity resolves; null while pure wishlist.</summary>
    public Guid? MediaItemId { get; set; }

    /// <summary>Denormalized display snapshot, refreshed on sync.</summary>
    public string Title { get; set; } = string.Empty;

    public int? Year { get; set; }

    /// <summary>Poster thumbnail URL — part of the display snapshot, refreshed on sync.</summary>
    public string? PosterUrl { get; set; }

    /// <summary>Provider production status (Released, Ended, Canceled, …); lets sync cheaply skip a settled title.</summary>
    public string? ProductionStatus { get; set; }

    /// <summary>
    /// Title-level <c>last_episode_to_air</c> snapshot (series only), refreshed on sync. Reminder creation
    /// resolves the series already-airing state from these — never from persisted episode rows, which the
    /// rolling horizon prunes.
    /// </summary>
    public int? LastAiredSeason { get; set; }

    public int? LastAiredEpisode { get; set; }

    public DateOnly? LastAiredDate { get; set; }

    /// <summary>
    /// Title-level <c>next_episode_to_air</c> snapshot (series only), refreshed on sync. Identifies which
    /// episode rows a tracking-off entry surfaces on the calendar ("when it returns"), independent of
    /// whatever other users' monitor scopes materialized.
    /// </summary>
    public int? NextAirSeason { get; set; }

    public int? NextAirEpisode { get; set; }

    public DateOnly? NextAirDate { get; set; }

    /// <summary>Last successful provider sync (diagnostics).</summary>
    public DateTimeOffset? LastRefreshedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public MediaItem? MediaItem { get; set; }

    public ICollection<TrackedRelease> Releases { get; set; } = new List<TrackedRelease>();

    public ICollection<WatchlistEntry> Entries { get; set; } = new List<WatchlistEntry>();
}
