using System.Security.Claims;
using System.Text.Json.Serialization;
using MediaServer.Api.Catalogs;
using MediaServer.Api.Collections;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Diagnostics;
using HostySdk.App;
using MediaServer.Api.Hosty;
using MediaServer.Api.IO;
using MediaServer.Api.Metadata;
using MediaServer.Api.Recommendations;
using MediaServer.Api.WatchHistory;
using MediaServer.Api.WatchHistory.Trakt;
using MediaServer.Api.Jobs;
using MediaServer.Api.Library;
using MediaServer.Api.Mux;
using MediaServer.Api.Organizer;
using MediaServer.Api.People;
using MediaServer.Api.Pipeline;
using MediaServer.Api.Pipeline.Stages;
using MediaServer.Api.Probe;
using MediaServer.Api.Realtime;
using MediaServer.Api.Torrents;
using MediaServer.Api.Transcoding;
using MediaServer.Api.Watchlist;
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

// Export traces/metrics/logs over OTLP to the Hosty collector when Core injects the OTEL_* env
// (docker runtime + observability enabled); a no-op otherwise. See Hosty/HostyTelemetry.cs.
builder.AddHostyTelemetry();

// Internal /api surface serializes enums by name (the web client uses string enum values like
// "Movie"); the Jellyfin surface keeps its own options in JellyfinJson.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Manifest settings (TMDb key, languages, ffprobe, torrent tunables) + catalog-root mounts.
var settings = MediaServerSettings.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(settings);

// Operator-editable settings persisted in the DB (e.g. custom release groups stripped before identify).
builder.Services.AddScoped<AppSettingsService>();

// Phase 0 playback observation (docs/planning/trakt-watched-state-sync.md). Off unless the operator
// turns it on, and the writer simply does not exist then, so the recorder short-circuits.
if (settings.PlaybackDiagnosticsEnabled)
{
    builder.Services.AddSingleton(serviceProvider => new PlaybackDiagnosticsWriter(
        Path.Combine(ResolveLogsDirectory(hosty), "playback-diagnostics.log"),
        serviceProvider.GetRequiredService<ILogger<PlaybackDiagnosticsWriter>>()));
}

builder.Services.AddScoped<PlaybackDiagnostics>(serviceProvider =>
    new PlaybackDiagnostics(serviceProvider.GetService<PlaybackDiagnosticsWriter>()));

// Filesystem primitives + catalog management.
builder.Services.AddSingleton<IFilesystemInspector, FilesystemInspector>();
builder.Services.AddSingleton<ICatalogPathSandbox, CatalogPathSandbox>();
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<CatalogHealthService>();
builder.Services.AddHostedService<CatalogHealthWorker>();
builder.Services.AddScoped<IOrganizer, OrganizerService>();

// Real-time SSE notifier + in-process pipeline queue.
builder.Services.AddSingleton<SseRealtimeNotifier>();
builder.Services.AddSingleton<IRealtimeNotifier>(serviceProvider => serviceProvider.GetRequiredService<SseRealtimeNotifier>());
builder.Services.AddSingleton<IPipelineQueue, PipelineQueue>();

// Torrent engine (hosted) + coordinator that bridges it to persistence and the pipeline. Downloading is
// delegated to the external torrent-engine app (a required dependency) over HTTP/SSE; it runs VPN-isolated
// in its own container. When no dependency URL is injected (dev without the engine, or a misconfiguration)
// a disabled engine keeps the rest of the app working while downloading is unavailable.
if (settings.TorrentEngineUrl is { Length: > 0 } torrentEngineUrl)
{
    builder.Services.AddSingleton(serviceProvider => new RemoteTorrentEngine(
        new HttpClient { BaseAddress = new Uri(torrentEngineUrl), Timeout = Timeout.InfiniteTimeSpan },
        settings,
        serviceProvider.GetRequiredService<ILogger<RemoteTorrentEngine>>()));
    builder.Services.AddSingleton<ITorrentEngine>(serviceProvider => serviceProvider.GetRequiredService<RemoteTorrentEngine>());
    builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<RemoteTorrentEngine>());
}
else
{
    builder.Services.AddSingleton<ITorrentEngine, DisabledTorrentEngine>();
}

