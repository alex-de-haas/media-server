namespace MediaServer.Api.Data;

/// <summary>
/// One typed, dated release event of a <see cref="TrackedTitle"/> — a movie's theatrical/digital date in a
/// region, or a series episode air date. Unique per <c>(TrackedTitleId, Region, Type, Season, Episode)</c>.
/// </summary>
public sealed class TrackedRelease
{
    public Guid Id { get; set; }

    public Guid TrackedTitleId { get; set; }

    /// <summary>ISO-3166-1 country (<c>US</c>, <c>RU</c>); null for series episode air dates.</summary>
    public string? Region { get; set; }

    /// <summary>App-level release type (bucketed from the provider code at parse time).</summary>
    public ReleaseType Type { get; set; }

    /// <summary>Original TMDb code (1–6), kept so <see cref="ReleaseType.Theatrical"/> can tell wide from limited.</summary>
    public int? RawType { get; set; }

    /// <summary>Set for <see cref="ReleaseType.EpisodeAir"/>.</summary>
    public int? Season { get; set; }

    public int? Episode { get; set; }

    /// <summary>The release/air date.</summary>
    public DateTimeOffset Date { get; set; }

    /// <summary>Provider note (e.g. "IMAX", "Netflix") or episode name.</summary>
    public string? Note { get; set; }

    /// <summary>Prior value when a date moved, so the UI can show "moved to …".</summary>
    public DateTimeOffset? PreviousDate { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public TrackedTitle? TrackedTitle { get; set; }
}
