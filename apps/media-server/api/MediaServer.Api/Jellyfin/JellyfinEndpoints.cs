using System.Security.Claims;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.Jellyfin.Auth;
using MediaServer.Api.Jellyfin.Endpoints;

namespace MediaServer.Api.Jellyfin;

/// <summary>
/// Wires the Jellyfin-compatible surface served on the public <c>jellyfin</c> endpoint. Anonymous
/// discovery/auth routes are open; everything else requires a Media Server-owned token validated by the
/// <see cref="JellyfinAuthenticationHandler"/> scheme. See <c>docs/planning/jellyfin-compatibility.md</c>.
/// </summary>
public static class JellyfinEndpoints
{
    public static void MapJellyfinEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapJellyfinSystemEndpoints();
        routes.MapJellyfinUserEndpoints();
        routes.MapJellyfinItemsEndpoints();
        routes.MapJellyfinMediaEndpoints();
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
}
