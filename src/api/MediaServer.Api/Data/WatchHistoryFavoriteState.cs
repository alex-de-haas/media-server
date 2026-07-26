namespace MediaServer.Api.Data;

/// <summary>
/// What one connection knew about one work's favorite state at the provider, as of the last
/// reconciliation. It is the memory that lets a later sync tell "added remotely" from "removed
/// locally" — without it, a work favorited here and absent there is indistinguishable from one
/// unfavorited there and still flagged here, and reconciliation would have to guess.
/// </summary>
/// <remarks>
/// Keyed by canonical identity rather than <see cref="MediaItem"/>: a favorite is a statement about the
/// work, and it must survive the item being deleted (a tombstone keeps the flag), re-imported into
/// another catalog, or split across two copies while a pre-existing duplicate awaits repair.
/// </remarks>
public sealed class WatchHistoryFavoriteState
{
    public Guid Id { get; set; }

    public Guid ConnectionId { get; set; }

    /// <summary>Movie or Series — the two things a provider can hold a favorite for.</summary>
    public MediaKind Kind { get; set; }

    /// <summary>Canonical provider of the identity below, e.g. <c>tmdb</c>.</summary>
    public string IdentityProvider { get; set; } = string.Empty;

    public string IdentityProviderId { get; set; } = string.Empty;

    /// <summary>Whether the provider held this favorite when the last reconciliation ran.</summary>
    public bool RemotePresent { get; set; }

    /// <summary>The provider's own timestamp for the favorite, when it gave one.</summary>
    public DateTimeOffset? RemoteFavoritedAt { get; set; }

    /// <summary>Whether this app held it locally at that same moment.</summary>
    public bool LocalFavorite { get; set; }

    public DateTimeOffset ReconciledAt { get; set; }

    public WatchHistoryProviderConnection? Connection { get; set; }
}
