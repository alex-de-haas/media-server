using System.Security.Claims;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Native;

/// <summary>
/// Maps the validated Host identity to the internal app user this surface's data is keyed by.
/// </summary>
/// <remarks>
/// The same lookup is copy-pasted across several <c>/api</c> endpoint files; this surface keeps one
/// copy rather than adding another, and the duplication elsewhere is tracked separately.
/// </remarks>
internal static class NativePrincipal
{
    public static async Task<int?> AppUserIdAsync(
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
}
