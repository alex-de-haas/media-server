using System.Security.Claims;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.WatchHistory;

/// <summary>One provider as Settings renders it, including this user's connection when there is one.</summary>
public sealed record WatchHistoryProviderResponse(
    string Key,
    string DisplayName,
    bool IsConfigured,
    bool SupportsExactTimestamps,
    WatchHistoryConnectionResponse? Connection);

/// <summary>This user's connection to one provider. Carries no credential material of any kind.</summary>
public sealed record WatchHistoryConnectionResponse(
    string ProviderKey,
    string Status,
    string? AccountName,
    DateTimeOffset ConnectedAt,
    DateTimeOffset? LastDeliveryAt,
    DateTimeOffset? LastSyncAt,
    string? LastError);

/// <summary>What to show the user so they can approve the connection on the provider's site.</summary>
public sealed record WatchHistoryAuthorizationResponse(
    string State,
    string? UserCode,
    string? VerificationUrl,
    DateTimeOffset? ExpiresAt,
    int? PollIntervalSeconds,
    WatchHistoryConnectionResponse? Connection);

/// <summary>
/// Internal UI endpoints (Hosty identity) for a signed-in user to manage their own watched-history
/// provider connections.
/// </summary>
/// <remarks>
/// Every route acts on the caller. None accepts an app-user id, so an administrator who configures
/// the instance's provider application still cannot connect an account for someone else or read their
/// tokens — the same boundary the authorization services enforce, restated at the edge so it cannot
/// be lost by a future refactor. No response carries an access token, a refresh token, or a device
/// code; the activation code and URL are safe to display by design.
/// </remarks>
public static class WatchHistoryEndpoints
{
    public static void MapWatchHistoryEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/watch-history").RequireAuthorization();

        group.MapGet("/providers", async (
            ClaimsPrincipal principal,
            IWatchHistoryProviderRegistry registry,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            var user = await ResolveUserAsync(principal, database, cancellationToken);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var connections = await database.WatchHistoryConnections
                .AsNoTracking()
                .Where(entry => entry.AppUserId == user.Id)
                .ToListAsync(cancellationToken);

            var providers = registry.Describe()
                .Select(descriptor => new WatchHistoryProviderResponse(
                    descriptor.Key,
                    descriptor.DisplayName,
                    descriptor.IsConfigured,
                    descriptor.Capabilities.ExactTimestampWrites,
                    ToResponse(connections.FirstOrDefault(entry =>
                        string.Equals(entry.ProviderKey, descriptor.Key, StringComparison.OrdinalIgnoreCase)))))
                .ToList();

            return Results.Ok(providers);
        });

        group.MapPost("/connections/{providerKey}/authorization/start", async (
            string providerKey,
            ClaimsPrincipal principal,
            IWatchHistoryProviderRegistry registry,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            var context = await ResolveAsync(principal, providerKey, registry, database, cancellationToken);
            if (context.Problem is not null)
            {
                return context.Problem;
            }

            var started = await context.Authorization!.StartAsync(context.User!.Id, cancellationToken);
            if (!started.Succeeded)
            {
                return ToProblem(started.Failure!.Value, started.Detail);
            }

            var prompt = started.Value!;
            return Results.Ok(new WatchHistoryAuthorizationResponse(
                nameof(WatchHistoryAuthorizationState.Pending),
                prompt.UserCode,
                prompt.VerificationUrl,
                prompt.ExpiresAt,
                (int)prompt.PollInterval.TotalSeconds,
                Connection: null));
        });

        group.MapPost("/connections/{providerKey}/authorization/poll", async (
            string providerKey,
            ClaimsPrincipal principal,
            IWatchHistoryProviderRegistry registry,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            var context = await ResolveAsync(principal, providerKey, registry, database, cancellationToken);
            if (context.Problem is not null)
            {
                return context.Problem;
            }

            var polled = await context.Authorization!.PollAsync(context.User!.Id, cancellationToken);
            if (!polled.Succeeded)
            {
                return ToProblem(polled.Failure!.Value, polled.Detail);
            }

            var outcome = polled.Value!;
            var connection = outcome.State == WatchHistoryAuthorizationState.Approved
                ? await database.WatchHistoryConnections.AsNoTracking().FirstOrDefaultAsync(
                    entry => entry.AppUserId == context.User.Id && entry.ProviderKey == context.ProviderKey, cancellationToken)
                : null;

            return Results.Ok(new WatchHistoryAuthorizationResponse(
                outcome.State.ToString(),
                UserCode: null,
                VerificationUrl: null,
                ExpiresAt: null,
                outcome.RetryAfter is { } retry ? (int)Math.Ceiling(retry.TotalSeconds) : null,
                ToResponse(connection)));
        });

