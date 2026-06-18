using MediaServer.Api.Data;
using MediaServer.Api.Jellyfin.Auth;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.Jellyfin;

public sealed class JellyfinCredentialServiceTests : IDisposable
{
    private readonly JellyfinDatabase _db = new();
    private readonly FakePinHasher _hasher = new();
    private readonly TestTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly JellyfinDeviceContext _device = new("Infuse", "iPhone", "device-1", "8.3");

    private JellyfinCredentialService Service() => new(_db.Create(), _hasher, _time);

    private async Task<AppUser> SeedUserAsync(string email = "alex@example.com")
    {
        var user = new AppUser
        {
            HostUserId = "host-" + Guid.NewGuid().ToString("N"),
            Email = email,
            Role = AppUserRole.User,
            CreatedAt = _time.GetUtcNow(),
            LastSeenAt = _time.GetUtcNow(),
        };
        _db.Context.AppUsers.Add(user);
        await _db.Context.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task Generates_a_six_digit_pin_when_none_is_supplied()
    {
        var user = await SeedUserAsync();

        var issued = await Service().CreateOrRegenerateAsync(user, requestedPin: null, CancellationToken.None);

        Assert.Equal("alex@example.com", issued.Username);
        Assert.NotNull(issued.GeneratedPin);
        Assert.Matches("^[0-9]{6}$", issued.GeneratedPin!);
        Assert.Single(_db.Create().JellyfinCredentials);
    }

    [Theory]
    [InlineData("12345")]      // too short
    [InlineData("123456789")]  // too long
    [InlineData("12ab56")]     // non-numeric
    public async Task Rejects_an_out_of_range_pin(string pin)
    {
        var user = await SeedUserAsync();

        await Assert.ThrowsAsync<JellyfinCredentialValidationException>(
            () => Service().CreateOrRegenerateAsync(user, pin, CancellationToken.None));
    }

    [Fact]
    public async Task Authenticates_with_the_correct_pin_and_issues_a_validatable_token()
    {
        var user = await SeedUserAsync();
        await Service().CreateOrRegenerateAsync(user, "123456", CancellationToken.None);

        var session = await Service().AuthenticateAsync("alex@example.com", "123456", _device, CancellationToken.None);

        Assert.Equal(user.Id, session.User.Id);
        Assert.False(string.IsNullOrWhiteSpace(session.RawToken));

        var validated = await Service().ValidateTokenAsync(session.RawToken, CancellationToken.None);
        Assert.NotNull(validated);
        Assert.Equal(user.Id, validated!.User.Id);
    }

    [Fact]
    public async Task Unknown_username_reports_invalid_credentials()
    {
        var exception = await Assert.ThrowsAsync<JellyfinAuthException>(
            () => Service().AuthenticateAsync("nobody@example.com", "123456", _device, CancellationToken.None));

        Assert.Equal(JellyfinAuthFailure.InvalidCredentials, exception.Reason);
    }

    [Fact]
    public async Task Wrong_pin_increments_the_consecutive_failure_count()
    {
        var user = await SeedUserAsync();
        await Service().CreateOrRegenerateAsync(user, "123456", CancellationToken.None);

        await Assert.ThrowsAsync<JellyfinAuthException>(
            () => Service().AuthenticateAsync("alex@example.com", "000000", _device, CancellationToken.None));

        var credential = await _db.Create().JellyfinCredentials.SingleAsync();
        Assert.Equal(1, credential.FailedAttempts);
    }

    [Fact]
    public async Task Temporary_lockout_after_ten_consecutive_failures()
    {
        var user = await SeedUserAsync();
        await Service().CreateOrRegenerateAsync(user, "123456", CancellationToken.None);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Assert.ThrowsAsync<JellyfinAuthException>(
                () => Service().AuthenticateAsync("alex@example.com", "000000", _device, CancellationToken.None));
        }

        var credential = await _db.Create().JellyfinCredentials.SingleAsync();
        Assert.True(credential.LockedUntil > _time.GetUtcNow());

        // Even the correct PIN is refused while temporarily locked.
        var exception = await Assert.ThrowsAsync<JellyfinAuthException>(
            () => Service().AuthenticateAsync("alex@example.com", "123456", _device, CancellationToken.None));
        Assert.Equal(JellyfinAuthFailure.TemporarilyLocked, exception.Reason);
    }

