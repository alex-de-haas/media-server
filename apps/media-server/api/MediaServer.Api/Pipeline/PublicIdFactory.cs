using System.Security.Cryptography;
using System.Text;

namespace MediaServer.Api.Pipeline;

/// <summary>
/// Generates the stable, client-facing Jellyfin id from a canonical identity key. The id is a
/// 32-character lowercase hex rendering (Jellyfin's <c>Guid</c> shape) derived deterministically, so it
/// survives rescans and only changes on an operator remap to a different canonical title. See
/// <c>docs/planning/domain-model.md</c>.
/// </summary>
public static class PublicIdFactory
{
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
