using System.Security.Claims;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.Jellyfin.Auth;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Jellyfin;

public sealed record JellyfinCredentialStatusResponse(
    bool HasCredential,
    string? Username,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? LastUsedAt,
    bool Locked,
    bool PermanentlyLocked,
    string? ServerUrl);

public sealed record CreateJellyfinCredentialRequest(string? Pin);

public sealed record JellyfinCredentialSecretResponse(string Username, string? Pin, string? ServerUrl);

/// <summary>
/// Internal UI endpoints (Hosty identity) for a signed-in user to manage their own Jellyfin/Infuse
/// access credential. The plaintext PIN is returned only once, at creation/regeneration.
/// </summary>
public static class JellyfinCredentialEndpoints
{
    public static void MapJellyfinCredentialEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/jellyfin").RequireAuthorization();

        group.MapGet("/credential", async (
            ClaimsPrincipal principal,
            JellyfinCredentialService credentials,
            MediaServerDbContext database,
            HostyOptions hosty,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            var user = await ResolveUserAsync(principal, database, cancellationToken);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var credential = await credentials.GetCredentialAsync(user.Id, cancellationToken);
            var locked = credential is { LockedUntil: { } until } && until > time.GetUtcNow();
            return Results.Ok(new JellyfinCredentialStatusResponse(
                HasCredential: credential is { Revoked: false },
                Username: credential is { Revoked: false } ? credential.Username : null,
                CreatedAt: credential?.CreatedAt,
                LastUsedAt: credential?.LastUsedAt,
                Locked: locked,
                PermanentlyLocked: credential?.PermanentlyLocked ?? false,
                ServerUrl: hosty.JellyfinServerUrl));
        });

        group.MapPost("/credential", async (
            CreateJellyfinCredentialRequest? request,
            ClaimsPrincipal principal,
            JellyfinCredentialService credentials,
            MediaServerDbContext database,
            HostyOptions hosty,
            CancellationToken cancellationToken) =>
        {
            var user = await ResolveUserAsync(principal, database, cancellationToken);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            try
            {
                var issued = await credentials.CreateOrRegenerateAsync(user, request?.Pin, cancellationToken);
                return Results.Ok(new JellyfinCredentialSecretResponse(issued.Username, issued.GeneratedPin, hosty.JellyfinServerUrl));
            }
            catch (JellyfinCredentialValidationException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        group.MapDelete("/credential", async (
            ClaimsPrincipal principal,
            JellyfinCredentialService credentials,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            var user = await ResolveUserAsync(principal, database, cancellationToken);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var revoked = await credentials.RevokeCredentialAsync(user.Id, cancellationToken);
            return revoked ? Results.NoContent() : Results.NotFound();
        });
    }

    private static async Task<AppUser?> ResolveUserAsync(ClaimsPrincipal principal, MediaServerDbContext database, CancellationToken cancellationToken)
    {
        var hostUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(hostUserId))
        {
            return null;
        }

        return await database.AppUsers.FirstOrDefaultAsync(user => user.HostUserId == hostUserId, cancellationToken);
    }
}
