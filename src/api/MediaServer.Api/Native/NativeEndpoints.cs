using System.Security.Claims;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.Jellyfin;
using MediaServer.Api.Library;
using MediaServer.Api.Native.Playback;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Native;

/// <summary>
/// The first-party client surface, versioned in the path so a client pinned to v1 keeps working when
/// v2 appears. Authentication is Hosty's own — the app writes none — so these routes sit on the same
/// scheme the rest of <c>/api</c> uses, and only the bootstrap route is anonymous.
/// See <c>docs/features/native-client-api/plan.md</c>.
/// </summary>
public static class NativeEndpoints
{
    public const string RoutePrefix = "/native/v1";

    public static void MapNativeEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup(RoutePrefix).AllowPublic();

        // Anonymous, and deliberately thin. A client that has never paired holds no token, so putting
        // the Core origin behind the token would mean needing a token to discover where tokens come
        // from. It therefore carries nothing about the library, the users, or which integrations are
        // configured — only what a client needs to begin. The Jellyfin surface splits the same way
        // (`/System/Info/Public` against `/System/Info`).
        group.MapGet("/server/public", (HostyOptions hosty, JellyfinServerContext server) =>
            Results.Ok(new NativeServerBootstrap(
                ServerName: server.ServerName,
                AppId: hosty.AppId,
                SurfaceVersion: NativeSurface.Version,
                CoreOrigin: hosty.CorePublicOrigin)))
            .Produces<NativeServerBootstrap>();

        // Authenticated, and the full answer: what this instance can actually do, so a client hides
        // what the server cannot do rather than failing on use.
        group.MapGet("/server", (HostyOptions hosty, JellyfinServerContext server, MediaServerSettings settings) =>
            Results.Ok(new NativeServerDescription(
                ServerName: server.ServerName,
                AppId: hosty.AppId,
                SurfaceVersion: NativeSurface.Version,
                CoreOrigin: hosty.CorePublicOrigin,
                Capabilities: new NativeServerCapabilities(
                    // The engine is an optional cross-app dependency: an injected URL is what decides
                    // whether it exists at all, exactly as the composition root reads it.
                    TranscodeEngine: !string.IsNullOrWhiteSpace(settings.TranscodeEngineUrl),
                    // Packaging is `remux-streaming`; until it ships the honest answer is false, and a
                    // client that keys off this simply never asks for a remux.
                    Packaging: false,
                    Recommendations: !string.IsNullOrWhiteSpace(settings.TmdbApiKey),
                    Trakt: settings.IsTraktConfigured))))
            .RequireAuthorization()
            .Produces<NativeServerDescription>();

        group.MapNativeMediaEndpoints();
        group.MapNativeImageEndpoints();
        group.MapNativeDiscoveryEndpoints();

        group.MapGet("/items/{id:guid}", async (
            Guid id,
            ClaimsPrincipal principal,
            MediaServerDbContext database,
            LibraryReadService library,
            NativeUrlTokenService tokens,
            CancellationToken cancellationToken) =>
        {
            if (await principal.ResolveAppUserIdAsync(database, cancellationToken) is not { } appUserId)
            {
                return Results.Unauthorized();
            }

            var detail = await library.GetDetailAsync(id, appUserId, cancellationToken);
            if (detail is null)
            {
                return Results.NotFound();
            }

            var images = await NativeImageEndpoints.BuildAsync(database, id, cancellationToken);
            return Results.Ok(new NativeItemDto(detail, NativeItemUrls.Build(detail, appUserId, tokens), images));
        }).RequireAuthorization().Produces<NativeItemDto>().Produces(StatusCodes.Status404NotFound);

        group.MapPost("/playback/resolve", async (
            NativePlaybackResolveRequest body,
            ClaimsPrincipal principal,
            MediaServerDbContext database,
            NativePlaybackResolver resolver,
            CancellationToken cancellationToken) =>
        {
            if (await principal.ResolveAppUserIdAsync(database, cancellationToken) is not { } appUserId)
            {
                return Results.Unauthorized();
            }

            var resolved = await resolver.ResolveAsync(body.ItemId, appUserId, body.Profile, cancellationToken);
            return resolved is null ? Results.NotFound() : Results.Ok(resolved);
        }).RequireAuthorization()
          .Produces<NativePlaybackResolutionResponse>()
          .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/playback/sessions/start", async (
            NativeSessionStart body,
            ClaimsPrincipal principal,
            MediaServerDbContext database,
            NativeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            if (await principal.ResolveAppUserIdAsync(database, cancellationToken) is not { } appUserId)
            {
                return Results.Unauthorized();
            }

