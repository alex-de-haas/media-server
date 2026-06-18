using System.Security.Claims;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Jellyfin.Endpoints;

/// <summary>Users, views, and library-folder endpoints.</summary>
internal static class JellyfinUserEndpoints
{
    public static void MapJellyfinUserEndpoints(this IEndpointRouteBuilder routes)
    {
        var secured = routes.MapGroup(string.Empty).RequireJellyfin();

        secured.MapGet("/Users/Me", async (ClaimsPrincipal principal, MediaServerDbContext database, JellyfinServerContext server, CancellationToken cancellationToken) =>
        {
            var user = await CurrentUserAsync(principal, database, cancellationToken);
            return user is null ? Results.Unauthorized() : JellyfinJson.Ok(JellyfinEndpoints.BuildUserDto(user, server));
        });

        secured.MapGet("/Users", async (ClaimsPrincipal principal, MediaServerDbContext database, JellyfinServerContext server, CancellationToken cancellationToken) =>
        {
            // Non-admins only see themselves; admins see the directory.
            var query = database.AppUsers.AsNoTracking();
            if (!JellyfinPrincipal.IsAdmin(principal) && JellyfinPrincipal.AppUserId(principal) is { } id)
            {
                query = query.Where(user => user.Id == id);
            }

            var users = await query.OrderBy(user => user.Id).ToListAsync(cancellationToken);
            return JellyfinJson.Ok(users.Select(user => JellyfinEndpoints.BuildUserDto(user, server)).ToList());
        });

        secured.MapGet("/Users/{userId}", async (string userId, ClaimsPrincipal principal, MediaServerDbContext database, JellyfinServerContext server, CancellationToken cancellationToken) =>
        {
            if (!JellyfinPrincipal.CanActAs(principal, userId))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var user = await ResolveUserAsync(userId, database, cancellationToken);
            return user is null ? Results.NotFound() : JellyfinJson.Ok(JellyfinEndpoints.BuildUserDto(user, server));
        });

        secured.MapGet("/Users/{userId}/Views", async (string userId, ClaimsPrincipal principal, JellyfinLibraryService library, CancellationToken cancellationToken) =>
        {
            if (!JellyfinPrincipal.CanActAs(principal, userId))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var views = await library.GetViewsAsync(cancellationToken);
            return JellyfinJson.Ok(new QueryResult<BaseItemDto>(views, views.Count));
        });

        // Newer Jellyfin route Infuse actually calls: /UserViews?userId=… (the per-user path form above
        // is the legacy alias). Views are the global catalogs, so the user only gates authorization.
        secured.MapGet("/UserViews", async (string? userId, ClaimsPrincipal principal, JellyfinLibraryService library, CancellationToken cancellationToken) =>
        {
            if (!string.IsNullOrEmpty(userId) && !JellyfinPrincipal.CanActAs(principal, userId))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var views = await library.GetViewsAsync(cancellationToken);
            return JellyfinJson.Ok(new QueryResult<BaseItemDto>(views, views.Count));
        });

        secured.MapGet("/Users/{userId}/GroupingOptions", (string userId) =>
            JellyfinJson.Ok(Array.Empty<SpecialViewOptionDto>()));

        // /UserViews counterpart Infuse calls (query userId); we expose no special grouping views.
        secured.MapGet("/UserViews/GroupingOptions", (string? userId) =>
            JellyfinJson.Ok(Array.Empty<SpecialViewOptionDto>()));

        secured.MapGet("/Library/MediaFolders", async (JellyfinLibraryService library, CancellationToken cancellationToken) =>
        {
            var views = await library.GetViewsAsync(cancellationToken);
            return JellyfinJson.Ok(new QueryResult<BaseItemDto>(views, views.Count));
        });

        secured.MapGet("/Library/VirtualFolders", async (JellyfinLibraryService library, CancellationToken cancellationToken) =>
        {
            var views = await library.GetViewsAsync(cancellationToken);
            var folders = views.Select(view => new
            {
                Name = view.Name,
                ItemId = view.Id,
                view.CollectionType,
                Locations = Array.Empty<string>(),
            });
            return JellyfinJson.Ok(folders.ToList());
        });

        // Infuse persists display preferences server-side; we accept and echo a stable default.
        secured.MapMethods("/DisplayPreferences/{displayPreferencesId}", ["GET", "POST"], (string displayPreferencesId) =>
            JellyfinJson.Ok(DefaultDisplayPreferences(displayPreferencesId)));
    }

    private static object DefaultDisplayPreferences(string id) => new
    {
        Id = id,
        SortBy = "SortName",
        SortOrder = "Ascending",
        RememberIndexing = false,
        RememberSorting = false,
        PrimaryImageHeight = 250,
        PrimaryImageWidth = 250,
        ScrollDirection = "Horizontal",
        ShowBackdrop = true,
        ShowSidebar = false,
        CustomPrefs = new Dictionary<string, string>(),
    };

    private static async Task<AppUser?> CurrentUserAsync(ClaimsPrincipal principal, MediaServerDbContext database, CancellationToken cancellationToken) =>
        JellyfinPrincipal.AppUserId(principal) is { } id
            ? await database.AppUsers.AsNoTracking().FirstOrDefaultAsync(user => user.Id == id, cancellationToken)
            : null;

    private static async Task<AppUser?> ResolveUserAsync(string jellyfinUserId, MediaServerDbContext database, CancellationToken cancellationToken)
    {
        // The Jellyfin user id is a one-way hash of the int id, so resolve over ids only and fetch the
        // single matching row rather than materializing every user.
        var ids = await database.AppUsers.AsNoTracking().Select(user => user.Id).ToListAsync(cancellationToken);
        var matchId = ids.FirstOrDefault(id => JellyfinIds.User(id) == jellyfinUserId);
        return matchId == 0
            ? null
            : await database.AppUsers.AsNoTracking().FirstOrDefaultAsync(user => user.Id == matchId, cancellationToken);
    }
}
