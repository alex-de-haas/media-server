using System.Security.Claims;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

var builder = WebApplication.CreateBuilder(args);

// All ports, origins, and paths come from the injected HOSTY_* environment — never hard-coded.
var hosty = HostyOptions.FromConfiguration(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddSingleton(hosty);
HostyKestrel.ConfigureUrls(builder.WebHost, hosty);

// EF Core + SQLite live under the app data directory so Hosty backup/restore covers them.
Directory.CreateDirectory(hosty.AppDataDir);
builder.Services.AddDbContext<MediaServerDbContext>(options =>
    options.UseSqlite($"Data Source={hosty.DatabasePath}"));

// Identity validation against Core, with a short-TTL positive cache.
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient(CoreIdentityValidator.HttpClientName, client =>
{
    client.BaseAddress = new Uri(hosty.CoreOrigin);
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddSingleton<CoreIdentityValidator>();
builder.Services.AddSingleton<IHostyIdentityValidator>(serviceProvider => new CachingIdentityValidator(
    serviceProvider.GetRequiredService<CoreIdentityValidator>(),
    serviceProvider.GetRequiredService<IMemoryCache>(),
    TimeSpan.FromSeconds(30)));

builder.Services
    .AddAuthentication(HostyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, HostyAuthenticationHandler>(HostyAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization();

var app = builder.Build();

// Apply schema migrations on startup. M4 will wrap this in an on-demand Core backup
// (POST /api/internal/apps/{appId}/backups) before migrating.
using (var scope = app.Services.CreateScope())
{
    var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
    database.Database.Migrate();
}

app.UseAuthentication();
app.UseAuthorization();

// Liveness/readiness probe on the internal surface.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Placeholder root so the public `jellyfin` port responds during M0; the real
// Jellyfin-compatible surface lands in M2.
app.MapGet("/", () => Results.Ok(new { service = "media-server", status = "ok" }));

// Returns the validated Host identity and upserts the internal app user (admin/user mapping).
app.MapGet("/api/me", async (ClaimsPrincipal principal, MediaServerDbContext database, CancellationToken cancellationToken) =>
{
    var hostUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrEmpty(hostUserId))
    {
        return Results.Unauthorized();
    }

    var email = principal.FindFirstValue(ClaimTypes.Email);
    var displayName = principal.Identity?.Name;
    var role = principal.IsInRole(AppRoles.Admin) ? AppUserRole.Admin : AppUserRole.User;
    var now = DateTimeOffset.UtcNow;

    var user = await database.AppUsers.FirstOrDefaultAsync(candidate => candidate.HostUserId == hostUserId, cancellationToken);
    if (user is null)
    {
        user = new AppUser
        {
            HostUserId = hostUserId,
            Email = email,
            DisplayName = displayName,
            Role = role,
            CreatedAt = now,
            LastSeenAt = now,
        };
        database.AppUsers.Add(user);
    }
    else
    {
        user.Email = email;
        user.DisplayName = displayName;
        user.Role = role;
        user.LastSeenAt = now;
    }

    await database.SaveChangesAsync(cancellationToken);

    return Results.Ok(new
    {
        id = user.Id,
        hostUserId = user.HostUserId,
        email = user.Email,
        displayName = user.DisplayName,
        role = user.Role.ToString().ToLowerInvariant(),
    });
}).RequireAuthorization();

app.Run();