            var playSessionId = await sessions.StartAsync(appUserId, body, cancellationToken);
            return playSessionId is null
                ? Results.NotFound()
                : Results.Ok(new NativeSessionStarted(playSessionId));
        }).RequireAuthorization()
          .Produces<NativeSessionStarted>()
          .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/playback/sessions/progress", async (
            NativeSessionReport body,
            ClaimsPrincipal principal,
            MediaServerDbContext database,
            NativeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            if (await principal.ResolveAppUserIdAsync(database, cancellationToken) is not { } appUserId)
            {
                return Results.Unauthorized();
            }

            return await sessions.ReportAsync(appUserId, body, isStopped: false, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound();
        }).RequireAuthorization()
          .Produces(StatusCodes.Status204NoContent)
          .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/playback/sessions/stop", async (
            NativeSessionReport body,
            ClaimsPrincipal principal,
            MediaServerDbContext database,
            NativeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            if (await principal.ResolveAppUserIdAsync(database, cancellationToken) is not { } appUserId)
            {
                return Results.Unauthorized();
            }

            return await sessions.ReportAsync(appUserId, body, isStopped: true, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound();
        }).RequireAuthorization()
          .Produces(StatusCodes.Status204NoContent)
          .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/playback/preferences", async (
            ClaimsPrincipal principal,
            MediaServerDbContext database,
            NativePreferenceService preferences,
            CancellationToken cancellationToken) =>
        {
            if (await principal.ResolveAppUserIdAsync(database, cancellationToken) is not { } appUserId)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(await preferences.ListAsync(appUserId, cancellationToken));
        }).RequireAuthorization().Produces<IReadOnlyList<NativePreferenceDto>>();

        group.MapPut("/playback/preferences", async (
            NativePreferenceDto body,
            ClaimsPrincipal principal,
            MediaServerDbContext database,
            NativePreferenceService preferences,
            CancellationToken cancellationToken) =>
        {
            if (await principal.ResolveAppUserIdAsync(database, cancellationToken) is not { } appUserId)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(await preferences.SetAsync(appUserId, body, cancellationToken));
        }).RequireAuthorization().Produces<NativePreferenceDto>();

        // The default is cleared by omitting the scope, a title's override by naming it.
        group.MapDelete("/playback/preferences", async (
            Guid? mediaItemId,
            ClaimsPrincipal principal,
            MediaServerDbContext database,
            NativePreferenceService preferences,
            CancellationToken cancellationToken) =>
        {
            if (await principal.ResolveAppUserIdAsync(database, cancellationToken) is not { } appUserId)
            {
                return Results.Unauthorized();
            }

            return await preferences.ClearAsync(appUserId, mediaItemId, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound();
        }).RequireAuthorization()
          .Produces(StatusCodes.Status204NoContent)
          .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/sync", async (
            string? cursor,
            ClaimsPrincipal principal,
            MediaServerDbContext database,
            NativeSyncService sync,
            CancellationToken cancellationToken) =>
        {
            if (await principal.ResolveAppUserIdAsync(database, cancellationToken) is not { } appUserId)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(await sync.SyncAsync(cursor, appUserId, cancellationToken));
        }).RequireAuthorization().Produces<NativeSyncPage>();
    }
}

/// <summary>What to resolve, and for which client.</summary>
public sealed record NativePlaybackResolveRequest(Guid ItemId, NativeCapabilityProfile Profile);

public static class NativeSurface
{
    /// <summary>The surface version, distinct from the app's release version.</summary>
    public const string Version = "1";

    /// <summary>Name of the OpenAPI document describing this surface, served at /openapi/native.json.</summary>
    public const string OpenApiDocumentName = "native";
}

/// <summary>Anonymous bootstrap: enough to find Core and start pairing, and nothing else.</summary>
public sealed record NativeServerBootstrap(
    string ServerName,
    string AppId,
    string SurfaceVersion,
    string? CoreOrigin);

public sealed record NativeServerDescription(
    string ServerName,
    string AppId,
    string SurfaceVersion,
    string? CoreOrigin,
    NativeServerCapabilities Capabilities);

public sealed record NativeServerCapabilities(
    bool TranscodeEngine,
    bool Packaging,
    bool Recommendations,
    bool Trakt);
