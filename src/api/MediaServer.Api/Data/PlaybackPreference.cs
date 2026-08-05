namespace MediaServer.Api.Data;

/// <summary>
/// What a viewer wants played, expressed as <b>intent rather than stream indexes</b>. An index means
/// nothing across two editions of the same film — a remux and a 1080p cut have different track
/// layouts — so the choice is stored as "Russian dub, no subtitles" and resolved against whatever the
/// source actually holds.
///
/// Scoped globally (<see cref="MediaItemId"/> null) or to one title; for a show the id is the series,
/// so a preference set on one episode applies to the next. See
/// <c>docs/features/native-playback/plan.md</c>.
/// </summary>
public sealed class PlaybackPreference
{
    public Guid Id { get; set; }

    public int AppUserId { get; set; }

    /// <summary>Null for the user's default; otherwise the movie or series this overrides it for.</summary>
    public Guid? MediaItemId { get; set; }

    /// <summary>Preferred audio language as a library language tag; null leaves the source's own default.</summary>
    public string? AudioLanguage { get; set; }

    /// <summary>Preferred subtitle language; null means no subtitles are chosen for the viewer.</summary>
    public string? SubtitleLanguage { get; set; }

    /// <summary>Only pick a subtitle track flagged forced — signs and songs over a dub, not full dialogue.</summary>
    public bool SubtitlesForcedOnly { get; set; }

    /// <summary>
    /// Prefer the work's original language over <see cref="AudioLanguage"/> when the source has it.
    /// A viewer who normally wants a dub but watches a given show subtitled sets this on that show.
    /// </summary>
    public bool PreferOriginalAudio { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
