using System.Security.Claims;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.Jellyfin;

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

        // Search and window are additive and the shape is unchanged: a caller passing nothing new still
        // receives the whole list, ordered the way the cards render. Anything else routes through the
        // SQL-ordered search, whose total the MCP surface reads by calling the service in-process.
        group.MapGet("/", async (
            Guid? catalogId,
            string? kind,
            string? q,
            bool? watched,
            int? limit,
            int? offset,
            ClaimsPrincipal principal,
            LibraryReadService library,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            var appUserId = await principal.ResolveAppUserIdAsync(database, cancellationToken);
            if (string.IsNullOrWhiteSpace(q) && watched is null && limit is null && offset is null)
            {
                return Results.Ok(await library.ListAsync(catalogId, ParseKind(kind), appUserId, cancellationToken));
            }

            var page = await library.SearchAsync(
                new LibrarySearchQuery(catalogId, ParseKind(kind), q, watched, limit, offset),
                appUserId,
                cancellationToken);
            return Results.Ok(page.Items);
        });

        group.MapGet("/{id:guid}", async (
            Guid id,
            ClaimsPrincipal principal,
            LibraryReadService library,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            var appUserId = await principal.ResolveAppUserIdAsync(database, cancellationToken);
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
            var appUserId = await principal.ResolveAppUserIdAsync(database, cancellationToken);
            var episodes = await library.GetEpisodesAsync(id, seasonId, appUserId, cancellationToken);
            return Results.Ok(episodes);
        });

        // Home rails.
        group.MapGet("/recent", async (
            int? limit, ClaimsPrincipal principal, LibraryReadService library, MediaServerDbContext database, CancellationToken cancellationToken) =>
        {
            var appUserId = await principal.ResolveAppUserIdAsync(database, cancellationToken);
            return Results.Ok(await library.GetRecentAsync(limit ?? 20, appUserId, cancellationToken));
        });

        group.MapGet("/resume", async (
            int? limit, ClaimsPrincipal principal, LibraryReadService library, MediaServerDbContext database, CancellationToken cancellationToken) =>
        {
            var appUserId = await principal.ResolveAppUserIdAsync(database, cancellationToken);
            return appUserId is { } userId
                ? Results.Ok(await library.GetResumeAsync(userId, limit ?? 20, cancellationToken))
                : Results.Ok(Array.Empty<LibraryRailItemDto>());
        });

        group.MapGet("/nextup", async (
            int? limit, ClaimsPrincipal principal, LibraryReadService library, MediaServerDbContext database, CancellationToken cancellationToken) =>
        {
            var appUserId = await principal.ResolveAppUserIdAsync(database, cancellationToken);
            return appUserId is { } userId
                ? Results.Ok(await library.GetNextUpAsync(userId, limit ?? 20, cancellationToken))
                : Results.Ok(Array.Empty<LibraryRailItemDto>());
        });

        // Per-user playback-state mutations (return the updated user data).
        group.MapPost("/{id:guid}/played", (Guid id, ClaimsPrincipal principal, UserDataService userData, MediaServerDbContext database, PlaybackDiagnostics diagnostics, CancellationToken cancellationToken) =>
            SetPlayedAsync(id, played: true, principal, userData, database, diagnostics, cancellationToken));
        group.MapDelete("/{id:guid}/played", (Guid id, ClaimsPrincipal principal, UserDataService userData, MediaServerDbContext database, PlaybackDiagnostics diagnostics, CancellationToken cancellationToken) =>
            SetPlayedAsync(id, played: false, principal, userData, database, diagnostics, cancellationToken));
        // Records a viewing the server never saw, at the instant the user names — the toggle above
        // cannot, because it claims no time and lands outside the calendar by design.
        group.MapPost("/{id:guid}/watches", async (
            Guid id,
            LogWatchRequest request,
            ClaimsPrincipal principal,
            UserDataService userData,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            if (await principal.ResolveAppUserIdAsync(database, cancellationToken) is not { } userId)
            {
                return Results.Unauthorized();
            }

            if (request.WatchedAt is not { } watchedAt)
            {
                return Results.BadRequest(new { error = "'watchedAt' is required." });
            }

            return ToResult(await userData.LogWatchAsync(userId, id, watchedAt, cancellationToken));
        });

        group.MapPost("/{id:guid}/favorite", (Guid id, ClaimsPrincipal principal, UserDataService userData, MediaServerDbContext database, CancellationToken cancellationToken) =>
            SetFavoriteAsync(id, favorite: true, principal, userData, database, cancellationToken));
        group.MapDelete("/{id:guid}/favorite", (Guid id, ClaimsPrincipal principal, UserDataService userData, MediaServerDbContext database, CancellationToken cancellationToken) =>
            SetFavoriteAsync(id, favorite: false, principal, userData, database, cancellationToken));

        // The 1-5 star verdict on a watched work — a separate system from the favorite above, which is
        // curation. DELETE clears it back to unrated, which is a different statement from one star.
        group.MapPut("/{id:guid}/rating", (Guid id, SetRatingRequest request, ClaimsPrincipal principal, UserDataService userData, MediaServerDbContext database, CancellationToken cancellationToken) =>
            SetRatingAsync(id, request.Rating, principal, userData, database, cancellationToken));
        group.MapDelete("/{id:guid}/rating", (Guid id, ClaimsPrincipal principal, UserDataService userData, MediaServerDbContext database, CancellationToken cancellationToken) =>
            SetRatingAsync(id, rating: null, principal, userData, database, cancellationToken));

        // The removed-titles surface: tombstoned movies/series with the signed-in user's signal summary,
        // clearing one's own favorite on a ghost, and the retroactive full purge (admin).
        group.MapGet("/removed", async (
            ClaimsPrincipal principal, RemovedTitlesService removed, MediaServerDbContext database, CancellationToken cancellationToken) =>
        {
            var appUserId = await principal.ResolveAppUserIdAsync(database, cancellationToken);
            return Results.Ok(await removed.ListAsync(appUserId, cancellationToken));
        });

        group.MapDelete("/removed/{id:guid}/favorite", async (
            Guid id, ClaimsPrincipal principal, RemovedTitlesService removed, MediaServerDbContext database, CancellationToken cancellationToken) =>
        {
            var appUserId = await principal.ResolveAppUserIdAsync(database, cancellationToken);
            if (appUserId is not { } userId)
            {
                return Results.Unauthorized();
            }

            return await removed.ClearFavoriteAsync(userId, id, cancellationToken) ? Results.NoContent() : Results.NotFound();
        });

        group.MapDelete("/removed/{id:guid}/rating", async (
            Guid id, ClaimsPrincipal principal, RemovedTitlesService removed, MediaServerDbContext database, CancellationToken cancellationToken) =>
        {
            var appUserId = await principal.ResolveAppUserIdAsync(database, cancellationToken);
            if (appUserId is not { } userId)
            {
                return Results.Unauthorized();
            }

            return await removed.ClearRatingAsync(userId, id, cancellationToken) ? Results.NoContent() : Results.NotFound();
        });

        group.MapDelete("/removed/{id:guid}", async (
            Guid id, LibraryDeleteService deleteService, CancellationToken cancellationToken) =>
        {
            var purged = await deleteService.PurgeRemovedAsync(id, cancellationToken);
            return purged ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization(AppRoles.AdminPolicy);

        // Delete a published item (admin only). `deleteFiles=true` also removes the library/ hardlinks.
        // `deleteUserData=true` forces a full purge; without it, an item with user signal (favorite,
        // watched state, history) survives as a tombstone. Refused while the item is moving between
        // catalogs — the move is relocating the very files/rows.
        group.MapDelete("/{id:guid}", async (Guid id, bool? deleteFiles, bool? deleteUserData, LibraryDeleteService deleteService, LibraryMoveGuard moveGuard, CancellationToken cancellationToken) =>
        {
            if (await moveGuard.IsItemMovingAsync(id, cancellationToken))
            {
                return Results.Conflict(new { error = LibraryMoveGuard.MoveInProgressError });
            }

            var deleted = await deleteService.DeleteAsync(id, deleteFiles ?? false, deleteUserData ?? false, cancellationToken);
            return deleted ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization(AppRoles.AdminPolicy);

        // Delete one episode, or a whole season, of a published series (admin only). `deleteFiles=true` also
        // erases the files from disk; `deleteUserData=true` as above. The response reports what the delete
        // emptied and pruned, so the caller knows when the series page it came from no longer exists.
        group.MapDelete("/episodes/{id:guid}", async (Guid id, bool? deleteFiles, bool? deleteUserData, LibraryDeleteService deleteService, LibraryMoveGuard moveGuard, CancellationToken cancellationToken) =>
        {
            if (await moveGuard.IsItemMovingAsync(id, cancellationToken))
            {
                return Results.Conflict(new { error = LibraryMoveGuard.MoveInProgressError });
            }

            var result = await deleteService.DeleteEpisodeAsync(id, deleteFiles ?? false, deleteUserData ?? false, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequireAuthorization(AppRoles.AdminPolicy);

        group.MapDelete("/seasons/{id:guid}", async (Guid id, bool? deleteFiles, bool? deleteUserData, LibraryDeleteService deleteService, LibraryMoveGuard moveGuard, CancellationToken cancellationToken) =>
        {
            if (await moveGuard.IsItemMovingAsync(id, cancellationToken))
            {
                return Results.Conflict(new { error = LibraryMoveGuard.MoveInProgressError });
            }

            var result = await deleteService.DeleteSeasonAsync(id, deleteFiles ?? false, deleteUserData ?? false, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequireAuthorization(AppRoles.AdminPolicy);

        // Delete a single media source / version (admin only). `deleteFile=true` also erases the file from
        // disk — used to drop the original after a verified transcode "replace".
        group.MapDelete("/sources/{sourceId:guid}", async (Guid sourceId, bool? deleteFile, LibraryDeleteService deleteService, LibraryMoveGuard moveGuard, CancellationToken cancellationToken) =>
        {
            if (await moveGuard.IsSourceMovingAsync(sourceId, cancellationToken))
            {
                return Results.Conflict(new { error = LibraryMoveGuard.MoveInProgressError });
            }

            var deleted = await deleteService.DeleteSourceAsync(sourceId, deleteFile ?? false, cancellationToken);
            return deleted ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization(AppRoles.AdminPolicy);

        // Delete one sidecar — an external audio track or subtitle sitting beside a library file (admin
        // only). `deleteFile=true` also erases it from disk; without it only the entry goes. Merging a track
        // into a video never removes its sidecar, so this is the deliberate act that does.
        group.MapDelete("/streams/{streamId:guid}", async (Guid streamId, bool? deleteFile, LibraryDeleteService deleteService, CancellationToken cancellationToken) =>
        {
            var deleted = await deleteService.DeleteExternalStreamAsync(streamId, deleteFile ?? false, cancellationToken);
            return deleted ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization(AppRoles.AdminPolicy);

        // Pin (or clear, with sourceId=null) the version that plays by default — clients honor the first
        // MediaSource, so this reorders the sources (admin only).
        group.MapPut("/{id:guid}/default-source", async (Guid id, SetDefaultSourceRequest request, LibrarySourceService sources, LibraryMoveGuard moveGuard, CancellationToken cancellationToken) =>
        {
            if (await moveGuard.IsItemMovingAsync(id, cancellationToken))
            {
                return Results.Conflict(new { error = LibraryMoveGuard.MoveInProgressError });
            }

            var ok = await sources.SetDefaultSourceAsync(id, request.SourceId, cancellationToken);
            return ok ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization(AppRoles.AdminPolicy);

        // Rename (or clear, with versionName=null) a single movie source's version — renaming the file on disk
        // to "Title (Year) - {version}.ext" and syncing the stored label (admin only).
        group.MapPut("/sources/{sourceId:guid}/version", async (Guid sourceId, SetVersionRequest request, LibrarySourceService sources, LibraryMoveGuard moveGuard, CancellationToken cancellationToken) =>
        {
            if (await moveGuard.IsSourceMovingAsync(sourceId, cancellationToken))
            {
                return Results.Conflict(new { error = LibraryMoveGuard.MoveInProgressError });
            }

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

        // The artwork this item holds, ranked as the surfaces rank it — the candidate list behind "Change
        // poster". Cached rows only, so it costs no provider request.
        group.MapGet("/{id:guid}/images", async (Guid id, ItemArtworkService artwork, CancellationToken cancellationToken) =>
            await artwork.ListAsync(id, cancellationToken) is { } images ? Results.Ok(images) : Results.NotFound());

        // Pin one of those posters, overriding the language ranking for this item (admin only).
        group.MapPut("/{id:guid}/poster", async (
            Guid id, PinPosterRequest request, ItemArtworkService artwork, CancellationToken cancellationToken) =>
            ToResult(await artwork.PinAsync(id, request.Tag, cancellationToken)))
            .RequireAuthorization(AppRoles.AdminPolicy);

        // Hand the choice back to the ranking (admin only).
        group.MapDelete("/{id:guid}/poster", async (Guid id, ItemArtworkService artwork, CancellationToken cancellationToken) =>
            await artwork.ClearAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(AppRoles.AdminPolicy);

        // Re-fetch provider metadata + images for one item (admin only).
        group.MapPost("/{id:guid}/refresh", async (Guid id, LibraryMaintenanceService maintenance, CancellationToken cancellationToken) =>
        {
            var refreshed = await maintenance.RefreshMetadataAsync(id, cancellationToken);
            return refreshed ? Results.Accepted() : Results.NotFound();
        }).RequireAuthorization(AppRoles.AdminPolicy);

        // Re-probe the item's media files and replace its stored streams (admin only). Refused mid-move: the
        // files are relocating, so the probe would race the copy and read paths that are about to change.
        group.MapPost("/{id:guid}/refresh-media", async (Guid id, LibraryMaintenanceService maintenance, LibraryMoveGuard moveGuard, CancellationToken cancellationToken) =>
        {
            if (await moveGuard.IsItemMovingAsync(id, cancellationToken))
            {
                return Results.Conflict(new { error = LibraryMoveGuard.MoveInProgressError });
            }

            var refreshed = await maintenance.RefreshMediaAsync(id, cancellationToken);
            return refreshed ? Results.Accepted() : Results.NotFound();
        }).RequireAuthorization(AppRoles.AdminPolicy);

        // Reassign a misidentified leaf (movie/episode) to a corrected identity and rebuild its hardlink (admin only).
        group.MapPost("/{id:guid}/remap", async (Guid id, RemapRequest request, RemapService remap, LibraryMoveGuard moveGuard, CancellationToken cancellationToken) =>
        {
            if (await moveGuard.IsItemMovingAsync(id, cancellationToken))
            {
                return Results.Conflict(new { error = LibraryMoveGuard.MoveInProgressError });
            }

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

        // Move a published top-level movie/series into another type-compatible catalog (admin only). Runs as a
        // background job (files are moved, ids re-minted); returns the job id so the UI can follow progress.
        group.MapPost("/{id:guid}/move", async (Guid id, LibraryMoveRequest request, LibraryMoveCoordinator coordinator, CancellationToken cancellationToken) =>
        {
            var result = await coordinator.RequestAsync(id, request.TargetCatalogId, cancellationToken);
            return result.Status switch
            {
                LibraryMoveRequestStatus.Started => Results.Accepted($"/api/library/{id}/move", new { jobId = result.JobId }),
                LibraryMoveRequestStatus.NotFound => Results.NotFound(),
                LibraryMoveRequestStatus.Unsupported => Results.BadRequest(new { error = "Only a published movie or series can be moved." }),
                LibraryMoveRequestStatus.SameCatalog => Results.BadRequest(new { error = "The item is already in that catalog." }),
                LibraryMoveRequestStatus.IncompatibleType => Results.BadRequest(new { error = "The target catalog's type is not compatible with this item." }),
                LibraryMoveRequestStatus.CatalogOffline => Results.Conflict(new { error = "The source or target catalog root is offline." }),
                LibraryMoveRequestStatus.InsufficientSpace => Results.Conflict(new { error = "Not enough free space in the target catalog for this move." }),
                LibraryMoveRequestStatus.AlreadyMoving => Results.Conflict(new { error = "A move is already in progress for this item." }),
                LibraryMoveRequestStatus.TranscodeActive => Results.Conflict(new { error = "A conversion is running for this item — wait for it to finish or cancel it first." }),
                _ => Results.NotFound(),
            };
        }).RequireAuthorization(AppRoles.AdminPolicy);

        // The moves currently in flight (admin only), for the UI to show progress.
        group.MapGet("/move/active", async (LibraryMoveCoordinator coordinator, CancellationToken cancellationToken) =>
            Results.Ok(await coordinator.ListActiveAsync(cancellationToken)))
            .RequireAuthorization(AppRoles.AdminPolicy);

    }

    /// <summary>
    /// What logging a watch answers. A folder is a 400 rather than a 404: the item exists, and telling
    /// the caller it does not would send them looking for the wrong bug.
    /// </summary>
    internal static IResult ToResult(LogWatchResult result) => result.Status switch
    {
        LogWatchStatus.Recorded => Results.Ok(result.Data),
        LogWatchStatus.ItemNotFound => Results.NotFound(),
        LogWatchStatus.NotPlayable => Results.BadRequest(new { error = "Only a playable item can have a watch logged against it." }),
        _ => Results.BadRequest(new { error = "'watchedAt' cannot be in the future." }),
    };

    private static MediaKind? ParseKind(string? kind) =>
        Enum.TryParse<MediaKind>(kind, ignoreCase: true, out var parsed) ? parsed : null;

    private static async Task<IResult> SetPlayedAsync(
        Guid id, bool played, ClaimsPrincipal principal, UserDataService userData, MediaServerDbContext database,
        PlaybackDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        // The Phase 0 matrix compares Infuse against the web player, so the web toggle is recorded
        // through the same instrument. The route kind stays PlayedItems*: it is the same intent, and
        // the absent playSessionId/itemId shape already distinguishes the surfaces.
        var userId = await principal.ResolveAppUserIdAsync(database, cancellationToken);
        diagnostics.BeginRequest(
            played ? PlaybackRouteKinds.PlayedItemsPost : PlaybackRouteKinds.PlayedItemsDelete,
            userId,
            id.ToString("N"),
            positionTicks: null,
            playSessionId: null,
            mediaSourceId: null,
            isPaused: null,
            isStopped: false,
            datePlayed: null,
            datePlayedSupplied: false);

        if (userId is null)
        {
            await diagnostics.CompleteAsync(StatusCodes.Status401Unauthorized, cancellationToken);
            return Results.Unauthorized();
        }

        var data = await userData.SetPlayedAsync(userId.Value, id, played, null, diagnostics, cancellationToken);
        await diagnostics.CompleteAsync(
            data is null ? StatusCodes.Status404NotFound : StatusCodes.Status200OK, cancellationToken);
        return data is null ? Results.NotFound() : Results.Ok(data);
    }

    private static async Task<IResult> SetFavoriteAsync(
        Guid id, bool favorite, ClaimsPrincipal principal, UserDataService userData, MediaServerDbContext database, CancellationToken cancellationToken)
    {
        if (await principal.ResolveAppUserIdAsync(database, cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var data = await userData.SetFavoriteAsync(userId, id, favorite, cancellationToken);
        return data is null ? Results.NotFound() : Results.Ok(data);
    }

    private static async Task<IResult> SetRatingAsync(
        Guid id, int? rating, ClaimsPrincipal principal, UserDataService userData, MediaServerDbContext database, CancellationToken cancellationToken)
    {
        if (await principal.ResolveAppUserIdAsync(database, cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        return ToResult(await userData.SetRatingAsync(userId, id, rating, cancellationToken));
    }

    internal static IResult ToResult(SetRatingResult result) => result.Status switch
    {
        SetRatingStatus.Applied => Results.Ok(result.Data),
        SetRatingStatus.ItemNotFound => Results.NotFound(),
        SetRatingStatus.NotRatable => Results.BadRequest(
            new { error = "Only movies and series can be rated." }),
        SetRatingStatus.OutOfRange => Results.BadRequest(
            new { error = $"'rating' must be between {UserRatingScale.Min} and {UserRatingScale.Max}." }),
        _ => Results.Problem(),
    };

    /// <summary>
    /// A pin refused for a tag the item does not hold is a <c>400</c>, not a <c>404</c>: the item exists and the
    /// caller found it, so pointing at the item would send them looking for the wrong bug.
    /// </summary>
    internal static IResult ToResult(PinPosterResult result) => result switch
    {
        PinPosterResult.Ok => Results.NoContent(),
        PinPosterResult.InvalidTag => Results.BadRequest(new { error = "This item has no poster with that tag." }),
        PinPosterResult.NotFound => Results.NotFound(),
        _ => Results.Problem(),
    };
}

/// <summary>The instant a viewing happened, as the user states it. Required — a log without a time is the toggle.</summary>
public sealed record LogWatchRequest(DateTimeOffset? WatchedAt);

/// <summary>A 1–5 star verdict. Out of range is rejected rather than clamped — see <see cref="SetRatingStatus"/>.</summary>
public sealed record SetRatingRequest(int Rating);
