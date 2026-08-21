using System.Security.Claims;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
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
    string? LastError,
    // Favorites, when this provider carries them: how full its list is and the cap it enforces (Trakt
    // allows 100 for every account), plus the works whose favorite could not be delivered — a full list
    // among them. Null counts mean no favorites call has run yet.
    bool SupportsFavorites,
    int? FavoritesCount,
    int? FavoritesCapacity,
    IReadOnlyList<string> FavoriteSyncFailures);

/// <summary>What to show the user so they can approve the connection on the provider's site.</summary>
public sealed record WatchHistoryAuthorizationResponse(
    string State,
    string? UserCode,
    string? VerificationUrl,
    DateTimeOffset? ExpiresAt,
    int? PollIntervalSeconds,
    WatchHistoryConnectionResponse? Connection);

/// <summary>Which catalogs and media kinds a sync should touch. Empty on either axis means "all".</summary>
public sealed record WatchHistorySyncScopeRequest(
    IReadOnlyList<Guid>? CatalogIds,
    IReadOnlyList<WatchHistoryMediaKind>? Kinds);

/// <summary>The one previewed run the caller is choosing to apply.</summary>
public sealed record WatchHistorySyncApplyRequest(Guid RunId);

/// <summary>The instant to stamp on an undated mark.</summary>
public sealed record SetWatchedAtRequest(DateTimeOffset? WatchedAt);

/// <summary>
/// The read-only comparison the user approves before anything is written. Counts and sample carry
/// enum keys/values that serialize as their names, so the UI reads "RemoteOnly" rather than an index.
/// </summary>
public sealed record WatchHistorySyncPreviewResponse(
    Guid RunId,
    IReadOnlyDictionary<WatchHistorySyncClassification, int> Counts,
    IReadOnlyList<WatchHistorySyncEntry> Sample,
    bool HasPendingOutboundWork,
    bool HasTerminalOutboundWork,
    bool AggregateCountsMayCollapse);

