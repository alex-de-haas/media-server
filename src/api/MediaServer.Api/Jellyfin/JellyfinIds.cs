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

    /// <summary>The single synthetic "Collections" view (a Jellyfin boxsets collection folder).</summary>
    public static string CollectionsView() => Hex("view|collections");

    /// <summary>A movie franchise surfaces as a Jellyfin <c>BoxSet</c> folder under the Collections view.</summary>
    public static string Collection(Guid collectionId) => Hex($"collection|{collectionId:N}");

    /// <summary>
    /// The synthetic "Recommended" view. One id for every user, not one per user: its <em>contents</em>
    /// are personal, but a client stores the id it browsed, and a per-user id would change the library's
    /// identity under any client signed in as someone else.
    /// </summary>
    public static string RecommendationsView() => Hex("view|recommendations");

    /// <summary>Per playable source; lets clients pin a specific version via <c>MediaSourceId</c>.</summary>
    public static string MediaSource(Guid mediaSourceId) => Hex($"source|{mediaSourceId:N}");

    /// <summary>
    /// A cast/crew member. Keyed by the provider identity rather than the database row, so the id a client
    /// stored stays valid across a rescan exactly like item ids do.
    /// </summary>
    public static string Person(string provider, string providerId) =>
        Hex($"person|{provider.ToLowerInvariant()}|{providerId}");

    private static string Hex(string key) =>
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(key)));
}
