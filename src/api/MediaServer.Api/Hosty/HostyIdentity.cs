namespace MediaServer.Api.Hosty;

/// <summary>
/// A Host user identity validated by Hosty Core (the result of revalidating a forwarded
/// app identity token). The app trusts only sessions produced this way — never client-set
/// headers or cookies on their own.
/// </summary>
public sealed record HostySession(
    string AppId,
    string UserId,
    string? Email,
    string? DisplayName,
    string HostRole,
    DateTimeOffset ExpiresAt)
{
    /// <summary>Hosty admins map to the Media Server <c>admin</c> role; everyone else to <c>user</c>.</summary>
    public bool IsAdmin => string.Equals(HostRole, "host.admin", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Turns a forwarded app identity token into a validated <see cref="HostySession"/> by
/// revalidating it against Core. Implementations must not trust a token without Core.
/// </summary>
public interface IHostyIdentityValidator
{
    Task<HostySession?> ValidateAsync(string accessToken, CancellationToken cancellationToken);
}