    [Fact]
    public async Task Successful_login_after_the_window_expires_resets_the_counter()
    {
        var user = await SeedUserAsync();
        await Service().CreateOrRegenerateAsync(user, "123456", CancellationToken.None);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Assert.ThrowsAsync<JellyfinAuthException>(
                () => Service().AuthenticateAsync("alex@example.com", "000000", _device, CancellationToken.None));
        }

        _time.Advance(TimeSpan.FromMinutes(5));
        var session = await Service().AuthenticateAsync("alex@example.com", "123456", _device, CancellationToken.None);

        Assert.NotNull(session.RawToken);
        var credential = await _db.Create().JellyfinCredentials.SingleAsync();
        Assert.Equal(0, credential.FailedAttempts);
        Assert.Null(credential.LockedUntil);
    }

    [Fact]
    public async Task Permanent_lockout_after_one_hundred_consecutive_failures()
    {
        var user = await SeedUserAsync();
        await Service().CreateOrRegenerateAsync(user, "123456", CancellationToken.None);

        for (var attempt = 0; attempt < 100; attempt++)
        {
            _time.Advance(TimeSpan.FromHours(2)); // clear any temporary window so each failure counts
            await Assert.ThrowsAsync<JellyfinAuthException>(
                () => Service().AuthenticateAsync("alex@example.com", "000000", _device, CancellationToken.None));
        }

        var credential = await _db.Create().JellyfinCredentials.SingleAsync();
        Assert.True(credential.PermanentlyLocked);

        _time.Advance(TimeSpan.FromHours(2));
        var exception = await Assert.ThrowsAsync<JellyfinAuthException>(
            () => Service().AuthenticateAsync("alex@example.com", "123456", _device, CancellationToken.None));
        Assert.Equal(JellyfinAuthFailure.PermanentlyLocked, exception.Reason);
    }

    [Fact]
    public async Task Regenerating_clears_lockout_and_revokes_existing_tokens()
    {
        var user = await SeedUserAsync();
        await Service().CreateOrRegenerateAsync(user, "123456", CancellationToken.None);
        var session = await Service().AuthenticateAsync("alex@example.com", "123456", _device, CancellationToken.None);

        await Service().CreateOrRegenerateAsync(user, "654321", CancellationToken.None);

        // Old token no longer validates; the old credential's tokens were revoked.
        Assert.Null(await Service().ValidateTokenAsync(session.RawToken, CancellationToken.None));
        // The new PIN works.
        var renewed = await Service().AuthenticateAsync("alex@example.com", "654321", _device, CancellationToken.None);
        Assert.NotNull(renewed.RawToken);
    }

    [Fact]
    public async Task Logout_revokes_the_session_token()
    {
        var user = await SeedUserAsync();
        await Service().CreateOrRegenerateAsync(user, "123456", CancellationToken.None);
        var session = await Service().AuthenticateAsync("alex@example.com", "123456", _device, CancellationToken.None);

        await Service().LogoutAsync(session.Token.Id, CancellationToken.None);

        Assert.Null(await Service().ValidateTokenAsync(session.RawToken, CancellationToken.None));
    }

    [Fact]
    public async Task Revoking_the_credential_blocks_login_and_tokens()
    {
        var user = await SeedUserAsync();
        await Service().CreateOrRegenerateAsync(user, "123456", CancellationToken.None);
        var session = await Service().AuthenticateAsync("alex@example.com", "123456", _device, CancellationToken.None);

        Assert.True(await Service().RevokeCredentialAsync(user.Id, CancellationToken.None));

        Assert.Null(await Service().ValidateTokenAsync(session.RawToken, CancellationToken.None));
        var exception = await Assert.ThrowsAsync<JellyfinAuthException>(
            () => Service().AuthenticateAsync("alex@example.com", "123456", _device, CancellationToken.None));
        Assert.Equal(JellyfinAuthFailure.Revoked, exception.Reason);
    }

    public void Dispose() => _db.Dispose();
}
