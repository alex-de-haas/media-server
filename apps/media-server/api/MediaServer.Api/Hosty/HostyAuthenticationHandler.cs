using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace MediaServer.Api.Hosty;

/// <summary>
/// Authenticates requests carrying a Hosty app identity token. The token is accepted from
/// (in priority order) the <c>Authorization: Bearer</c> header used by the web BFF, the
/// <c>X-Docker-Host-Identity</c> compatibility header, or an app-origin cookie. It is always
/// revalidated against Core via <see cref="IHostyIdentityValidator"/> — the app never trusts a
/// client-supplied token on its own.
/// </summary>
public sealed class HostyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IHostyIdentityValidator validator)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Hosty";
    public const string IdentityHeader = "X-Docker-Host-Identity";
    public const string CookieName = "hosty_identity";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ExtractToken(Request);
        if (string.IsNullOrWhiteSpace(token))
        {
            return AuthenticateResult.NoResult();
        }

        var session = await validator.ValidateAsync(token, Context.RequestAborted);
        if (session is null)
        {
            return AuthenticateResult.Fail("Hosty identity token is invalid or could not be revalidated.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, session.UserId),
            new("hosty_role", session.HostRole),
            new(ClaimTypes.Role, session.IsAdmin ? AppRoles.Admin : AppRoles.User),
        };

        if (!string.IsNullOrEmpty(session.Email))
        {
            claims.Add(new Claim(ClaimTypes.Email, session.Email));
        }

        if (!string.IsNullOrEmpty(session.DisplayName))
        {
            claims.Add(new Claim(ClaimTypes.Name, session.DisplayName));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return AuthenticateResult.Success(ticket);
    }

    private static string? ExtractToken(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authorization["Bearer ".Length..].Trim();
        }

        if (request.Headers.TryGetValue(IdentityHeader, out var header) && !string.IsNullOrWhiteSpace(header))
        {
            return header.ToString().Trim();
        }

        if (request.Cookies.TryGetValue(CookieName, out var cookie) && !string.IsNullOrWhiteSpace(cookie))
        {
            return cookie;
        }

        // WebSocket handshakes cannot set custom headers, so SignalR passes the bearer as a query
        // parameter on hub connections. Only honor it for the real-time hub paths.
        if (request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase) &&
            request.Query.TryGetValue("access_token", out var queryToken) && !string.IsNullOrWhiteSpace(queryToken))
        {
            return queryToken.ToString().Trim();
        }

        return null;
    }
}
