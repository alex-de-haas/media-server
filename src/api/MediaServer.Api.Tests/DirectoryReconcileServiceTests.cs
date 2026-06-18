using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.Jellyfin.Auth;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests;

public sealed class DirectoryReconcileServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;

    public DirectoryReconcileServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();
    }

    private DirectoryReconcileService Service(IHostyCoreClient core)
    {
        var credentials = new JellyfinCredentialService(_database, new Argon2idPinHasher(), TimeProvider.System);
        return new DirectoryReconcileService(_database, core, credentials, NullLogger<DirectoryReconcileService>.Instance);
    }

    [Fact]
    public async Task Upserts_new_users_with_admin_role_mapping()
    {
        var core = new FakeCore([
            new CoreDirectoryUser("h-admin", "Ann", "ann@example.com", "host.admin"),
            new CoreDirectoryUser("h-user", "Bob", "bob@example.com", "host.user"),
        ]);

        var result = await Service(core).ReconcileAsync(CancellationToken.None);

        Assert.False(result.Skipped);
        Assert.Equal(2, result.UsersUpserted);

        await using var fresh = Fresh();
        var admin = await fresh.AppUsers.FirstAsync(u => u.HostUserId == "h-admin");
        var user = await fresh.AppUsers.FirstAsync(u => u.HostUserId == "h-user");
        Assert.Equal(AppUserRole.Admin, admin.Role);
        Assert.Equal(AppUserRole.User, user.Role);
        Assert.Equal("ann@example.com", admin.Email);
    }

    [Fact]
    public async Task Updates_changed_role_and_email()
    {
        _database.AppUsers.Add(new AppUser
        {
            HostUserId = "h1",
            Email = "old@example.com",
            DisplayName = "Old",
            Role = AppUserRole.User,
            CreatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        });
        await _database.SaveChangesAsync();

        var core = new FakeCore([new CoreDirectoryUser("h1", "New", "new@example.com", "host.admin")]);
        var result = await Service(core).ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, result.UsersUpserted);
        await using var fresh = Fresh();
        var user = await fresh.AppUsers.FirstAsync(u => u.HostUserId == "h1");
        Assert.Equal(AppUserRole.Admin, user.Role);
        Assert.Equal("new@example.com", user.Email);
        Assert.Equal("New", user.DisplayName);
    }

    [Fact]
    public async Task Revokes_jellyfin_credential_for_user_no_longer_assigned()
    {
        var appUserId = await SeedUserWithCredential("h-gone");

        // Directory now lists a different user; "h-gone" has been unassigned.
        var core = new FakeCore([new CoreDirectoryUser("h-present", "Present", "p@example.com", "host.user")]);
        var result = await Service(core).ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, result.CredentialsRevoked);
        await using var fresh = Fresh();
        Assert.True((await fresh.JellyfinCredentials.FirstAsync(c => c.AppUserId == appUserId)).Revoked);
        Assert.All(await fresh.JellyfinAccessTokens.Where(t => t.AppUserId == appUserId).ToListAsync(), t => Assert.True(t.Revoked));
    }

    [Fact]
    public async Task Keeps_credential_when_user_still_assigned()
    {
        var appUserId = await SeedUserWithCredential("h-keep");
        var core = new FakeCore([new CoreDirectoryUser("h-keep", "Keep", "k@example.com", "host.user")]);

        var result = await Service(core).ReconcileAsync(CancellationToken.None);

        Assert.Equal(0, result.CredentialsRevoked);
        await using var fresh = Fresh();
        Assert.False((await fresh.JellyfinCredentials.FirstAsync(c => c.AppUserId == appUserId)).Revoked);
    }

    [Fact]
    public async Task Skips_when_core_unavailable_and_never_revokes()
    {
        await SeedUserWithCredential("h-x");
        var core = new FakeCore(directory: null); // null = not Core managed / transient failure.

        var result = await Service(core).ReconcileAsync(CancellationToken.None);

        Assert.True(result.Skipped);
        await using var fresh = Fresh();
        Assert.False((await fresh.JellyfinCredentials.FirstAsync()).Revoked);
    }

    private async Task<int> SeedUserWithCredential(string hostUserId)
    {
        var user = new AppUser
        {
            HostUserId = hostUserId,
            Email = $"{hostUserId}@example.com",
            Role = AppUserRole.User,
            CreatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        _database.AppUsers.Add(user);
        await _database.SaveChangesAsync();

        var credentialId = Guid.NewGuid();
        _database.JellyfinCredentials.Add(new JellyfinCredential
        {
            Id = credentialId,
            AppUserId = user.Id,
            HostUserId = hostUserId,
            Username = user.Email!,
            PinHash = "hash",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        _database.JellyfinAccessTokens.Add(new JellyfinAccessToken
        {
            Id = Guid.NewGuid(),
            CredentialId = credentialId,
            AppUserId = user.Id,
            TokenHash = "tokenhash",
            CreatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        });
        await _database.SaveChangesAsync();
        return user.Id;
    }

    private MediaServerDbContext Fresh() =>
        new(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }

    private sealed class FakeCore(IReadOnlyList<CoreDirectoryUser>? directory) : IHostyCoreClient
    {
        public bool IsEnabled => directory is not null;

        public Task<CoreBackupResult?> CreateBackupAsync(string? note, CancellationToken cancellationToken) =>
            Task.FromResult<CoreBackupResult?>(null);

        public Task<bool> PublishNotificationAsync(
            CoreNotificationLevel level, string title, string? body, string? link, string? dedupeKey,
            string target = HostyCoreClient.BroadcastTarget, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<CoreDirectoryUser>?> ListDirectoryUsersAsync(CancellationToken cancellationToken) =>
            Task.FromResult(directory);
    }
}
