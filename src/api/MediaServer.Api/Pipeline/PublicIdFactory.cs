using System.Security.Cryptography;
using System.Text;
using MediaServer.Api.Data;

namespace MediaServer.Api.Pipeline;

/// <summary>
/// Generates the stable, client-facing Jellyfin id from a canonical identity key. The id is a
/// 32-character lowercase hex rendering (Jellyfin's <c>Guid</c> shape) derived deterministically, so it
/// survives rescans and only changes on an operator remap to a different canonical title. See
/// <c>docs/planning/domain-model.md</c>.
/// </summary>
public static class PublicIdFactory
{
    /// <summary>
    /// The public id for a fully identified item, derived from its kind + canonical identity. Shared by
    /// the publish stage and the operator remap so both mint ids identically. Falls back to <c>local</c>
    /// + the internal id when an item has no provider identity.
    /// </summary>
    public static string ForItem(MediaItem item)
    {
        var provider = item.IdentityProvider ?? "local";
        var providerId = item.IdentityProviderId ?? item.Id.ToString("N");

        return item.Kind switch
        {
            MediaKind.Movie or MediaKind.Video => ForMovie(item.CatalogId, provider, providerId),
            MediaKind.Series => ForSeries(item.CatalogId, provider, providerId),
            MediaKind.Season => ForSeason(item.CatalogId, provider, providerId, item.IdentitySeasonNumber ?? item.IndexNumber ?? 1),
            MediaKind.Episode => ForEpisode(item.CatalogId, provider, providerId,
                item.IdentitySeasonNumber ?? item.ParentIndexNumber ?? 1, item.IdentityEpisodeNumber ?? item.IndexNumber ?? 0),
            _ => ForMovie(item.CatalogId, provider, providerId),
        };
    }

    public static string ForMovie(Guid catalogId, string provider, string providerId) =>
        FromKey($"movie|{catalogId:N}|{provider}|{providerId}");

    public static string ForSeries(Guid catalogId, string provider, string seriesProviderId) =>
        FromKey($"series|{catalogId:N}|{provider}|{seriesProviderId}");

    public static string ForSeason(Guid catalogId, string provider, string seriesProviderId, int season) =>
        FromKey($"season|{catalogId:N}|{provider}|{seriesProviderId}|{season}");

    public static string ForEpisode(Guid catalogId, string provider, string seriesProviderId, int season, int episode) =>
        FromKey($"episode|{catalogId:N}|{provider}|{seriesProviderId}|{season}|{episode}");

    internal static string FromKey(string key)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexStringLower(hash);
    }
}
