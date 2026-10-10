using HostySdk.App;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Mcp;

/// <summary>The authenticated Host actor, app account, and credential's MCP permissions.</summary>
/// <param name="AppUserId">Null for a Host user without an app account; personal tools refuse it.</param>
public sealed record McpCaller(
    int? AppUserId, bool IsAdministrator, string HostUserId, bool CanRead, bool CanMutate);

/// <summary>Authenticates MCP credentials separately from ordinary app sessions.</summary>
public static class McpCallerIdentity
{
    public const string HostAdminRole = "host.admin";
    public const string McpInvokeScope = "mcp:invoke";

    /// <summary>
    /// Legacy delegated tokens are verified locally. Other credentials are introspected on every
    /// request, so revocation and changed app access take effect without a cache window.
    /// </summary>
    public static async Task<McpCaller?> ResolveAsync(
        string? authorizationHeader,
        MediaServerDbContext database,
        HostyScopedTokenClient scopedTokens,
        CancellationToken cancellationToken,
        string? tool = null,
        string? appId = null,
        string? publicKeyBase64 = null)
    {
        var token = HostyDelegatedToken.ReadBearer(authorizationHeader);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var delegated = HostyDelegatedToken.Validate(token, appId, publicKeyBase64);
        string subject;
        string? role;
        bool canRead;
        bool canMutate;
        if (delegated is not null)
        {
            subject = delegated.Subject;
            role = delegated.Role;
            canRead = true;
            canMutate = true;
        }
        else
        {
            // Only this endpoint uses MCP-specific introspection. App-session authentication must
            // continue rejecting assistant MCP-only grants on ordinary API routes.
            var actor = await scopedTokens.IntrospectMcpAsync(token, tool, cancellationToken);
            if (!actor.Active || string.IsNullOrWhiteSpace(actor.Sub))
            {
                return null;
            }

            subject = actor.Sub;
            role = actor.Role;
            canRead = actor.HasScope(HostyScopedTokenClient.McpReadScope);
            canMutate = canRead && !string.IsNullOrWhiteSpace(actor.CallerAppId)
                && actor.HasScope(McpInvokeScope);
        }

        var appUserId = await database.AppUsers.AsNoTracking()
            .Where(user => user.HostUserId == subject)
            .Select(user => (int?)user.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return new McpCaller(appUserId,
            string.Equals(role, HostAdminRole, StringComparison.OrdinalIgnoreCase),
            subject, canRead, canMutate);
    }
}
