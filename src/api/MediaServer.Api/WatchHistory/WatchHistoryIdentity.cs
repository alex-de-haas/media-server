using MediaServer.Api.Data;

namespace MediaServer.Api.WatchHistory;

/// <summary>
/// What a provider needs to name one movie or episode, with no provider's vocabulary in it. The core
/// builds these from local rows; an adapter translates them into whatever its API wants.
/// </summary>
/// <remarks>
/// Snapshots are immutable and are stored alongside outbound work: a delivery that runs minutes after
/// the local change must describe the item as it was identified then, even if the library has since
/// been rescanned, re-identified, or deleted.
/// </remarks>
public sealed record WatchHistoryIdentity
{
    public required WatchHistoryMediaKind Kind { get; init; }

    /// <summary>TMDb id of the movie, or of the *series* for an episode. Null when unidentified.</summary>
    public int? TmdbId { get; init; }

    /// <summary>IMDb id (<c>tt…</c>) when known; a second lever for providers that prefer it.</summary>
    public string? ImdbId { get; init; }

    /// <summary>Canonical season number; episodes only.</summary>
    public int? SeasonNumber { get; init; }

    /// <summary>Canonical episode number; episodes only. For a multi-episode file this is the first.</summary>
    public int? EpisodeNumber { get; init; }

    /// <summary>
    /// Last episode of an inclusive range when one file holds several episodes, else null. Providers
    /// have no notion of a double episode, so outbound delivery expands the range into one entry per
    /// episode — see the observation results in the plan.
    /// </summary>
    public int? EpisodeNumberEnd { get; init; }

    /// <summary>True when this identity is complete enough to send anywhere.</summary>
    public bool IsResolvable => Kind switch
    {
        WatchHistoryMediaKind.Movie => TmdbId is not null || ImdbId is not null,
        WatchHistoryMediaKind.Episode => (TmdbId is not null || ImdbId is not null)
            && SeasonNumber is not null
            && EpisodeNumber is not null,
        _ => false,
    };

    /// <summary>
    /// Expands a multi-episode range into one identity per episode; every other identity yields itself.
    /// </summary>
    public IEnumerable<WatchHistoryIdentity> Expand()
    {
        var last = EpisodeNumberEnd;
        if (Kind is not WatchHistoryMediaKind.Episode || EpisodeNumber is not { } first || last is null || last <= first)
        {
            yield return this;
            yield break;
        }

        for (var episode = first; episode <= last; episode++)
        {
            yield return this with { EpisodeNumber = episode, EpisodeNumberEnd = null };
        }
    }
}

/// <summary>
/// The kinds a watch-history provider can carry. Deliberately narrower than <see cref="MediaKind"/>:
/// seasons and series are expanded to their episodes before they reach a provider, and extras have no
/// counterpart to sync to.
/// </summary>
public enum WatchHistoryMediaKind
{
    Movie,
    Episode,
}
