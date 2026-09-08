namespace MediaServer.Api.Data;

/// <summary>
/// Orders a media item's sources for the surfaces that hand them to a player. Clients (Infuse) treat the
/// first entry of <c>MediaSources</c> as the default to play when no explicit <c>MediaSourceId</c> is sent,
/// so putting the item's chosen <see cref="MediaItem.DefaultSourceId"/> first is how we steer the default
/// version. Ties (and the no-preference case) fall back to oldest-first for a stable, deterministic order.
/// </summary>
public static class MediaSourceOrdering
{
    public static IReadOnlyList<MediaSource> OrderByDefault(this IEnumerable<MediaSource> sources, Guid? defaultSourceId) =>
        sources.OrderByDefault(defaultSourceId, source => source.Id, source => source.CreatedAt);

    /// <summary>
    /// The same order over a projection of the sources — whatever shape a caller read instead of the entity —
    /// so a listing that fetches only what it shows still agrees with the players on which version leads.
    /// </summary>
    public static IReadOnlyList<T> OrderByDefault<T>(
        this IEnumerable<T> sources, Guid? defaultSourceId, Func<T, Guid> id, Func<T, DateTimeOffset> createdAt) =>
        sources
            .OrderByDescending(source => defaultSourceId is { } wanted && id(source) == wanted)
            .ThenBy(createdAt)
            .ThenBy(id)
            .ToList();
}
