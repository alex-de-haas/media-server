using System.Security.Claims;
using System.Text.Encodings.Web;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace MediaServer.Api.Jellyfin.Auth;

/// <summary>
/// Authenticates Jellyfin-surface requests against a Media Server-owned opaque token. Unlike the Hosty
/// identity scheme, the token is validated <em>locally</em> against the credential store on every
/// request — Core is consulted only at login/issuance (see <c>docs/features/security.md</c>). The token
/// is read from the <c>MediaBrowser</c>/<c>Emby</c> authorization header, the <c>X-Emby-Token</c>
/// header, or (for media/image URLs only) the <c>api_key</c> query parameter.
/// </summary>
public sealed class JellyfinAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    JellyfinCredentialService credentials)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Jellyfin";
    public const string PolicyName = "Jellyfin";

    public const string TokenIdClaim = "jf_token_id";
    public const string UserIdClaim = "jf_user_id";
    public const string DeviceIdClaim = "jf_device_id";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var path = Request.Path.Value ?? string.Empty;
        var allowQueryToken =
            path.StartsWith("/Videos", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/Images/", StringComparison.OrdinalIgnoreCase);

        var authorization = MediaBrowserAuthorization.Parse(Request, allowQueryToken);
        if (string.IsNullOrWhiteSpace(authorization.Token))
        {
            return AuthenticateResult.NoResult();
        }

        var validated = await credentials.ValidateTokenAsync(authorization.Token, Context.RequestAborted);
        if (validated is null)
        {
            return AuthenticateResult.Fail("Jellyfin access token is invalid or has been revoked.");
        }

        var user = validated.User;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(UserIdClaim, JellyfinIds.User(user.Id)),
            new(TokenIdClaim, validated.Token.Id.ToString()),
            new(ClaimTypes.Role, user.Role == AppUserRole.Admin ? AppRoles.Admin : AppRoles.User),
        };

        if (!string.IsNullOrEmpty(user.Email))
        {
            claims.Add(new Claim(ClaimTypes.Email, user.Email));
        }

        if (!string.IsNullOrEmpty(validated.Token.DeviceId))
        {
            claims.Add(new Claim(DeviceIdClaim, validated.Token.DeviceId));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return AuthenticateResult.Success(ticket);
    }
}