builder.Services.AddHostedService<TorrentCoordinator>();
builder.Services.AddScoped<TorrentService>();
builder.Services.AddScoped<DownloadFileService>();
builder.Services.AddScoped<DownloadDeletionService>();

// Transcode engine. Re-encoding is delegated to the external transcode-engine app (an optional dependency)
// over HTTP/SSE; it runs ffmpeg with the host's /dev/dri passed through for VAAPI hardware encoding. When no
// dependency URL is injected, a disabled engine keeps the rest of the app working while transcoding is
// unavailable. See docs/ideas/transcode-engine-app.md.
if (settings.TranscodeEngineUrl is { Length: > 0 } transcodeEngineUrl)
{
    builder.Services.AddSingleton(serviceProvider => new RemoteTranscodeEngine(
        new HttpClient { BaseAddress = new Uri(transcodeEngineUrl), Timeout = Timeout.InfiniteTimeSpan },
        settings,
        serviceProvider.GetRequiredService<ILogger<RemoteTranscodeEngine>>()));
    builder.Services.AddSingleton<ITranscodeEngine>(serviceProvider => serviceProvider.GetRequiredService<RemoteTranscodeEngine>());
    builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<RemoteTranscodeEngine>());
}
else
{
    builder.Services.AddSingleton<ITranscodeEngine, DisabledTranscodeEngine>();
}

builder.Services.AddHostedService<TranscodeCoordinator>();
builder.Services.AddScoped<TranscodeService>();
builder.Services.AddScoped<TranscodeOutputImporter>();