/// <summary>
/// Internal UI endpoints (Hosty identity) for a signed-in user to read their own watched history and
/// manage their own provider connections.
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
            var user = await principal.ResolveAppUserAsync(database, cancellationToken);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var connections = await database.WatchHistoryConnections
                .AsNoTracking()
                .Where(entry => entry.AppUserId == user.Id)
                .ToListAsync(cancellationToken);

            // This is the route Settings actually loads, so the favorites fields have to be filled in
            // here — not only on the single-connection route — or the whole favorites UI stays hidden.
            var failuresByConnection = await FavoriteFailuresAsync(
                database, connections.Select(entry => entry.Id).ToList(), cancellationToken);

            var providers = registry.Describe()
                .Select(descriptor => new WatchHistoryProviderResponse(
                    descriptor.Key,
                    descriptor.DisplayName,
                    descriptor.IsConfigured,
                    descriptor.Capabilities.ExactTimestampWrites,
                    ToResponse(
                        connections.FirstOrDefault(entry =>
                            string.Equals(entry.ProviderKey, descriptor.Key, StringComparison.OrdinalIgnoreCase)),
                        registry.FindFavorites(descriptor.Key) is not null,
                        failuresByConnection)))
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

        group.MapGet("/calendar", async (
            DateTimeOffset from,
            DateTimeOffset toExclusive,
            ClaimsPrincipal principal,
            WatchHistoryCalendarService calendar,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            var user = await principal.ResolveAppUserAsync(database, cancellationToken);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            if (toExclusive <= from)
            {
                return Results.BadRequest(new { error = "'toExclusive' must be after 'from'." });
            }

            // Bounded so one request cannot ask the database to scan a decade of history. The client
            // asks for the visible grid, which never exceeds six weeks.
            if (toExclusive - from > WatchHistoryCalendarService.MaxRange)
            {
                return Results.BadRequest(new
                {
                    error = $"The requested range exceeds {WatchHistoryCalendarService.MaxRange.TotalDays:0} days.",
                });
            }

            return Results.Ok(await calendar.LoadAsync(user.Id, from, toExclusive, cancellationToken));
        });

        group.MapGet("/calendar/undated", async (
            MediaKind? kind,
            ClaimsPrincipal principal,
            WatchHistoryCalendarService calendar,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            var user = await principal.ResolveAppUserAsync(database, cancellationToken);
            return user is null
                ? Results.Unauthorized()
                : Results.Ok(await calendar.LoadUndatedAsync(user.Id, kind, cancellationToken));
        });

        // Sets when a play happened: a mark that was never timed takes its instant, and one timed
        // wrongly is moved to the right one. The play itself is untouched either way — nothing is
        // recorded and nothing is removed, so the item's play count does not move.
        group.MapPatch("/entries/{entryId:guid}", async (
            Guid entryId,
            SetWatchedAtRequest request,
            ClaimsPrincipal principal,
            WatchHistoryEntryService entries,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            var user = await principal.ResolveAppUserAsync(database, cancellationToken);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            if (request.WatchedAt is not { } watchedAt)
            {
                return Results.BadRequest(new { error = "'watchedAt' is required." });
            }

            return ToResult(await entries.SetWatchedAtAsync(user.Id, entryId, watchedAt, cancellationToken));
        });

        group.MapDelete("/entries/{entryId:guid}", async (
            Guid entryId,
            ClaimsPrincipal principal,
            WatchHistoryEntryService entries,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            var user = await principal.ResolveAppUserAsync(database, cancellationToken);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            // Not idempotent, unlike disconnecting: "delete this play" names a specific row, and
            // answering 204 for an id the caller does not own would confirm that it exists.
            return await entries.DeleteAsync(user.Id, entryId, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound();
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

            if (connection is null)
            {
                return Results.NotFound();
            }

            var failures = await FavoriteFailuresAsync(database, [connection.Id], cancellationToken);
            return Results.Ok(ToResponse(connection, registry.FindFavorites(context.ProviderKey!) is not null, failures));
        });

        group.MapPost("/connections/{providerKey}/sync/preview", async (
            string providerKey,
            WatchHistorySyncScopeRequest? request,
            ClaimsPrincipal principal,
            IWatchHistoryProviderRegistry registry,
            WatchHistorySyncPreviewService preview,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            var context = await ResolveAsync(principal, providerKey, registry, database, cancellationToken);
            if (context.Problem is not null)
            {
                return context.Problem;
            }

            var result = await preview.BuildAsync(context.User!.Id, ToScope(request), cancellationToken);
            return result.Succeeded
                ? Results.Ok(ToResponse(result.Value!))
                : ToProblem(result.Failure!.Value, result.Detail);
        });

        group.MapPost("/connections/{providerKey}/sync/apply", async (
            string providerKey,
            WatchHistorySyncApplyRequest request,
            ClaimsPrincipal principal,
            IWatchHistoryProviderRegistry registry,
            WatchHistorySyncApplyService apply,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            var context = await ResolveAsync(principal, providerKey, registry, database, cancellationToken);
            if (context.Problem is not null)
            {
                return context.Problem;
            }

            // The run carries its own scope and captured revisions; the caller only names which one.
            var result = await apply.ApplyAsync(context.User!.Id, request.RunId, cancellationToken);
            return result.Succeeded
                ? Results.Ok(result.Value!)
                : ToProblem(result.Failure!.Value, result.Detail);
        });

        // Favorites reconcile alongside the watched history but stand on their own: they compare works,
        // not plays, so they are their own preview and apply rather than a section of the history run.
        group.MapPost("/connections/{providerKey}/favorites/preview", async (
            string providerKey,
            ClaimsPrincipal principal,
            IWatchHistoryProviderRegistry registry,
            FavoritesSyncService favorites,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            var context = await ResolveAsync(principal, providerKey, registry, database, cancellationToken);
            if (context.Problem is not null)
            {
                return context.Problem;
            }

            var result = await favorites.PreviewAsync(context.User!.Id, context.ProviderKey!, cancellationToken);
            return result.Succeeded ? Results.Ok(result.Value!) : ToProblem(result.Failure!.Value, result.Detail);
        });

        group.MapPost("/connections/{providerKey}/favorites/apply", async (
            string providerKey,
            ClaimsPrincipal principal,
            IWatchHistoryProviderRegistry registry,
            FavoritesSyncService favorites,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            var context = await ResolveAsync(principal, providerKey, registry, database, cancellationToken);
            if (context.Problem is not null)
            {
                return context.Problem;
            }

            var result = await favorites.ApplyAsync(context.User!.Id, context.ProviderKey!, cancellationToken);
            return result.Succeeded ? Results.Ok(result.Value!) : ToProblem(result.Failure!.Value, result.Detail);
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

    /// <summary>A null request, or empty on either axis, means "everything" — the same as an unscoped sync.</summary>
    internal static WatchHistorySyncScope ToScope(WatchHistorySyncScopeRequest? request) =>
        request is null
            ? WatchHistorySyncScope.Everything
            : new WatchHistorySyncScope(request.CatalogIds ?? [], request.Kinds ?? []);

    internal static WatchHistorySyncPreviewResponse ToResponse(WatchHistorySyncPreview preview) => new(
        preview.RunId,
        preview.Counts,
        preview.Sample,
        preview.HasPendingOutboundWork,
        preview.HasTerminalOutboundWork,
        preview.AggregateCountsMayCollapse);

    private sealed record ResolvedContext(
        AppUser? User, IWatchHistoryProviderAuthorization? Authorization, string ProviderKey, IResult? Problem);

    private static async Task<ResolvedContext> ResolveAsync(
        ClaimsPrincipal principal,
        string providerKey,
        IWatchHistoryProviderRegistry registry,
        MediaServerDbContext database,
        CancellationToken cancellationToken)
    {
        var user = await principal.ResolveAppUserAsync(database, cancellationToken);
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

    /// <summary>
    /// What setting a play's time answers. A caller's own missing entry and someone else's entry are
    /// both 404 — the same boundary the deletion route enforces, so neither can be used to probe for the
    /// other. A future instant is 400 because the request itself is wrong, not the state.
    /// </summary>
    internal static IResult ToResult(SetWatchedAtStatus status) => status switch
    {
        SetWatchedAtStatus.Updated => Results.NoContent(),
        SetWatchedAtStatus.NotFound => Results.NotFound(),
        _ => Results.BadRequest(new { error = "'watchedAt' cannot be in the future." }),
    };

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

    /// <summary>
    /// Titles whose favorite push ended terminally — a full Trakt list is the common one — keyed by
    /// connection. Surfaced with the connection so Settings can name what never made it across, instead
    /// of leaving the failure buried in the outbox.
    /// </summary>
    private static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> FavoriteFailuresAsync(
        MediaServerDbContext database, IReadOnlyList<Guid> connectionIds, CancellationToken cancellationToken)
    {
        if (connectionIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<string>>();
        }

        var rows = await database.WatchHistoryOutboxEvents.AsNoTracking()
            .Where(item => connectionIds.Contains(item.ConnectionId) &&
                item.Status == WatchHistoryOutboxStatus.Terminal &&
                (item.Operation == WatchHistoryOutboxOperation.AddFavorite ||
                 item.Operation == WatchHistoryOutboxOperation.RemoveFavorite))
            .OrderByDescending(item => item.CreatedAt)
            .Take(50)
            .Join(database.MediaItems.AsNoTracking(), item => item.MediaItemId, media => media.Id,
                (item, media) => new { item.ConnectionId, media.Title })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.ConnectionId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group.Select(row => row.Title).Distinct().ToList());
    }

    internal static WatchHistoryConnectionResponse? ToResponse(
        WatchHistoryProviderConnection? connection,
        bool supportsFavorites = false,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>>? favoriteFailures = null) =>
        connection is null
            ? null
            : new WatchHistoryConnectionResponse(
                connection.ProviderKey,
                connection.Status.ToString(),
                connection.ProviderAccountName,
                connection.ConnectedAt,
                connection.LastDeliveryAt,
                connection.LastSyncAt,
                connection.LastError,
                supportsFavorites,
                connection.FavoritesRemoteCount,
                connection.FavoritesCapacity,
                favoriteFailures?.GetValueOrDefault(connection.Id) ?? []);
}
