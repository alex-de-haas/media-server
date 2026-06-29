using System.Security.Claims;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Library;

/// <summary>
/// The internal <c>/api/library</c> surface for the UI: browse, detail, and episode listings, each
/// carrying the caller's per-user playback state. Projects the domain via <see cref="LibraryReadService"/>
/// (camelCase JSON); it never reaches into the Jellyfin surface.
/// </summary>
public static class LibraryEndpoints
{
    public static void MapLibraryEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/library").RequireAuthorization();

        group.MapGet("/", async (
            Guid? catalogId,
            string? kind,
            ClaimsPrincipal principal,
            LibraryReadService library,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            var appUserId = await ResolveAppUserIdAsync(principal, database, cancellationToken);
            var items = await library.ListAsync(catalogId, ParseKind(kind), appUserId, cancellationToken);
            return Results.Ok(items);
        });

        group.MapGet("/{id:guid}", async (
            Guid id,
            ClaimsPrincipal principal,
            LibraryReadService library,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            var appUserId = await ResolveAppUserIdAsync(principal, database, cancellationToken);
            var detail = await library.GetDetailAsync(id, appUserId, cancellationToken);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        group.MapGet("/{id:guid}/episodes", async (
            Guid id,
            Guid? seasonId,
            ClaimsPrincipal principal,
            LibraryReadService library,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            var appUserId = await ResolveAppUserIdAsync(principal, database, cancellationToken);
            var episodes = await library.GetEpisodesAsync(id, seasonId, appUserId, cancellationToken);
            return Results.Ok(episodes);
        });

        // Home rails.
        group.MapGet("/recent", async (
            int? limit, ClaimsPrincipal principal, LibraryReadService library, MediaServerDbContext database, CancellationToken cancellationToken) =>
        {
            var appUserId = await ResolveAppUserIdAsync(principal, database, cancellationToken);
            return Results.Ok(await library.GetRecentAsync(limit ?? 20, appUserId, cancellationToken));
        });

        group.MapGet("/resume", async (
            int? limit, ClaimsPrincipal principal, LibraryReadService library, MediaServerDbContext database, CancellationToken cancellationToken) =>
        {
            var appUserId = await ResolveAppUserIdAsync(principal, database, cancellationToken);
            return appUserId is { } userId
                ? Results.Ok(await library.GetResumeAsync(userId, limit ?? 20, cancellationToken))
                : Results.Ok(Array.Empty<LibraryRailItemDto>());
        });

        group.MapGet("/nextup", async (
            int? limit, ClaimsPrincipal principal, LibraryReadService library, MediaServerDbContext database, CancellationToken cancellationToken) =>
        {
            var appUserId = await ResolveAppUserIdAsync(principal, database, cancellationToken);
            return appUserId is { } userId
                ? Results.Ok(await library.GetNextUpAsync(userId, limit ?? 20, cancellationToken))
                : Results.Ok(Array.Empty<LibraryRailItemDto>());
        });

        // Per-user playback-state mutations (return the updated user data).
        group.MapPost("/{id:guid}/played", (Guid id, ClaimsPrincipal principal, UserDataService userData, MediaServerDbContext database, CancellationToken cancellationToken) =>
            SetPlayedAsync(id, played: true, principal, userData, database, cancellationToken));
        group.MapDelete("/{id:guid}/played", (Guid id, ClaimsPrincipal principal, UserDataService userData, MediaServerDbContext database, CancellationToken cancellationToken) =>
            SetPlayedAsync(id, played: false, principal, userData, database, cancellationToken));
        group.MapPost("/{id:guid}/favorite", (Guid id, ClaimsPrincipal principal, UserDataService userData, MediaServerDbContext database, CancellationToken cancellationToken) =>
            SetFavoriteAsync(id, favorite: true, principal, userData, database, cancellationToken));
        group.MapDelete("/{id:guid}/favorite", (Guid id, ClaimsPrincipal principal, UserDataService userData, MediaServerDbContext database, CancellationToken cancellationToken) =>
            SetFavoriteAsync(id, favorite: false, principal, userData, database, cancellationToken));

        // Delete a published item (admin only). `deleteFiles=true` also removes the library/ hardlinks.
        group.MapDelete("/{id:guid}", async (Guid id, bool? deleteFiles, LibraryDeleteService deleteService, CancellationToken cancellationToken) =>
        {
            var deleted = await deleteService.DeleteAsync(id, deleteFiles ?? false, cancellationToken);
            return deleted ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization(AppRoles.AdminPolicy);

        // Delete a single media source / version (admin only). `deleteFile=true` also erases the file from
        // disk — used to drop the original after a verified transcode "replace".
        group.MapDelete("/sources/{sourceId:guid}", async (Guid sourceId, bool? deleteFile, LibraryDeleteService deleteService, CancellationToken cancellationToken) =>
        {
            var deleted = await deleteService.DeleteSourceAsync(sourceId, deleteFile ?? false, cancellationToken);
            return deleted ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization(AppRoles.AdminPolicy);

        // Pin (or clear, with sourceId=null) the version that plays by default — clients honor the first
        // MediaSource, so this reorders the sources (admin only).
        group.MapPut("/{id:guid}/default-source", async (Guid id, SetDefaultSourceRequest request, LibrarySourceService sources, CancellationToken cancellationToken) =>
        {
            var ok = await sources.SetDefaultSourceAsync(id, request.SourceId, cancellationToken);
            return ok ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization(AppRoles.AdminPolicy);

        // Rename (or clear, with versionName=null) a single movie source's version — renaming the file on disk
        // to "Title (Year) - {version}.ext" and syncing the stored label (admin only).
        group.MapPut("/sources/{sourceId:guid}/version", async (Guid sourceId, SetVersionRequest request, LibrarySourceService sources, CancellationToken cancellationToken) =>
        {
            var result = await sources.RenameVersionAsync(sourceId, request.VersionName, cancellationToken);
            return result.Status switch
            {
                RenameVersionResult.Kind.Ok => Results.NoContent(),
                RenameVersionResult.Kind.Unsupported => Results.Problem(detail: result.Error, statusCode: 400),
                RenameVersionResult.Kind.InvalidName => Results.Problem(detail: result.Error, statusCode: 400),
                RenameVersionResult.Kind.Conflict => Results.Problem(detail: result.Error, statusCode: 409),
                RenameVersionResult.Kind.MissingFile => Results.Problem(detail: result.Error, statusCode: 409),
                _ => Results.NotFound(),
            };
        }).RequireAuthorization(AppRoles.AdminPolicy);

        // Re-fetch provider metadata + images for one item (admin only).
        group.MapPost("/{id:guid}/refresh", async (Guid id, LibraryMaintenanceService maintenance, CancellationToken cancellationToken) =>
        {
            var refreshed = await maintenance.RefreshMetadataAsync(id, cancellationToken);
            return refreshed ? Results.Accepted() : Results.NotFound();
        }).RequireAuthorization(AppRoles.AdminPolicy);

        // Re-probe the item's media files and replace its stored streams (admin only).
        group.MapPost("/{id:guid}/refresh-media", async (Guid id, LibraryMaintenanceService maintenance, CancellationToken cancellationToken) =>
        {
            var refreshed = await maintenance.RefreshMediaAsync(id, cancellationToken);
            return refreshed ? Results.Accepted() : Results.NotFound();
        }).RequireAuthorization(AppRoles.AdminPolicy);

        // Reassign a misidentified leaf (movie/episode) to a corrected identity and rebuild its hardlink (admin only).
        group.MapPost("/{id:guid}/remap", async (Guid id, RemapRequest request, RemapService remap, CancellationToken cancellationToken) =>
        {
            var result = await remap.RemapAsync(id, request, cancellationToken);
            return result.Status switch
            {
                RemapResult.Kind.Ok => Results.Ok(new { id = result.TargetId }),
                RemapResult.Kind.Unsupported => Results.BadRequest(new { error = "Only a movie, video, or episode can be remapped." }),
                RemapResult.Kind.NoSource => Results.BadRequest(new { error = "This item has no media file to remap." }),
                RemapResult.Kind.MissingFile => Results.Conflict(new { error = "The media file is missing on disk." }),
                _ => Results.NotFound(),
            };
        }).RequireAuthorization(AppRoles.AdminPolicy);

        // Scan all online catalogs for missing library files (admin only); also runs on a timer.
        group.MapPost("/scan", async (LibraryMaintenanceService maintenance, CancellationToken cancellationToken) =>
            Results.Ok(await maintenance.ScanAsync(cancellationToken)))
            .RequireAuthorization(AppRoles.AdminPolicy);
    }

    private static MediaKind? ParseKind(string? kind) =>
        Enum.TryParse<MediaKind>(kind, ignoreCase: true, out var parsed) ? parsed : null;

    /// <summary>Maps the validated Host principal to the internal app user id (null if not yet provisioned).</summary>
    private static async Task<int?> ResolveAppUserIdAsync(
        ClaimsPrincipal principal, MediaServerDbContext database, CancellationToken cancellationToken)
    {
        var hostUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(hostUserId))
        {
            return null;
        }

        return await database.AppUsers.AsNoTracking()
            .Where(user => user.HostUserId == hostUserId)
            .Select(user => (int?)user.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static async Task<IResult> SetPlayedAsync(
        Guid id, bool played, ClaimsPrincipal principal, UserDataService userData, MediaServerDbContext database, CancellationToken cancellationToken)
    {
        if (await ResolveAppUserIdAsync(principal, database, cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var data = await userData.SetPlayedAsync(userId, id, played, null, cancellationToken);
        return data is null ? Results.NotFound() : Results.Ok(data);
    }

    private static async Task<IResult> SetFavoriteAsync(
        Guid id, bool favorite, ClaimsPrincipal principal, UserDataService userData, MediaServerDbContext database, CancellationToken cancellationToken)
    {
        if (await ResolveAppUserIdAsync(principal, database, cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var data = await userData.SetFavoriteAsync(userId, id, favorite, cancellationToken);
        return data is null ? Results.NotFound() : Results.Ok(data);
    }
}
