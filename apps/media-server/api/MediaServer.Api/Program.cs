using System.Security.Claims;
using MediaServer.Api.Catalogs;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.IO;
using MediaServer.Api.Metadata;
using MediaServer.Api.Jobs;
using MediaServer.Api.Library;
using MediaServer.Api.Organizer;
using MediaServer.Api.Pipeline;
using MediaServer.Api.Pipeline.Stages;
using MediaServer.Api.Probe;
using MediaServer.Api.Realtime;
using MediaServer.Api.Torrents;
using MediaServer.Api.Jellyfin;
using MediaServer.Api.Jellyfin.Auth;
using MediaServer.Api.Jellyfin.Endpoints;
using MediaServer.Api.Jellyfin.Streaming;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

var builder = WebApplication.CreateBuilder(args);

// All ports, origins, and paths come from the injected HOSTY_* environment — never hard-coded.
var hosty = HostyOptions.FromConfiguration(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddSingleton(hosty);
HostyKestrel.ConfigureUrls(builder.WebHost, hosty);

// Manifest settings (TMDb key, languages, ffprobe, torrent tunables) + catalog-root mounts.
var settings = MediaServerSettings.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(settings);

// Filesystem primitives + catalog management.
builder.Services.AddSingleton<IHardLinker, HardLinker>();
builder.Services.AddSingleton<IFilesystemInspector, FilesystemInspector>();
builder.Services.AddSingleton<ICatalogPathSandbox, CatalogPathSandbox>();
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<IOrganizer, OrganizerService>();

// Real-time hub + in-process pipeline queue.
builder.Services.AddSignalR();
builder.Services.AddSingleton<IRealtimeNotifier, SignalRRealtimeNotifier>();
builder.Services.AddSingleton<IPipelineQueue, PipelineQueue>();

// Torrent engine (hosted) + coordinator that bridges it to persistence and the pipeline.
builder.Services.AddSingleton<MonoTorrentEngine>();
builder.Services.AddSingleton<ITorrentEngine>(serviceProvider => serviceProvider.GetRequiredService<MonoTorrentEngine>());
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<MonoTorrentEngine>());
builder.Services.AddHostedService<TorrentCoordinator>();
builder.Services.AddScoped<TorrentService>();
builder.Services.AddScoped<DownloadFileService>();

// Identify / probe / enrich building blocks.
builder.Services.AddSingleton<INameParser, NameParser>();
builder.Services.AddSingleton<IMediaProbe, FfprobeMediaProbe>();
builder.Services.AddHttpClient(TmdbMetadataProvider.HttpClientName, client =>
{
    client.BaseAddress = new Uri("https://api.themoviedb.org/");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddSingleton<IMetadataProvider, TmdbMetadataProvider>();

// Pipeline: stages, supporting services, orchestrator, and the worker + reconciler hosted services.
builder.Services.AddScoped<IdentifyService>();
builder.Services.AddScoped<EnrichService>();
builder.Services.AddScoped<JobService>();
builder.Services.AddScoped<IPipelineStage, IntakeStage>();
builder.Services.AddScoped<IPipelineStage, DownloadStage>();
builder.Services.AddScoped<IPipelineStage, IdentifyStage>();
builder.Services.AddScoped<IPipelineStage, OrganizeStage>();
builder.Services.AddScoped<IPipelineStage, ProbeStage>();
builder.Services.AddScoped<IPipelineStage, EnrichStage>();
builder.Services.AddScoped<IPipelineStage, PublishStage>();
builder.Services.AddSingleton<IngestOrchestrator>();
builder.Services.AddScoped<IngestService>();
builder.Services.AddHostedService<PipelineWorker>();
builder.Services.AddHostedService<ReconcilerWorker>();

// EF Core + SQLite live under the app data directory so Hosty backup/restore covers them.
Directory.CreateDirectory(hosty.AppDataDir);
builder.Services.AddSingleton<SqlitePragmaInterceptor>();
builder.Services.AddDbContext<MediaServerDbContext>((serviceProvider, options) =>
    options
        .UseSqlite($"Data Source={hosty.DatabasePath}")
        .AddInterceptors(serviceProvider.GetRequiredService<SqlitePragmaInterceptor>()));

// Jellyfin-compatible surface (M2): credentials/tokens, DTO mapping, browsing, images, streaming.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPinHasher, Argon2idPinHasher>();
builder.Services.AddSingleton<JellyfinServerContext>();
builder.Services.AddSingleton<JellyfinItemMapper>();
builder.Services.AddScoped<JellyfinCredentialService>();
builder.Services.AddScoped<UserDataService>();
builder.Services.AddScoped<JellyfinLibraryService>();
builder.Services.AddScoped<JellyfinImageService>();
builder.Services.AddScoped<JellyfinStreamResolver>();
builder.Services.AddHttpClient(JellyfinImageService.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(15));

// Throttle PIN logins per source IP; the per-username dimension is handled by credential lockout.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(JellyfinSystemEndpoints.AuthRateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromSeconds(30), QueueLimit = 0 }));
});

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
    .AddScheme<AuthenticationSchemeOptions, HostyAuthenticationHandler>(HostyAuthenticationHandler.SchemeName, null)
    .AddScheme<AuthenticationSchemeOptions, JellyfinAuthenticationHandler>(JellyfinAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization(options =>
{
    // The Jellyfin surface authenticates only with Media Server-owned tokens, never Host identity.
    options.AddPolicy(JellyfinAuthenticationHandler.PolicyName, policy =>
    {
        policy.AddAuthenticationSchemes(JellyfinAuthenticationHandler.SchemeName);
        policy.RequireAuthenticatedUser();
    });
});

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
app.UseRateLimiter();

// Liveness/readiness probe on the internal surface.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapCatalogEndpoints();
app.MapTorrentEndpoints();
app.MapIngestEndpoints();
app.MapLibraryEndpoints();
app.MapJellyfinCredentialEndpoints();
app.MapHub<ActivityHub>(ActivityHub.Path);

// Jellyfin-compatible surface served on the public `jellyfin` endpoint.
app.MapJellyfinEndpoints();

// Root marker so the public `jellyfin` port responds to a bare probe.
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
