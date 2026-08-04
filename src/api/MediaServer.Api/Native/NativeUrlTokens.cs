using System.Security.Cryptography;
using System.Text;
using MediaServer.Api.Hosty;

namespace MediaServer.Api.Native;

/// <summary>
/// Short-lived signed tokens for the media and image URLs the native clients hand to
/// <c>AVPlayer</c>. It will not attach an <c>Authorization</c> header to the ranged requests it
/// issues itself, which is the same reason the Jellyfin surface accepts <c>api_key=</c> on those
/// routes. See <c>docs/features/native-client-api/plan.md</c>.
///
/// A token is not merely "scoped to an item": one playback issues many ranged requests over hours,
/// so it is bound to the user, the specific media source, and the methods it may be used with, and
/// its lifetime is meant to cover a whole playback rather than a single request. A token that can
/// expire between two <c>Range</c> requests of one file is a broken token.
/// </summary>
public sealed class NativeUrlTokenService(NativeUrlSigningKey key, TimeProvider time)
{
    /// <summary>
    /// Default validity. Long enough that a film plus pauses cannot outlive it; short enough that a
    /// leaked URL is not a standing grant. <c>native-playback</c> narrows this to the session.
    /// </summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(12);

    private const string Version = "v1";

    /// <summary>
    /// A ceiling on what we will even look at. These routes are anonymous by design — the token is the
    /// credential — so an unbounded query string would otherwise buy an attacker arbitrary allocation
    /// and HMAC work per request. A real token is well under a hundred characters.
    /// </summary>
    private const int MaxTokenLength = 256;

    public string Mint(int appUserId, Guid mediaSourceId, NativeUrlTokenMethods methods, TimeSpan? lifetime = null)
    {
        var expires = time.GetUtcNow().Add(lifetime ?? DefaultLifetime).ToUnixTimeSeconds();
        var payload = Payload(appUserId, mediaSourceId, methods, expires);
        return $"{payload}.{Sign(payload)}";
    }

    /// <summary>
    /// Validates a token against what the request is actually trying to do. Every failure returns a
    /// reason rather than a bool, so the caller can log why without logging the token.
    /// </summary>
    public NativeUrlTokenResult Validate(string? token, Guid mediaSourceId, string httpMethod)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return NativeUrlTokenResult.Invalid(NativeUrlTokenFailure.Missing);
        }

        if (token.Length > MaxTokenLength)
        {
            return NativeUrlTokenResult.Invalid(NativeUrlTokenFailure.Malformed);
        }

        var separator = token.LastIndexOf('.');
        if (separator <= 0)
        {
            return NativeUrlTokenResult.Invalid(NativeUrlTokenFailure.Malformed);
        }

        var payload = token[..separator];
        var signature = token[(separator + 1)..];

        // Fixed-time comparison: a signature check that leaks timing is a signature check that can be
        // walked byte by byte.
        var expected = Sign(payload);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature), Encoding.UTF8.GetBytes(expected)))
        {
            return NativeUrlTokenResult.Invalid(NativeUrlTokenFailure.BadSignature);
        }

        var parts = payload.Split('.');
        if (parts.Length != 5 || parts[0] != Version
            || !int.TryParse(parts[1], out var appUserId)
            || !Guid.TryParseExact(parts[2], "N", out var tokenSourceId)
            || !long.TryParse(parts[4], out var expiresUnix))
        {
            return NativeUrlTokenResult.Invalid(NativeUrlTokenFailure.Malformed);
        }

        if (tokenSourceId != mediaSourceId)
        {
            return NativeUrlTokenResult.Invalid(NativeUrlTokenFailure.WrongSource);
        }

        if (!NativeUrlTokenMethods.Parse(parts[3]).Allows(httpMethod))
        {
            return NativeUrlTokenResult.Invalid(NativeUrlTokenFailure.MethodNotAllowed);
        }

        if (DateTimeOffset.FromUnixTimeSeconds(expiresUnix) <= time.GetUtcNow())
        {
            return NativeUrlTokenResult.Invalid(NativeUrlTokenFailure.Expired);
        }

        return NativeUrlTokenResult.Valid(appUserId);
    }

    private static string Payload(int appUserId, Guid mediaSourceId, NativeUrlTokenMethods methods, long expires) =>
        $"{Version}.{appUserId}.{mediaSourceId:N}.{methods}.{expires}";

    private string Sign(string payload) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(key.Value, Encoding.UTF8.GetBytes(payload)));
}

/// <summary>The HTTP methods a token may be spent on. Media reads are <c>GET</c> and <c>HEAD</c>.</summary>
public readonly record struct NativeUrlTokenMethods(bool Get, bool Head)
{
    public static readonly NativeUrlTokenMethods Read = new(Get: true, Head: true);

    // Compared case-insensitively rather than upper-cased: these routes take one call per ranged
    // request, and a hot path should not allocate a string to answer a two-way question.
    public bool Allows(string httpMethod) =>
        (Get && HttpMethods.IsGet(httpMethod)) || (Head && HttpMethods.IsHead(httpMethod));

    public static NativeUrlTokenMethods Parse(string value) =>
        new(Get: value.Contains('G', StringComparison.Ordinal), Head: value.Contains('H', StringComparison.Ordinal));

    public override string ToString() => $"{(Get ? "G" : string.Empty)}{(Head ? "H" : string.Empty)}";
}

public enum NativeUrlTokenFailure
{
    None,
    Missing,
    Malformed,
    BadSignature,
    WrongSource,
    MethodNotAllowed,
    Expired,
}

public readonly record struct NativeUrlTokenResult(bool IsValid, int AppUserId, NativeUrlTokenFailure Failure)
{
    public static NativeUrlTokenResult Valid(int appUserId) => new(true, appUserId, NativeUrlTokenFailure.None);

    public static NativeUrlTokenResult Invalid(NativeUrlTokenFailure failure) => new(false, 0, failure);
}

/// <summary>
/// The HMAC key behind the URL tokens, persisted under the app data directory so tokens survive a
/// restart — a viewer should not be interrupted because the server was updated mid-film. It is
/// generated on first use and never leaves the process.
/// </summary>
public sealed class NativeUrlSigningKey
{
    private const string FileName = "native-url-signing.key";

    public NativeUrlSigningKey(HostyOptions hosty)
    {
        var path = Path.Combine(hosty.AppDataDir, FileName);
        if (File.Exists(path))
        {
            Value = File.ReadAllBytes(path);
            if (Value.Length >= 32)
            {
                return;
            }
        }

        Value = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(hosty.AppDataDir);
        File.WriteAllBytes(path, Value);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    internal NativeUrlSigningKey(byte[] value) => Value = value;

    public byte[] Value { get; }
}
