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
        sources
            .OrderByDescending(source => defaultSourceId is { } id && source.Id == id)
            .ThenBy(source => source.CreatedAt)
            .ThenBy(source => source.Id)
            .ToList();
}
