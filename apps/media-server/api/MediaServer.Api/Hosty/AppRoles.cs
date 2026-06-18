namespace MediaServer.Api.Hosty;

/// <summary>Media Server role names used for ASP.NET role-based authorization.</summary>
public static class AppRoles
{
    public const string Admin = "admin";
    public const string User = "user";

    /// <summary>Authorization policy name for admin-only Host-identity endpoints.</summary>
    public const string AdminPolicy = "Admin";
}
