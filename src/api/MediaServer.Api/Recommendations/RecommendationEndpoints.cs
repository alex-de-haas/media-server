using System.Security.Claims;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Recommendations;

/// <summary>The title a hide or unhide acts on.</summary>
public sealed record RecommendationHideRequest(RecommendationKind Kind, string TmdbId);

/// <summary>The user's source narrowing; an empty or absent list means "every available source".</summary>
public sealed record RecommendationSourcesRequest(IReadOnlyList<string>? Sources);

/// <summary>
/// The recommendations surface, scoped to the signed-in user.
/// </summary>
/// <remarks>
/// Every route acts on the caller and none accepts a user id: a feed is built from what someone
/// watched, so serving another user's would leak exactly that.
/// </remarks>
public static class RecommendationEndpoints
{
    /// <summary>Enough for the page; the home row asks for fewer.</summary>
    private const int DefaultLimit = 40;

    private const int MaxLimit = 100;

    public static void MapRecommendationEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/recommendations").RequireAuthorization();

        group.MapGet("/", async (
            RecommendationKind? kind,
            int? limit,
            ClaimsPrincipal principal,
            RecommendationFeedService feed,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            var user = await ResolveUserAsync(principal, database, cancellationToken);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var bounded = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
            return Results.Ok(await feed.BuildAsync(user.Id, kind, bounded, cancellationToken));
        });

        group.MapPost("/hide", async (
            RecommendationHideRequest request,
            ClaimsPrincipal principal,
            RecommendationFeedService feed,
            MediaServerDbContext database,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            var user = await ResolveUserAsync(principal, database, cancellationToken);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(request.TmdbId))
            {
                return Results.BadRequest(new { error = "A TMDb id is required." });
            }

            await feed.HideAsync(
                user.Id,
                new RecommendationIdentity(request.Kind, request.TmdbId.Trim()),
                time.GetUtcNow(),
                cancellationToken);

            return Results.NoContent();
        });

        group.MapDelete("/hide", async (
            RecommendationKind kind,
            string tmdbId,
            ClaimsPrincipal principal,
            RecommendationFeedService feed,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            var user = await ResolveUserAsync(principal, database, cancellationToken);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(tmdbId))
            {
                // A blank id would otherwise look like a successful no-op and hide a caller's bug.
                return Results.BadRequest(new { error = "A TMDb id is required." });
            }

            // Idempotent: unhiding something that is not hidden is the state the caller wanted.
            await feed.UnhideAsync(user.Id, new RecommendationIdentity(kind, tmdbId.Trim()), cancellationToken);
            return Results.NoContent();
        });

        group.MapPut("/sources", async (
            RecommendationSourcesRequest request,
            ClaimsPrincipal principal,
            RecommendationFeedService feed,
            MediaServerDbContext database,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            var user = await ResolveUserAsync(principal, database, cancellationToken);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            await feed.SetSourcesAsync(user.Id, request.Sources, time.GetUtcNow(), cancellationToken);
            return Results.NoContent();
        });
    }

    private static async Task<AppUser?> ResolveUserAsync(
        ClaimsPrincipal principal, MediaServerDbContext database, CancellationToken cancellationToken)
    {
        var hostUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return string.IsNullOrEmpty(hostUserId)
            ? null
            : await database.AppUsers.FirstOrDefaultAsync(user => user.HostUserId == hostUserId, cancellationToken);
    }
}
