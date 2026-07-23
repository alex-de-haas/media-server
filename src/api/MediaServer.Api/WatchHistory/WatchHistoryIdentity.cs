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

    /// <summary>
    /// True when this identity is complete and well-formed enough to send anywhere. This is the guard
    /// outbound delivery relies on, so it rejects impossible coordinates as well as missing ones —
    /// nonsense numbering must surface as an issue rather than reach the provider.
    /// </summary>
    /// <remarks>
    /// Season 0 is valid: it is the specials season by TMDb convention, which Trakt shares. Episode
    /// numbering starts at 1, and negative values are meaningless in either position.
    /// </remarks>
    public bool IsResolvable => Kind switch
    {
        WatchHistoryMediaKind.Movie => HasExternalId,
        WatchHistoryMediaKind.Episode => HasExternalId
            && SeasonNumber is >= 0
            && EpisodeNumber is >= 1
            && EpisodeNumberEnd is null or >= 1,
        _ => false,
    };

    private bool HasExternalId => TmdbId is not null || !string.IsNullOrWhiteSpace(ImdbId);

    /// <summary>
    /// Expands a multi-episode range into one identity per episode; every other identity yields itself.
    /// </summary>
    public IEnumerable<WatchHistoryIdentity> Expand()
    {
        var last = EpisodeNumberEnd;
        if (Kind is not WatchHistoryMediaKind.Episode || EpisodeNumber is not { } first || last is null || last <= first)
        {
            // Clear a degenerate end rather than passing it on: `end == start` and `end < start` are
            // not ranges, and letting either reach a snapshot or a provider propagates malformed data
            // that later readers would have to re-interpret.
            yield return EpisodeNumberEnd is null ? this : this with { EpisodeNumberEnd = null };
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