// Identify / probe / enrich building blocks.
builder.Services.AddSingleton<INameParser, NameParser>();
builder.Services.AddSingleton<IMediaProbe, FfprobeMediaProbe>();
builder.Services.AddSingleton<IAudioMuxer, FfmpegAudioMuxer>();
builder.Services.AddScoped<AudioMuxService>();
builder.Services.AddHttpClient(TmdbMetadataProvider.HttpClientName, client =>
{
    client.BaseAddress = new Uri("https://api.themoviedb.org/");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddSingleton<IMetadataProvider, TmdbMetadataProvider>();

// Watched-history providers resolve by stable key, like the metadata providers above.
// Scoped rather than singleton: the adapters it resolves hold a DbContext, and a singleton registry
// would capture one for the process lifetime.
builder.Services.AddScoped<IWatchHistoryProviderRegistry, WatchHistoryProviderRegistry>();
builder.Services.AddSingleton<IWatchHistoryCredentialStore, HostyCoreCredentialStore>();
builder.Services.AddHttpClient(TraktOAuthClient.HttpClientName, client =>
{
    client.BaseAddress = new Uri("https://api.trakt.tv/");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddScoped<TraktOAuthClient>();
// Scoped, not singleton: it holds a DbContext. Registered against the interface as well so the
// registry picks it up without naming the concrete type.
builder.Services.AddScoped<TraktAuthorizationService>();
builder.Services.AddScoped<IWatchHistoryProviderAuthorization>(provider =>
    provider.GetRequiredService<TraktAuthorizationService>());
builder.Services.AddSingleton<TraktWorkIdCache>();
builder.Services.AddScoped<TraktWorkIdResolver>();
builder.Services.AddScoped<IWatchHistoryProvider, TraktWatchHistoryProvider>();
builder.Services.AddScoped<WatchHistoryIdentityMapper>();
builder.Services.AddScoped<WatchHistoryRecorder>();
builder.Services.AddScoped<WatchHistoryDeliveryService>();
builder.Services.AddScoped<WatchHistorySyncPreviewService>();
builder.Services.AddScoped<WatchHistorySyncApplyService>();
builder.Services.AddScoped<WatchHistoryCalendarService>();

builder.Services.AddScoped<IRecommendationProviderRegistry, RecommendationProviderRegistry>();
builder.Services.AddScoped<RecommendationSeedSelector>();
builder.Services.AddScoped<ITmdbRecommendationSource, TmdbRecommendationSource>();
builder.Services.AddScoped<IRecommendationProvider, LibraryRecommendationProvider>();
builder.Services.AddScoped<IRecommendationProvider, MediaServer.Api.Recommendations.Trakt.TraktRecommendationProvider>();
builder.Services.AddScoped<ITmdbPosterLookup, TmdbPosterLookup>();
builder.Services.AddScoped<RecommendationFeedService>();
builder.Services.AddHostedService<WatchHistoryDeliveryWorker>();
// An abandoned device flow is never polled again, so nothing else would remove its row or its stored
// device code.
builder.Services.AddScoped<WatchHistoryAuthorizationCleanupService>();
builder.Services.AddHostedService<WatchHistoryAuthorizationCleanupWorker>();
builder.Services.AddSingleton<IReleaseScheduleProvider, TmdbReleaseScheduleProvider>();

// Pipeline: stages, supporting services, orchestrator, and the worker + reconciler hosted services.
builder.Services.AddScoped<IdentifyService>();
builder.Services.AddScoped<PersonSyncService>();
builder.Services.AddScoped<CollectionSyncService>();
builder.Services.AddScoped<EnrichService>();
builder.Services.AddScoped<JobService>();
builder.Services.AddScoped<IPipelineStage, IntakeStage>();
builder.Services.AddScoped<IPipelineStage, DownloadStage>();
builder.Services.AddScoped<IPipelineStage, IdentifyStage>();
builder.Services.AddScoped<IPipelineStage, MuxStage>();
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

// Periodic online-backup snapshot so scheduled host backups capture a consistent DB copy (no quiesce hook).
builder.Services.AddSingleton<SqliteSnapshotService>();
builder.Services.AddHostedService<DatabaseSnapshotWorker>();

// Internal UI-facing read layer for the `/api` (camelCase) surface — projects the domain into UI DTOs.
// Surface-neutral: it shares the domain + UserDataService with Jellyfin but never the Jellyfin DTOs.
builder.Services.AddScoped<LibraryReadService>();
builder.Services.AddScoped<PersonReadService>();
builder.Services.AddScoped<CollectionReadService>();
builder.Services.AddSingleton<LibraryFileEraser>();
builder.Services.AddScoped<LibraryDeleteService>();
builder.Services.AddScoped<LibrarySourceService>();
builder.Services.AddScoped<RemapService>();

// Move a published item between catalogs: a background job (files moved, ids re-minted, sources merged).
builder.Services.AddSingleton<ILibraryMoveQueue, LibraryMoveQueue>();
builder.Services.AddScoped<LibraryMoveCoordinator>();
builder.Services.AddScoped<LibraryMoveService>();
builder.Services.AddScoped<LibraryMoveGuard>(); // Blocks item/source mutations while their move is in flight.
builder.Services.AddHostedService<LibraryMoveWorker>();

// Scheduled scans (missing-file drift) + on-demand metadata refresh.
builder.Services.AddScoped<LibraryMaintenanceService>();
builder.Services.AddHostedService<LibraryScanWorker>();

// One-off, idempotent backfill of cast/crew from already-cached metadata for pre-existing items.
builder.Services.AddScoped<PersonBackfillService>();
builder.Services.AddHostedService<PersonBackfillWorker>();
builder.Services.AddScoped<CollectionBackfillService>();
builder.Services.AddHostedService<CollectionBackfillWorker>();

// Library import: scan a catalog root for orphan media files and ingest them from the identify stage.
builder.Services.AddScoped<LibraryImportService>();

// Release tracking (M5a): per-user watchlist + reminders. The date-sync loop is the only TMDb caller
// (24h cadence + on-demand queue); the dispatch loop is local-only and frequent.
builder.Services.AddSingleton<IWatchlistSyncQueue, WatchlistSyncQueue>();
builder.Services.AddScoped<WatchlistSyncService>();
builder.Services.AddHostedService<WatchlistSyncWorker>();
builder.Services.AddScoped<ReminderDispatchService>();
builder.Services.AddHostedService<ReminderDispatchWorker>();
builder.Services.AddScoped<WatchlistService>();
builder.Services.AddScoped<ReminderService>();
builder.Services.AddScoped<WatchlistLibraryLinker>();

// Catalog-wide metadata refresh: an admin-triggered background job that re-enriches every identified item.
builder.Services.AddSingleton<ICatalogRefreshQueue, CatalogRefreshQueue>();
builder.Services.AddScoped<CatalogRefreshCoordinator>();
builder.Services.AddScoped<CatalogMetadataRefreshService>();
builder.Services.AddHostedService<CatalogRefreshWorker>();

// Jellyfin-compatible surface (M2): credentials/tokens, DTO mapping, browsing, images, streaming.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPinHasher, Argon2idPinHasher>();
builder.Services.AddSingleton<JellyfinServerContext>();
builder.Services.AddSingleton<JellyfinItemMapper>();
builder.Services.AddScoped<JellyfinCredentialService>();
builder.Services.AddScoped<UserDataService>();
builder.Services.AddScoped<JellyfinCatalogArtwork>();
builder.Services.AddScoped<JellyfinCollectionService>();
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

// Identity validation against Core (HostySdk.App): validators, the platform-decided 30s
// positive cache, the hosty-core HttpClient, and the Hosty authentication scheme. The role
// mapper turns the raw Host role into this app's admin/user claim.
var hostyAuth = builder.Services.AddHostyAppAuthentication(
    new HostyAppOptions
    {
        AppId = hosty.AppId,
        CoreOrigin = hosty.CoreOrigin,
        CorePublicOrigin = hosty.CorePublicOrigin,
        ServiceToken = hosty.ServiceToken,
        AppDataDir = hosty.AppDataDir,
        RunningInContainer = hosty.RunningInContainer,
    },
    configure: options =>
    {
        options.MapHostRole = role =>
            string.Equals(role, "host.admin", StringComparison.OrdinalIgnoreCase) ? AppRoles.Admin : AppRoles.User;
    });

// Talks to Core's internal app APIs (backups, notifications, directory) with the service token bearer.
builder.Services.AddSingleton<HostyCoreClient>();
builder.Services.AddSingleton<IHostyCoreClient>(provider => provider.GetRequiredService<HostyCoreClient>());
// Same object behind both contracts; the secrets half is separate because its failures must not be
// folded into null the way the fire-and-forget calls are.
builder.Services.AddSingleton<IHostyCoreSecrets>(provider => provider.GetRequiredService<HostyCoreClient>());

// Polls Core's scoped directory (no webhooks): upserts assigned users, revokes Jellyfin access on unassign/disable.
builder.Services.AddScoped<DirectoryReconcileService>();
builder.Services.AddHostedService<DirectoryReconcileWorker>();

hostyAuth.AddScheme<AuthenticationSchemeOptions, JellyfinAuthenticationHandler>(JellyfinAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization(options =>
{
    // The Jellyfin surface authenticates only with Media Server-owned tokens, never Host identity.
    options.AddPolicy(JellyfinAuthenticationHandler.PolicyName, policy =>
    {
        policy.AddAuthenticationSchemes(JellyfinAuthenticationHandler.SchemeName);
        policy.RequireAuthenticatedUser();
    });

    // Admin-only management surfaces (e.g. catalog configuration) on the Host-identity scheme.
    options.AddPolicy(AppRoles.AdminPolicy, policy =>
    {
        policy.AddAuthenticationSchemes(HostyAuthenticationHandler.SchemeName);
        policy.RequireAuthenticatedUser();
        policy.RequireRole(AppRoles.Admin);
    });
});

var app = builder.Build();

// Apply schema migrations on startup, wrapped in an on-demand Core backup so a failed migration is
// recoverable (POST /api/internal/apps/{appId}/backups). The backup is requested only when there is
// actually pending schema work, so ordinary restarts don't accumulate snapshots; on migration failure
// we raise an operator notification and rethrow (the app must not run against a half-migrated schema).
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var database = services.GetRequiredService<MediaServerDbContext>();
    var core = services.GetRequiredService<IHostyCoreClient>();
    var startupLogger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup.Migrations");

    var pending = (await database.Database.GetPendingMigrationsAsync()).ToList();
    if (pending.Count > 0)
    {
        startupLogger.LogInformation("Applying {Count} pending migration(s): {Migrations}.", pending.Count, string.Join(", ", pending));

        if (core.IsEnabled)
        {
            var backup = await core.CreateBackupAsync($"Before applying {pending.Count} Media Server migration(s)", CancellationToken.None);
            if (backup is null)
            {
                startupLogger.LogWarning("Pre-migration Core backup could not be created; proceeding with migration.");
            }
        }

        try
        {
            await database.Database.MigrateAsync();
        }
        catch (Exception exception)
        {
            startupLogger.LogError(exception, "Database migration failed.");
            await core.PublishNotificationAsync(
                CoreNotificationLevel.Error,
                "Media Server: database migration failed",
                "Applying database schema migrations failed on startup. The previous data was backed up; the app may not function until this is resolved.",
                link: null,
                dedupeKey: "media-server:migration-failed",
                cancellationToken: CancellationToken.None);
            throw;
        }
    }
}

// Diagnostic: append every incoming request (incl. 404s) to a dedicated file next to the Hosty logs,
// so we can see exactly which routes clients like Infuse call. Outermost so it records the final status.
// Development gets it implicitly; PLAYBACK_DIAGNOSTICS turns it on in any runtime, because the docker
// runtime the app ships with is never Development and so could never be observed.
if (app.Environment.IsDevelopment() || settings.PlaybackDiagnosticsEnabled)
{
    var requestLogPath = Path.Combine(ResolveLogsDirectory(hosty), "requests.log");
    app.UseMiddleware<RequestLoggingMiddleware>(requestLogPath);
    app.Logger.LogInformation("Request logging enabled -> {Path}", requestLogPath);
}

if (settings.PlaybackDiagnosticsEnabled)
{
    app.Logger.LogInformation(
        "Playback diagnostics enabled -> {Path}",
        app.Services.GetRequiredService<PlaybackDiagnosticsWriter>().Path);
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Liveness/readiness probe on the internal surface.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapCatalogEndpoints();
app.MapTorrentEndpoints();
app.MapTranscodeEndpoints();
app.MapIngestEndpoints();
app.MapLibraryEndpoints();
app.MapPersonEndpoints();
app.MapCollectionEndpoints();
app.MapMetadataEndpoints();
app.MapWatchlistEndpoints();
app.MapSettingsEndpoints();
app.MapJellyfinCredentialEndpoints();
app.MapWatchHistoryEndpoints();
app.MapRecommendationEndpoints();
app.MapRealtimeEndpoints();

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

// Hosty keeps app logs beside the data directory (apps/<id>/logs); fall back to the data directory
// itself when that layout is absent, e.g. a standalone local run outside Core.
static string ResolveLogsDirectory(HostyOptions hosty)
{
    var appRoot = Path.GetDirectoryName(hosty.AppDataDir);
    var logsDir = appRoot is not null && Directory.Exists(Path.Combine(appRoot, "logs"))
        ? Path.Combine(appRoot, "logs")
        : hosty.AppDataDir;
    Directory.CreateDirectory(logsDir);
    return logsDir;
}
