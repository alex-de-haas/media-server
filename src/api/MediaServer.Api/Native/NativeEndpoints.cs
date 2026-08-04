using System.Security.Claims;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.Jellyfin;
using MediaServer.Api.Library;
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
                CoreOrigin: hosty.CorePublicOrigin)));

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
            .RequireAuthorization();

        group.MapNativeMediaEndpoints();
        group.MapNativeDiscoveryEndpoints();

        group.MapGet("/items/{id:guid}", async (
            Guid id,
            ClaimsPrincipal principal,
            MediaServerDbContext database,
            LibraryReadService library,
            NativeUrlTokenService tokens,
            CancellationToken cancellationToken) =>
        {
            if (await NativePrincipal.AppUserIdAsync(principal, database, cancellationToken) is not { } appUserId)
            {
                return Results.Unauthorized();
            }

            var detail = await library.GetDetailAsync(id, appUserId, cancellationToken);
            return detail is null
                ? Results.NotFound()
                : Results.Ok(new NativeItemDto(detail, NativeItemUrls.Build(detail, appUserId, tokens)));
        }).RequireAuthorization();

        group.MapGet("/sync", async (
            string? cursor,
            ClaimsPrincipal principal,
            MediaServerDbContext database,
            NativeSyncService sync,
            CancellationToken cancellationToken) =>
        {
            if (await NativePrincipal.AppUserIdAsync(principal, database, cancellationToken) is not { } appUserId)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(await sync.SyncAsync(cursor, appUserId, cancellationToken));
        }).RequireAuthorization();
    }
}

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
