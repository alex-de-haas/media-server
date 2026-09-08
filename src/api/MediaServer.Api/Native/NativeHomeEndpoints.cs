using System.Security.Claims;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.Library;

namespace MediaServer.Api.Native;

/// <summary>Per-user Home rails shared with the Web library, bounded for native browsing.</summary>
public static class NativeHomeEndpoints
{
    public static void MapNativeHomeEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/home/resume", (int? limit, ClaimsPrincipal principal, MediaServerDbContext database,
            LibraryReadService library, CancellationToken ct) => ReadAsync(false, limit, principal, database, library, ct))
            .RequireAuthorization().WithName("getNativeHomeResume").Produces<IReadOnlyList<LibraryRailItemDto>>();
        group.MapGet("/home/nextup", (int? limit, ClaimsPrincipal principal, MediaServerDbContext database,
            LibraryReadService library, CancellationToken ct) => ReadAsync(true, limit, principal, database, library, ct))
            .RequireAuthorization().WithName("getNativeHomeNextUp").Produces<IReadOnlyList<LibraryRailItemDto>>();
    }

    internal static async Task<IResult> ReadAsync(bool nextUp, int? limit, ClaimsPrincipal principal,
        MediaServerDbContext database, LibraryReadService library, CancellationToken ct)
    {
        if (await principal.ResolveAppUserIdAsync(database, ct) is not { } userId)
            return Results.Unauthorized();
        var bounded = Math.Clamp(limit ?? 20, 1, 60);
        return Results.Ok(nextUp
            ? await library.GetNextUpAsync(userId, bounded, ct)
            : await library.GetResumeAsync(userId, bounded, ct));
    }
}
