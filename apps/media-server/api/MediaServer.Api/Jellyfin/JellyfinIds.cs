using System.Security.Cryptography;
using System.Text;

namespace MediaServer.Api.Jellyfin;

/// <summary>
/// Derives the stable 32-character lowercase-hex ids (Jellyfin's <c>Guid</c> shape) that the
/// compatibility surface exposes for the server, users, and media-source ids. Media item ids reuse
/// the same shape via <see cref="Pipeline.PublicIdFactory"/> so they survive rescans.
/// </summary>
public static class JellyfinIds
{
    /// <summary>Stable per-deployment server id, derived from the Hosty app id.</summary>
    public static string Server(string appId) => Hex($"server|{appId}");

    /// <summary>Stable per-user id; the internal int id never leaks to clients.</summary>
    public static string User(int appUserId) => Hex($"user|{appUserId}");

    /// <summary>Catalogs surface as Jellyfin collection folders (views).</summary>
    public static string Catalog(Guid catalogId) => Hex($"catalog|{catalogId:N}");

    /// <summary>Per playable source; lets clients pin a specific version via <c>MediaSourceId</c>.</summary>
    public static string MediaSource(Guid mediaSourceId) => Hex($"source|{mediaSourceId:N}");

    private static string Hex(string key) =>
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(key)));
}
