using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.Library;
using MediaServer.Api.Tests.Jellyfin;
using MediaServer.Api.WatchHistory;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MediaServer.Api.Tests.WatchHistory;

/// <summary>Exercises the real routes and authorization middleware without opening a server port.</summary>
public sealed class WatchHistoryEndpointTests : IAsyncLifetime
{
    private const string CalendarPath = "/api/watch-history/calendar?from=2026-07-01T00:00:00Z&toExclusive=2026-08-01T00:00:00Z";
    private const string UndatedPath = "/api/watch-history/calendar/undated?kind=Movie";
    private readonly JellyfinDatabase _database = new();
    private readonly TestTimeProvider _time = new(DateTimeOffset.Parse("2026-07-24T12:00:00Z"));
    private readonly Guid _otherEntryId = Guid.NewGuid();
    private readonly Guid _ownEntryId = Guid.NewGuid();
    private WebApplication _app = null!;
    private RequestDelegate _pipeline = null!;
    private int _currentUserId;
    private int _otherUserId;

    public Task InitializeAsync()
    {
        var current = AddUser("host-current", "Current viewer", "current@example.com");
        var other = AddUser("host-other", "Other viewer", "other@example.com");
        _currentUserId = current.Id;
        _otherUserId = other.Id;

        var catalogId = Guid.NewGuid();
        _database.Context.Catalogs.Add(new Catalog
        {
            Id = catalogId, Name = "Movies", Type = CatalogType.Movie, Root = "/test/movies",
            CreatedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow(),
        });
        AddPlay(catalogId, current.Id, "Own dated film", _ownEntryId, "2026-07-04T19:00:00Z");
        AddPlay(catalogId, current.Id, "Own undated film", Guid.NewGuid(), null);
        AddPlay(catalogId, other.Id, "Other dated film", _otherEntryId, "2026-07-10T21:00:00Z");
        AddPlay(catalogId, other.Id, "Other undated film", Guid.NewGuid(), null);
        _database.Context.SaveChanges();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_database.Context);
        builder.Services.AddSingleton(new WatchHistoryCalendarService(_database.Context, TestSettings.English));
        builder.Services.AddSingleton(new WatchHistoryEntryService(
            _database.Context,
            new WatchHistoryRecorder(_database.Context, _time),
            new LibraryDeleteService(_database.Context,
                new LibraryFileEraser(new CatalogPathSandbox(), NullLogger<LibraryFileEraser>.Instance)),
            _time));
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, PrincipalAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy(AppRoles.AdminPolicy, policy =>
            policy.AddAuthenticationSchemes("test").RequireAuthenticatedUser().RequireRole(AppRoles.Admin)));
        _app = builder.Build();

        var pipeline = new ApplicationBuilder(_app.Services);
        pipeline.UseRouting();
        pipeline.UseAuthentication();
        pipeline.UseAuthorization();
        pipeline.UseEndpoints(routes => routes.MapWatchHistoryEndpoints());
        _pipeline = pipeline.Build();
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData(CalendarPath)]
    [InlineData(UndatedPath)]
    [InlineData("/api/watch-history/users")]
    public async Task Anonymous_requests_are_unauthorized(string path) =>
        Assert.Equal(StatusCodes.Status401Unauthorized, (await SendAsync(path, role: null)).StatusCode);

    [Fact]
    public async Task Only_an_administrator_can_list_selectable_users()
    {
        Assert.Equal(StatusCodes.Status403Forbidden,
            (await SendAsync("/api/watch-history/users", AppRoles.User)).StatusCode);

        var unnamed = AddUser("private-host-unnamed", " ", " ");
        var emailNamed = AddUser("private-host-email", null, "email-fallback@example.com");
        var response = await SendAsync("/api/watch-history/users", AppRoles.Admin);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        var users = response.Json.EnumerateArray().ToArray();
        Assert.Equal(4, users.Length);
        Assert.All(users, user => Assert.Equal(["id", "displayName", "isCurrentUser"],
            user.EnumerateObject().Select(property => property.Name)));
        Assert.Equal(_currentUserId, Assert.Single(users, user => user.GetProperty("isCurrentUser").GetBoolean())
            .GetProperty("id").GetInt32());
        Assert.Equal($"User {unnamed.Id}", users.Single(user => user.GetProperty("id").GetInt32() == unnamed.Id)
            .GetProperty("displayName").GetString());
        Assert.Equal("email-fallback@example.com", users.Single(user => user.GetProperty("id").GetInt32() == emailNamed.Id)
            .GetProperty("displayName").GetString());
        Assert.DoesNotContain("private-host", response.Body);
        Assert.DoesNotContain("current@example.com", response.Body);
    }

    [Theory]
    [InlineData(CalendarPath, AppRoles.User, false)]
    [InlineData(CalendarPath, AppRoles.User, true)]
    [InlineData(CalendarPath, AppRoles.Admin, false)]
    [InlineData(CalendarPath, AppRoles.Admin, true)]
    [InlineData(UndatedPath, AppRoles.User, false)]
    [InlineData(UndatedPath, AppRoles.User, true)]
    [InlineData(UndatedPath, AppRoles.Admin, false)]
    [InlineData(UndatedPath, AppRoles.Admin, true)]
    public async Task Default_and_explicit_self_selection_preserve_the_callers_history(string path, string role, bool selectSelf)
    {
        var response = await SendAsync(path + (selectSelf ? $"&userId={_currentUserId}" : ""), role);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Contains(path == CalendarPath ? "Own dated film" : "Own undated film", response.Body);
        Assert.DoesNotContain("Other", response.Body);
    }

    [Theory]
    [InlineData(CalendarPath)]
    [InlineData(UndatedPath)]
    public async Task Administrator_reads_the_selected_users_history(string path)
    {
        // The current validated principal decides administration, rather than a stale database role.
        Assert.Equal(AppUserRole.User, _database.Context.AppUsers.Single(user => user.Id == _currentUserId).Role);
        var response = await SendAsync(path + $"&userId={_otherUserId}", AppRoles.Admin);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Contains(path == CalendarPath ? "Other dated film" : "Other undated film", response.Body);
        Assert.DoesNotContain("Own", response.Body);
        if (path == CalendarPath)
        {
            Assert.Equal(DateTimeOffset.Parse("2026-07-10T21:00:00Z"),
                response.Json.GetProperty("latestWatchedAt").GetDateTimeOffset());
            Assert.Equal(1, response.Json.GetProperty("undated").GetProperty("movies").GetInt32());
        }
    }

    [Theory]
    [InlineData(CalendarPath)]
    [InlineData(UndatedPath)]
    public async Task Ordinary_users_cannot_probe_existing_or_missing_users(string path)
    {
        // Even a stale app-user admin flag must not override a current non-admin principal.
        _database.Context.AppUsers.Single(user => user.Id == _currentUserId).Role = AppUserRole.Admin;
        await _database.Context.SaveChangesAsync();

        Assert.Equal(StatusCodes.Status403Forbidden,
            (await SendAsync(path + $"&userId={_otherUserId}", AppRoles.User)).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden,
            (await SendAsync(path + "&userId=2147483647", AppRoles.User)).StatusCode);
    }

    [Theory]
    [InlineData(CalendarPath)]
    [InlineData(UndatedPath)]
    public async Task Administrator_gets_not_found_for_a_missing_target(string path) =>
        Assert.Equal(StatusCodes.Status404NotFound,
            (await SendAsync(path + "&userId=2147483647", AppRoles.Admin)).StatusCode);

    [Theory]
    [InlineData(CalendarPath)]
    [InlineData(UndatedPath)]
    [InlineData("/api/watch-history/users")]
    public async Task Unprovisioned_callers_cannot_read_history_or_user_lists(string path) =>
        Assert.Equal(StatusCodes.Status401Unauthorized,
            (await SendAsync(path, AppRoles.Admin, hostUserId: "not-provisioned")).StatusCode);

    [Fact]
    public async Task Selecting_another_user_does_not_grant_mutation_access()
    {
        var path = $"/api/watch-history/entries/{_otherEntryId}?userId={_otherUserId}";
        var patch = await SendAsync(path, AppRoles.Admin, method: "PATCH", body: "{\"watchedAt\":\"2026-07-05T20:00:00Z\"}");
        var delete = await SendAsync(path, AppRoles.Admin, method: "DELETE");

        Assert.Equal(StatusCodes.Status404NotFound, patch.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, delete.StatusCode);
        var entry = await _database.Context.PlaybackHistoryEntries.SingleAsync(row => row.Id == _otherEntryId);
        Assert.Equal(DateTimeOffset.Parse("2026-07-10T21:00:00Z"), entry.WatchedAt);
    }

    [Fact]
    public async Task Owners_can_still_correct_and_delete_their_own_play()
    {
        var path = $"/api/watch-history/entries/{_ownEntryId}";
        Assert.Equal(StatusCodes.Status204NoContent, (await SendAsync(path, AppRoles.User,
            method: "PATCH", body: "{\"watchedAt\":\"2026-07-05T20:00:00Z\"}")).StatusCode);
        Assert.Equal(DateTimeOffset.Parse("2026-07-05T20:00:00Z"),
            (await _database.Context.PlaybackHistoryEntries.SingleAsync(row => row.Id == _ownEntryId)).WatchedAt);
        Assert.Equal(StatusCodes.Status204NoContent, (await SendAsync(path, AppRoles.User, method: "DELETE")).StatusCode);
        Assert.False(await _database.Context.PlaybackHistoryEntries.AnyAsync(row => row.Id == _ownEntryId));
    }

    [Fact]
    public async Task Invalid_calendar_ranges_are_still_rejected() =>
        Assert.Equal(StatusCodes.Status400BadRequest,
            (await SendAsync("/api/watch-history/calendar?from=2026-07-10T00:00:00Z&toExclusive=2026-07-01T00:00:00Z", AppRoles.Admin)).StatusCode);

    private AppUser AddUser(string hostUserId, string? displayName, string? email)
    {
        var user = new AppUser
        {
            HostUserId = hostUserId, DisplayName = displayName, Email = email, Role = AppUserRole.User,
            CreatedAt = _time.GetUtcNow(), LastSeenAt = _time.GetUtcNow(),
        };
        _database.Context.AppUsers.Add(user);
        _database.Context.SaveChanges();
        return user;
    }

    private void AddPlay(Guid catalogId, int userId, string title, Guid entryId, string? watchedAt)
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(), CatalogId = catalogId, Kind = MediaKind.Movie, Title = title,
            AddedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow(),
        };
        _database.Context.MediaItems.Add(item);
        _database.Context.PlaybackHistoryEntries.Add(new PlaybackHistoryEntry
        {
            Id = entryId, AppUserId = userId, MediaItemId = item.Id, CreatedAt = _time.GetUtcNow(),
            WatchedAt = watchedAt is null ? null : DateTimeOffset.Parse(watchedAt), Origin = PlaybackHistoryOrigin.Manual,
        });
    }

    private async Task<Response> SendAsync(string path, string? role, string hostUserId = "host-current",
        string method = "GET", string? body = null)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var uri = new Uri("http://localhost" + path);
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = method;
        context.Request.Path = uri.AbsolutePath;
        context.Request.QueryString = new QueryString(uri.Query);
        if (role is not null)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, hostUserId), new Claim(ClaimTypes.Role, role),
            ], "test"));
        }
        if (body is not null)
        {
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new RequestBodyDetectionFeature());
            context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            context.Request.ContentLength = context.Request.Body.Length;
        }
        await using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;
        await _pipeline(context);
        return new Response(context.Response.StatusCode, Encoding.UTF8.GetString(responseBody.ToArray()));
    }

    private sealed record Response(int StatusCode, string Body)
    {
        public JsonElement Json => JsonSerializer.Deserialize<JsonElement>(Body);
    }

    private sealed class RequestBodyDetectionFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    public sealed class PrincipalAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            Context.User.Identity?.IsAuthenticated == true
                ? AuthenticateResult.Success(new AuthenticationTicket(Context.User, Scheme.Name))
                : AuthenticateResult.NoResult());
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        _database.Dispose();
    }
}
