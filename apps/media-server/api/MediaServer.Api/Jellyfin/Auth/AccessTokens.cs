using System.Security.Cryptography;
using System.Text;

namespace MediaServer.Api.Jellyfin.Auth;

/// <summary>
/// Generates opaque access tokens (≥128 bits of entropy) and the at-rest hash. The raw token is
/// returned to the client exactly once; only its SHA-256 hash is persisted. SHA-256 (not argon2) is
/// appropriate here because the token is already high-entropy random — there is nothing to brute-force.
/// </summary>
public static class AccessTokens
{
    private const int TokenBytes = 32; // 256 bits

    public static string Generate() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(TokenBytes));

    public static string Hash(string rawToken) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
}
