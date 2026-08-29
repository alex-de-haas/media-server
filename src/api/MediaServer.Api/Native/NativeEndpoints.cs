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
                CoreOrigin: hosty.CorePublicOrigin,
                // Whitespace is not an origin. The contract says null means "the host has no public
                // origin for this app", and a client falls back on that — so a blank setting must read
                // as absent rather than as an origin nothing will ever match.
                PairingOrigin: string.IsNullOrWhiteSpace(hosty.JellyfinPublicOrigin)
                    ? null
                    : hosty.JellyfinPublicOrigin)))
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
                    // Packaging ships. It needs no engine and no configuration — an index is built in
                    // the background for every Matroska source — so this is a property of the build
                    // rather than of the deployment. Whether a *particular* source is ready is a
                    // different question, and `resolve` answers it per source.
                    Packaging: true,
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
            MediaServerSettings settings,
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

            var images = await NativeImageEndpoints.BuildAsync(database, id, settings.PreferredLanguage, cancellationToken);
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

            var resolved = await resolver.ResolveAsync(
                body.ItemId, appUserId, body.Profile, body.AudioStreamId, body.SubtitleStreamId,
                body.SubtitlesOff, cancellationToken);
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

            var saved = await preferences.SetAsync(appUserId, body, cancellationToken);
            return saved is null ? Results.NotFound() : Results.Ok(saved);
        }).RequireAuthorization()
          .Produces<NativePreferenceDto>()
          .Produces(StatusCodes.Status404NotFound);

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
/// <param name="AudioStreamId">
/// The track a viewer has just picked. Absent means "decide for me", and the decision is their stored
/// preference — so the first resolve of a film needs no choice and a switch mid-film is one field.
/// </param>
/// <param name="SubtitleStreamId">
/// The same for words, with the same "absent means decide for me". Turning subtitles <em>off</em> is a
/// different thing and has its own field.
/// </param>
/// <param name="SubtitlesOff">
/// No subtitles, whatever the preference says. Absent and "none" cannot be one value: a viewer whose
/// preference names a language would otherwise be handed it straight back, and the Off row in their
/// picker would do nothing whatever.
/// </param>
public sealed record NativePlaybackResolveRequest(
    Guid ItemId,
    NativeCapabilityProfile Profile,
    Guid? AudioStreamId = null,
    Guid? SubtitleStreamId = null,
    bool SubtitlesOff = false);

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
    string? CoreOrigin,

    /// <summary>
    /// The origin Core has installed for this app, which is the only thing it will accept as a
    /// <c>redirectUri</c> when a device asks to be authorised.
    ///
    /// It is told rather than assumed because the address a viewer types need not be one Core has ever
    /// heard of. A television reaching a server across the room types its address on this network, and
    /// Core checks the redirect against the app's *installed* endpoint origins — so pairing against a
    /// local address fails at the last step, after the code has already been approved, which is the
    /// most expensive moment to fail at.
    ///
    /// Null when the host has no public origin for this app; a client then falls back to the address it
    /// was given, which is what every pairing did before this existed.
    /// </summary>
    string? PairingOrigin = null);

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
