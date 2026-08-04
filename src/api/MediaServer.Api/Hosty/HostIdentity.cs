using System.Security.Claims;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Hosty;

/// <summary>
/// Maps a validated Host principal onto this app's own user row, so "who is asking" is decided in one
/// place. Every resolver returns null when the principal carries no Host user id and when no
/// <see cref="AppUser"/> has been provisioned for it yet — the row is written by <c>/api/me</c> on first
/// sign-in, so an authenticated caller can legitimately reach an endpoint before it exists.
/// </summary>
public static class HostIdentity
{
    /// <summary>The Host user id carried by the principal, or null when the claim is absent or empty.</summary>
    public static string? GetHostUserId(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } hostUserId ? hostUserId : null;

    /// <summary>
    /// The internal app user id for the caller. Read untracked: callers only pass the id down to a service.
    /// </summary>
    public static async Task<int?> ResolveAppUserIdAsync(
        this ClaimsPrincipal principal, MediaServerDbContext database, CancellationToken cancellationToken)
    {
        if (principal.GetHostUserId() is not { } hostUserId)
        {
            return null;
        }

        return await database.AppUsers.AsNoTracking()
            .Where(user => user.HostUserId == hostUserId)
            .Select(user => (int?)user.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// The caller's app user row, tracked by <paramref name="database"/> — for endpoints that hand the
    /// entity to a service which may go on to write it.
    /// </summary>
    public static async Task<AppUser?> ResolveAppUserAsync(
        this ClaimsPrincipal principal, MediaServerDbContext database, CancellationToken cancellationToken)
    {
        if (principal.GetHostUserId() is not { } hostUserId)
        {
            return null;
        }

        return await database.AppUsers.FirstOrDefaultAsync(user => user.HostUserId == hostUserId, cancellationToken);
    }
}
