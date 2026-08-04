using System.Security.Claims;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.Hosty;

/// <summary>
/// The one place a Host principal becomes an app user: the claim is read from
/// <see cref="ClaimTypes.NameIdentifier"/>, an absent/empty claim and an unprovisioned user both resolve
/// to null (never to another user), and the id overload stays out of the change tracker.
/// </summary>
public sealed class HostIdentityTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;

    public HostIdentityTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(
            new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();
    }

    [Fact]
    public void Host_user_id_comes_from_the_name_identifier_claim()
    {
        Assert.Equal("host-1", Principal("host-1").GetHostUserId());
        Assert.Null(Anonymous().GetHostUserId());
        Assert.Null(Principal(string.Empty).GetHostUserId());
    }

    [Fact]
    public async Task Resolves_the_app_user_of_the_calling_host_identity()
    {
        SeedUser("host-1");
        var expected = SeedUser("host-2");

        Assert.Equal(expected.Id, await Principal("host-2").ResolveAppUserIdAsync(_database, default));

        var user = await Principal("host-2").ResolveAppUserAsync(_database, default);
        Assert.Equal(expected.Id, Assert.IsType<AppUser>(user).Id);
        Assert.Equal("host-2", user.HostUserId);
    }

    [Fact]
    public async Task Resolves_to_null_when_the_principal_carries_no_host_user_id()
    {
        SeedUser("host-1");

        Assert.Null(await Anonymous().ResolveAppUserIdAsync(_database, default));
        Assert.Null(await Anonymous().ResolveAppUserAsync(_database, default));
        Assert.Null(await Principal(string.Empty).ResolveAppUserIdAsync(_database, default));
        Assert.Null(await Principal(string.Empty).ResolveAppUserAsync(_database, default));
    }

    [Fact]
    public async Task Resolves_to_null_when_the_user_has_not_been_provisioned_yet()
    {
        SeedUser("host-1");

        Assert.Null(await Principal("host-unknown").ResolveAppUserIdAsync(_database, default));
        Assert.Null(await Principal("host-unknown").ResolveAppUserAsync(_database, default));
    }

    [Fact]
    public async Task Id_resolution_is_untracked_while_entity_resolution_is_tracked()
    {
        SeedUser("host-1");
        _database.ChangeTracker.Clear();

        await Principal("host-1").ResolveAppUserIdAsync(_database, default);
        Assert.Empty(_database.ChangeTracker.Entries<AppUser>());

        var user = await Principal("host-1").ResolveAppUserAsync(_database, default);
        Assert.Same(user, Assert.Single(_database.ChangeTracker.Entries<AppUser>()).Entity);
    }

    private static ClaimsPrincipal Principal(string hostUserId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, hostUserId)], "Test"));

    /// <summary>A principal without the Host claim — what an unauthenticated request carries.</summary>
    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    private AppUser SeedUser(string hostUserId)
    {
        var user = new AppUser
        {
            HostUserId = hostUserId,
            Email = $"{hostUserId}@example.com",
            DisplayName = hostUserId,
            Role = AppUserRole.User,
            CreatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        _database.AppUsers.Add(user);
        _database.SaveChanges();
        return user;
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }
}
