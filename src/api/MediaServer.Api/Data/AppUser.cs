namespace MediaServer.Api.Data;

public enum AppUserRole
{
    User = 0,
    Admin = 1,
}

/// <summary>
/// Internal Media Server user, linked to a Hosty Host user. The Hosty user id and email are
/// stored so the app can re-link by unique email if Host user ids ever change.
/// </summary>
public sealed class AppUser
{
    public int Id { get; set; }

    public required string HostUserId { get; set; }

    public string? Email { get; set; }

    public string? DisplayName { get; set; }

    public AppUserRole Role { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }
}
