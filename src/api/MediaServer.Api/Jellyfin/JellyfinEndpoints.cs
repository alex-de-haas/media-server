using System.Security.Claims;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.Jellyfin.Auth;
using MediaServer.Api.Jellyfin.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Jellyfin;

/// <summary>
/// Wires the Jellyfin-compatible surface served on the public <c>jellyfin</c> endpoint. Anonymous
/// discovery/auth routes are open; everything else requires a Media Server-owned token validated by the
/// <see cref="JellyfinAuthenticationHandler"/> scheme. See <c>docs/features/jellyfin-compatibility.md</c>.
/// </summary>
public static class JellyfinEndpoints
{
    public static void MapJellyfinEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapJellyfinSystemEndpoints();
        routes.MapJellyfinUserEndpoints();
        routes.MapJellyfinItemsEndpoints();
        routes.MapJellyfinMediaEndpoints();
        routes.MapJellyfinPlaybackEndpoints();
    }

    /// <summary>Authorization for the authenticated Jellyfin routes (Jellyfin token scheme only).</summary>
    internal static RouteGroupBuilder RequireJellyfin(this RouteGroupBuilder group) =>
        group.RequireAuthorization(JellyfinAuthenticationHandler.PolicyName);

    internal static UserDto BuildUserDto(AppUser user, JellyfinServerContext server) => new(
        Name: user.DisplayName ?? user.Email ?? $"user-{user.Id}",
        ServerId: server.ServerId,
        Id: JellyfinIds.User(user.Id),
        HasPassword: true,
        HasConfiguredPassword: true,
        Configuration: new UserConfigurationDto(),
        Policy: new UserPolicyDto(IsAdministrator: user.Role == AppUserRole.Admin),
        LastLoginDate: user.LastSeenAt,
        LastActivityDate: user.LastSeenAt);
}

/// <summary>Reads the authenticated Jellyfin caller from the request principal.</summary>
internal static class JellyfinPrincipal
{
    public static int? AppUserId(ClaimsPrincipal principal) =>
        int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static string? UserId(ClaimsPrincipal principal) =>
        principal.FindFirstValue(JellyfinAuthenticationHandler.UserIdClaim);

    public static Guid? TokenId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(JellyfinAuthenticationHandler.TokenIdClaim), out var id) ? id : null;

    public static bool IsAdmin(ClaimsPrincipal principal) => principal.IsInRole(AppRoles.Admin);

    /// <summary>True when the route's user id targets the caller (or the caller is an admin).</summary>
    public static bool CanActAs(ClaimsPrincipal principal, string routeUserId) =>
        IsAdmin(principal) || string.Equals(UserId(principal), routeUserId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the internal app-user id a <c>/Users/{userId}/…</c> route should act on, honoring
    /// authorization: the caller for their own id, or (admin only) another user resolved from the hashed
    /// route id. Returns null when the caller may not act as the target or the id is unknown.
    /// </summary>
    public static async Task<int?> ResolveActingUserIdAsync(
        ClaimsPrincipal principal, string routeUserId, MediaServerDbContext database, CancellationToken cancellationToken)
    {
        if (string.Equals(UserId(principal), routeUserId, StringComparison.OrdinalIgnoreCase))
        {
            return AppUserId(principal);
        }

        if (!IsAdmin(principal))
        {
            return null;
        }

        // Admin acting on another user: the route id is the one-way hash of the int id, so scan ids only.
        // Project to int? so a "no match" result is null rather than the magic value 0.
        var ids = await database.AppUsers.AsNoTracking().Select(user => user.Id).ToListAsync(cancellationToken);
        return ids
            .Select(id => (int?)id)
            .FirstOrDefault(id => string.Equals(JellyfinIds.User(id!.Value), routeUserId, StringComparison.OrdinalIgnoreCase));
    }
}
