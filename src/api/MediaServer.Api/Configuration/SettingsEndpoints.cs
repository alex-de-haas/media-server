using MediaServer.Api.Hosty;

namespace MediaServer.Api.Configuration;

/// <summary>Operator-editable application settings under <c>/api/settings</c> (Host identity).</summary>
public sealed record AppSettingsResponse(IReadOnlyList<string> CustomReleaseGroups);

/// <summary>Full replace of the editable settings (the UI sends the whole list back).</summary>
public sealed record UpdateAppSettingsRequest(IReadOnlyList<string>? CustomReleaseGroups);

public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/settings").RequireAuthorization();

        // Any signed-in user may read the configured groups (they only affect name parsing).
        group.MapGet("/", async (AppSettingsService settings, CancellationToken cancellationToken) =>
            Results.Ok(new AppSettingsResponse(await settings.GetCustomReleaseGroupsAsync(cancellationToken))));

        // Mutating the parser configuration is an admin-only management action.
        group.MapPut("/", async (UpdateAppSettingsRequest? request, AppSettingsService settings, CancellationToken cancellationToken) =>
        {
            var saved = await settings.UpdateCustomReleaseGroupsAsync(request?.CustomReleaseGroups, cancellationToken);
            return Results.Ok(new AppSettingsResponse(saved));
        }).RequireAuthorization(AppRoles.AdminPolicy);
    }
}