        group.MapGet("/connections/{providerKey}", async (
            string providerKey,
            ClaimsPrincipal principal,
            IWatchHistoryProviderRegistry registry,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            var context = await ResolveAsync(principal, providerKey, registry, database, cancellationToken);
            if (context.Problem is not null)
            {
                return context.Problem;
            }

            var connection = await database.WatchHistoryConnections.AsNoTracking().FirstOrDefaultAsync(
                entry => entry.AppUserId == context.User!.Id && entry.ProviderKey == context.ProviderKey, cancellationToken);

            return connection is null ? Results.NotFound() : Results.Ok(ToResponse(connection));
        });

        group.MapDelete("/connections/{providerKey}", async (
            string providerKey,
            ClaimsPrincipal principal,
            IWatchHistoryProviderRegistry registry,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            var context = await ResolveAsync(principal, providerKey, registry, database, cancellationToken);
            if (context.Problem is not null)
            {
                return context.Problem;
            }

            // Idempotent: disconnecting something already disconnected is a no-op, not a 404. The
            // caller's intent — "I should not be connected" — is satisfied either way.
            await context.Authorization!.DisconnectAsync(context.User!.Id, cancellationToken);
            return Results.NoContent();
        });
    }

    private sealed record ResolvedContext(
        AppUser? User, IWatchHistoryProviderAuthorization? Authorization, string ProviderKey, IResult? Problem);

    private static async Task<ResolvedContext> ResolveAsync(
        ClaimsPrincipal principal,
        string providerKey,
        IWatchHistoryProviderRegistry registry,
        MediaServerDbContext database,
        CancellationToken cancellationToken)
    {
        var user = await ResolveUserAsync(principal, database, cancellationToken);
        if (user is null)
        {
            return new ResolvedContext(null, null, providerKey, Results.Unauthorized());
        }

        var authorization = registry.FindAuthorization(providerKey);
        if (authorization is null)
        {
            return new ResolvedContext(user, null, providerKey, Results.NotFound());
        }

        if (!authorization.IsConfigured)
        {
            // The operator has not supplied this provider's application settings. Distinct from "no
            // such provider" so Settings can say which of the two it is, and 409 rather than 400
            // because nothing about the request is wrong.
            return new ResolvedContext(user, authorization, authorization.ProviderKey, Results.Problem(
                title: "Provider not configured",
                detail: "This provider has not been configured for this instance.",
                statusCode: StatusCodes.Status409Conflict));
        }

        return new ResolvedContext(user, authorization, authorization.ProviderKey, null);
    }

    internal static IResult ToProblem(WatchHistoryFailure failure, string? detail) => failure switch
    {
        WatchHistoryFailure.Unsupported => Results.Problem(
            title: "Provider not configured", detail: detail, statusCode: StatusCodes.Status409Conflict),
        WatchHistoryFailure.IdentityRejected => Results.Problem(
            title: "No authorization in progress", detail: detail, statusCode: StatusCodes.Status409Conflict),
        WatchHistoryFailure.AuthenticationRequired => Results.Problem(
            title: "Reconnect required", detail: detail, statusCode: StatusCodes.Status409Conflict),
        WatchHistoryFailure.RateLimited => Results.Problem(
            title: "Provider rate limit reached", detail: detail, statusCode: StatusCodes.Status429TooManyRequests),
        // Transient and contract failures are the provider's fault, not the caller's: 502 says "try
        // again", where a 500 would suggest this app is broken.
        _ => Results.Problem(
            title: "Provider unavailable", detail: detail, statusCode: StatusCodes.Status502BadGateway),
    };

    internal static WatchHistoryConnectionResponse? ToResponse(WatchHistoryProviderConnection? connection) =>
        connection is null
            ? null
            : new WatchHistoryConnectionResponse(
                connection.ProviderKey,
                connection.Status.ToString(),
                connection.ProviderAccountName,
                connection.ConnectedAt,
                connection.LastDeliveryAt,
                connection.LastSyncAt,
                connection.LastError);

    private static async Task<AppUser?> ResolveUserAsync(
        ClaimsPrincipal principal, MediaServerDbContext database, CancellationToken cancellationToken)
    {
        var hostUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(hostUserId))
        {
            return null;
        }

        return await database.AppUsers.FirstOrDefaultAsync(user => user.HostUserId == hostUserId, cancellationToken);
    }
}
