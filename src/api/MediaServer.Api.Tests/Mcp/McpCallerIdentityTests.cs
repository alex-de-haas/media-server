using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaServer.Api.Data;
using MediaServer.Api.Mcp;
using MediaServer.Api.Tests.Jellyfin;

namespace MediaServer.Api.Tests.Mcp;

/// <summary>
/// Who an MCP call is from, decided from the delegated token it carries.
/// </summary>
/// <remarks>
/// This is the wiring that was wrong on a live host: the route authenticated with the app's ordinary
/// identity scheme, which revalidates an app identity token against Core and therefore rejected every
/// agent call, while browser traffic kept working and made it look like configuration. The tests are
/// mostly refusals, because the route in front of this believes whatever it returns.
/// </remarks>
public sealed class McpCallerIdentityTests : IDisposable
{
    private const string AppId = "com.haas.media-server";
    private readonly JellyfinDatabase _db = new();
    private readonly MediaServerDbContext _context;
    private readonly ECDsa _core = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public McpCallerIdentityTests() => _context = _db.Create();

    [Fact]
    public async Task An_operator_with_an_account_here_is_resolved_to_it()
    {
        var appUserId = AddUser("host-user-1");

        var caller = await Resolve($"Bearer {Mint(subject: "host-user-1")}");

        Assert.NotNull(caller);
        Assert.Equal(appUserId, caller.AppUserId);
        Assert.Equal("host-user-1", caller.HostUserId);
    }

    [Fact]
    public async Task A_host_user_who_has_never_opened_the_app_is_authenticated_with_no_account()
    {
        // Authenticated and account-less is a real state, and it is not the same as unauthenticated:
        // the personal tools must refuse it while the rest of the surface still answers. Collapsing the
        // two would either lock the operator out or answer their library questions as somebody else.
        var caller = await Resolve($"Bearer {Mint(subject: "host-user-unknown")}");

        Assert.NotNull(caller);
        Assert.Null(caller.AppUserId);
    }

    [Fact]
    public async Task An_administrator_is_told_apart_from_an_ordinary_operator()
    {
        AddUser("host-admin");
        AddUser("host-viewer");

        Assert.True((await Resolve($"Bearer {Mint("host-admin", role: "host.admin")}"))!.IsAdministrator);
        // Paired: a flag that is always true would gate nothing, and the maintenance tools are the
        // only thing standing between an ordinary operator and a catalog-wide rescan.
        Assert.False((await Resolve($"Bearer {Mint("host-viewer", role: "host.user")}"))!.IsAdministrator);
    }

    [Fact]
    public async Task A_token_minted_for_another_app_is_refused()
    {
        AddUser("host-user-1");

        Assert.Null(await Resolve($"Bearer {Mint("host-user-1", audience: "com.haas.other")}"));
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        AddUser("host-user-1");
        var expired = Mint("host-user-1", expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.Null(await Resolve($"Bearer {expired}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer ")]
    [InlineData("Basic abc")]
    [InlineData("Bearer not-a-token")]
    // An app identity token — the credential this route used to demand, and the one an agent never has.
    [InlineData("Bearer hosty_app_identity.1.payload.signature")]
    public async Task Anything_that_is_not_a_delegated_token_for_this_app_is_refused(string? header)
        => Assert.Null(await Resolve(header));

    private Task<McpCaller?> Resolve(string? header) => McpCallerIdentity.ResolveAsync(
        header, _context, CancellationToken.None, AppId, Convert.ToBase64String(_core.ExportSubjectPublicKeyInfo()));

    private string Mint(
        string subject, string role = "host.admin", string audience = AppId, DateTimeOffset? expiresAt = null)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["sub"] = subject,
            ["role"] = role,
            ["aud"] = audience,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = (expiresAt ?? now.AddMinutes(5)).ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString("N"),
        }));
        var signingInput = $"hosty_delegated.1.{payload}";
        return $"{signingInput}.{Base64Url(_core.SignData(
            Encoding.UTF8.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation))}";
    }

    private int AddUser(string hostUserId)
    {
        var user = new AppUser
        {
            HostUserId = hostUserId,
            DisplayName = hostUserId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _context.AppUsers.Add(user);
        _context.SaveChanges();
        return user.Id;
    }

    private static string Base64Url(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose()
    {
        _core.Dispose();
        _context.Dispose();
        _db.Dispose();
    }
}
