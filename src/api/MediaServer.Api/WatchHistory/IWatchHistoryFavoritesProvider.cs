namespace MediaServer.Api.WatchHistory;

/// <summary>
/// What can carry a favorite: a whole movie or a whole series. Deliberately not
/// <see cref="WatchHistoryMediaKind"/> — that one models *plays*, where a series is expanded to its
/// episodes, while a favorite is a statement about the work itself and providers store it that way.
/// </summary>
public enum FavoriteWorkKind
{
    Movie,
    Series,
}

/// <summary>A favoritable work, addressed the way providers address works.</summary>
public sealed record FavoriteIdentity(FavoriteWorkKind Kind, int? TmdbId, string? ImdbId)
{
    /// <summary>False when nothing a provider understands is known about the work.</summary>
    public bool IsResolvable => TmdbId is not null || !string.IsNullOrWhiteSpace(ImdbId);
}

/// <summary>One favorite as the provider holds it. <see cref="FavoritedAt"/> is null when it says nothing.</summary>
public sealed record ProviderFavorite(FavoriteIdentity Identity, DateTimeOffset? FavoritedAt);

/// <summary>
/// The provider's favorites, plus how full the list is. <see cref="Capacity"/> is the provider's own cap
/// (Trakt allows 100 for every account) so the core never hard-codes another service's limit; both are
/// null when the provider does not say.
/// </summary>
public sealed record FavoritesSnapshot(IReadOnlyList<ProviderFavorite> Favorites, int? RemoteCount, int? Capacity);

/// <summary>
/// What one favorites write did. <see cref="Unchanged"/> counts works the provider already agreed with
/// (adding a favorite twice, removing one that was gone) — a success, not a failure. Works the provider
/// could not identify are counted separately: they are the caller's business to surface, not a reason
/// to fail a batch that otherwise landed.
/// </summary>
public sealed record FavoritesWriteResult(int Applied, int Unchanged, int NotFound, int? RemoteCount, int? Capacity = null);

/// <summary>
/// A provider that also carries favorites. Optional on purpose: an adapter that only knows plays stays
/// a complete <see cref="IWatchHistoryProvider"/>, and the core asks for this interface rather than
/// consulting a capability flag that could disagree with the type.
/// </summary>
public interface IWatchHistoryFavoritesProvider
{
    /// <summary>
    /// The provider whose connection this extends — the same key as its
    /// <see cref="IWatchHistoryProvider.Key"/>, so one account covers history and favorites alike.
    /// </summary>
    string ProviderKey { get; }

    /// <summary>Every favorite the provider holds for this user, movies and series alike.</summary>
    Task<WatchHistoryResult<FavoritesSnapshot>> GetFavoritesAsync(int appUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Adds favorites. A provider whose list is full answers
    /// <see cref="WatchHistoryFailure.AccountLimitReached"/> — terminal, because retrying cannot succeed
    /// until the user frees space.
    /// </summary>
    Task<WatchHistoryResult<FavoritesWriteResult>> AddFavoritesAsync(
        int appUserId, IReadOnlyCollection<FavoriteIdentity> identities, CancellationToken cancellationToken);

    /// <summary>Removes favorites. Removing one that is already gone is success, not an error.</summary>
    Task<WatchHistoryResult<FavoritesWriteResult>> RemoveFavoritesAsync(
        int appUserId, IReadOnlyCollection<FavoriteIdentity> identities, CancellationToken cancellationToken);
}
