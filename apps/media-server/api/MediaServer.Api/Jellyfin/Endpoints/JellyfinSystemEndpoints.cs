using MediaServer.Api.Jellyfin.Auth;

namespace MediaServer.Api.Jellyfin.Endpoints;

/// <summary>Anonymous discovery, native-client PIN auth, and session endpoints.</summary>
internal static class JellyfinSystemEndpoints
{
    public const string AuthRateLimitPolicy = "jellyfin-auth";

    public static void MapJellyfinSystemEndpoints(this IEndpointRouteBuilder routes)
    {
        // ---- Anonymous discovery ----
        routes.MapGet("/System/Info/Public", (JellyfinServerContext server) =>
            JellyfinJson.Ok(new SystemInfoPublic(server.ServerId, server.ServerName, JellyfinServerContext.ServerVersion)));

        routes.MapMethods("/System/Ping", ["GET", "POST"], (JellyfinServerContext server) =>
            JellyfinJson.Ok(server.ServerName));

        routes.MapGet("/Branding/Configuration", () => JellyfinJson.Ok(new BrandingConfiguration()));

        // We do not publicly enumerate users; Infuse logs in with an explicit username.
        routes.MapGet("/Users/Public", () => JellyfinJson.Ok(Array.Empty<UserDto>()));

        // ---- Native-client PIN login ----
        routes.MapPost("/Users/AuthenticateByName", async (
                HttpRequest request,
                AuthenticateByNameRequest body,
                JellyfinCredentialService credentials,
                JellyfinServerContext server,
                CancellationToken cancellationToken) =>
            {
                var device = MediaBrowserAuthorization.Parse(request, allowQueryToken: false).ToDeviceContext();
                var pin = body.Pw ?? body.Password ?? string.Empty;

                try
                {
                    var session = await credentials.AuthenticateAsync(body.Username ?? string.Empty, pin, device, cancellationToken);
                    var user = JellyfinEndpoints.BuildUserDto(session.User, server);
                    var sessionInfo = new SessionInfoDto(
                        Id: session.Token.Id.ToString("N"),
                        UserId: user.Id,
                        UserName: user.Name,
                        Client: session.Token.Client,
                        DeviceName: session.Token.DeviceName,
                        DeviceId: session.Token.DeviceId,
                        ApplicationVersion: session.Token.AppVersion,
                        ServerId: server.ServerId);

                    return JellyfinJson.Ok(new AuthenticationResultDto(user, sessionInfo, session.RawToken, server.ServerId));
                }
                catch (JellyfinAuthException exception)
                {
                    var status = exception.Reason switch
                    {
                        JellyfinAuthFailure.TemporarilyLocked => StatusCodes.Status429TooManyRequests,
                        JellyfinAuthFailure.PermanentlyLocked => StatusCodes.Status403Forbidden,
                        _ => StatusCodes.Status401Unauthorized,
                    };
                    return Results.StatusCode(status);
                }
            })
            .RequireRateLimiting(AuthRateLimitPolicy);

        // ---- Authenticated system/session ----
        var secured = routes.MapGroup(string.Empty).RequireJellyfin();

        secured.MapGet("/System/Info", (JellyfinServerContext server) =>
            JellyfinJson.Ok(new SystemInfo(server.ServerId, server.ServerName, JellyfinServerContext.ServerVersion)));

        secured.MapGet("/Sessions", (System.Security.Claims.ClaimsPrincipal principal, JellyfinServerContext server) =>
        {
            var userId = JellyfinPrincipal.UserId(principal);
            var tokenId = JellyfinPrincipal.TokenId(principal);
            if (userId is null || tokenId is null)
            {
                return JellyfinJson.Ok(Array.Empty<SessionInfoDto>());
            }

            var session = new SessionInfoDto(
                Id: tokenId.Value.ToString("N"),
                UserId: userId,
                UserName: principal.Identity?.Name ?? userId,
                Client: null,
                DeviceName: null,
                DeviceId: principal.FindFirst(JellyfinAuthenticationHandler.DeviceIdClaim)?.Value,
                ApplicationVersion: null,
                ServerId: server.ServerId);
            return JellyfinJson.Ok(new[] { session });
        });

        // Infuse posts its device capabilities right after connecting; we don't track live sessions
        // server-side, so accept and acknowledge (Jellyfin returns 204 No Content here).
        secured.MapPost("/Sessions/Capabilities/Full", () => Results.NoContent());
        secured.MapPost("/Sessions/Capabilities", () => Results.NoContent());

        secured.MapPost("/Sessions/Logout", async (
            System.Security.Claims.ClaimsPrincipal principal,
            JellyfinCredentialService credentials,
            CancellationToken cancellationToken) =>
        {
            if (JellyfinPrincipal.TokenId(principal) is { } tokenId)
            {
                await credentials.LogoutAsync(tokenId, cancellationToken);
            }

            return Results.NoContent();
        });
    }
}
