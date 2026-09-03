using HostySdk.App;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Mcp;

/// <summary>Who is calling a tool, and whether this server treats them as an administrator.</summary>
/// <param name="AppUserId">
/// This app's own user id, or null when the Host user has none — someone who has never opened the
/// app. Personal tools refuse on null rather than answering for a stranger.
/// </param>
public sealed record McpCaller(int? AppUserId, bool IsAdministrator, string HostUserId);

/// <summary>
/// Authenticates an MCP call from the delegated token it carries.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not the app's ordinary authentication. An agent calling on an operator's behalf holds
/// a short-TTL token Core signed for *this* app, while the identity scheme in front of every other
/// route revalidates an app identity token against Core — which rejects a delegated one outright,
/// because the credential type is inside the signed input. Authenticating this route the ordinary way
/// refused every agent call with a 401 while browser traffic kept working, which is what made it look
/// like a configuration problem rather than the wrong scheme.
/// </para>
/// <para>
/// A separate type, not a few lines inside the endpoint, so the wiring can be asserted: this project
/// has no integration harness, and left in a route lambda none of the decisions below would be
/// reachable by a test.
/// </para>
/// </remarks>
public static class McpCallerIdentity
{
    /// <summary>The Host role a delegated token carries for an administrator.</summary>
    /// <remarks>
    /// The same string the authentication scheme maps in <c>Program.cs</c>. Compared here rather than
    /// mapped through the app's own role, because a delegated token is not a session and never becomes
    /// a <c>ClaimsPrincipal</c> — there is no claim to read it from.
    /// </remarks>
    public const string HostAdminRole = "host.admin";

    /// <summary>The caller, or null when the credential is missing, malformed, expired, or not ours.</summary>
    public static async Task<McpCaller?> ResolveAsync(
        string? authorizationHeader,
        MediaServerDbContext database,
        CancellationToken cancellationToken,
        string? appId = null,
        string? publicKeyBase64 = null)
    {
        var actor = HostyDelegatedToken.Validate(
            HostyDelegatedToken.ReadBearer(authorizationHeader), appId, publicKeyBase64);
        if (actor is null)
        {
            return null;
        }

        // Resolved the way the identity scheme resolves it, against the same column. A Host user with
        // no row here is authenticated and has no library state — which is a different answer from
        // "unauthenticated", and the tools tell them apart.
        var appUserId = await database.AppUsers.AsNoTracking()
            .Where(user => user.HostUserId == actor.Subject)
            .Select(user => (int?)user.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return new McpCaller(
            appUserId,
            string.Equals(actor.Role, HostAdminRole, StringComparison.OrdinalIgnoreCase),
            actor.Subject);
    }
}
