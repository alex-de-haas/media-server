using System.Net;
using System.Text.Json;
using MediaServer.Api.Hosty;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Hosty;

public sealed class HostyCoreClientTests
{
    private static HostyOptions CoreManaged() => new()
    {
        AppId = "com.haas.media-server",
        ServiceToken = "svc-token",
        CoreOrigin = "http://core.local",
        AppDataDir = Path.Combine(Path.GetTempPath(), "ms-core-client"),
    };

    private static HostyOptions Standalone() => new()
    {
        AppId = "com.haas.media-server",
        ServiceToken = null,
        CoreOrigin = "http://core.local",
        AppDataDir = Path.Combine(Path.GetTempPath(), "ms-core-client"),
    };

    private static HostyCoreClient Build(RecordingHandler handler, HostyOptions options) =>
        new(new SingleClientFactory(handler, options.CoreOrigin), options, NullLogger<HostyCoreClient>.Instance);

    [Fact]
    public async Task CreateBackup_posts_with_bearer_and_note_and_parses_result()
    {
        var handler = new RecordingHandler((_, _) => Json(HttpStatusCode.Created, new { status = "completed", backupId = "bkp_1" }));
        var client = Build(handler, CoreManaged());

        var result = await client.CreateBackupAsync("pre-migration", CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.Completed);
        Assert.Equal("bkp_1", result.BackupId);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/internal/apps/com.haas.media-server/backups", request.Path);
        Assert.Equal("Bearer svc-token", request.Authorization);
        Assert.Equal("pre-migration", request.Body!.RootElement.GetProperty("note").GetString());
    }

    [Fact]
    public async Task CreateBackup_returns_null_when_not_core_managed_without_calling_core()
    {
        var handler = new RecordingHandler((_, _) => Json(HttpStatusCode.Created, new { status = "completed" }));
        var client = Build(handler, Standalone());

        var result = await client.CreateBackupAsync(null, CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PublishNotification_sends_user_audience_lowercase_level_and_dedupe()
    {
        var handler = new RecordingHandler((_, _) => Json(HttpStatusCode.Created, new { status = "created" }));
        var client = Build(handler, CoreManaged());

        var ok = await client.PublishNotificationAsync(
            CoreNotificationLevel.Warning, "Low disk", "Only 1 GB left", link: null, dedupeKey: "disk:cat-1", cancellationToken: CancellationToken.None);

        Assert.True(ok);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/internal/apps/com.haas.media-server/notifications", request.Path);
        var body = request.Body!.RootElement;
        Assert.Equal("broadcast", body.GetProperty("target").GetString());
        Assert.Equal("user", body.GetProperty("audience").GetString());
        Assert.Equal("warning", body.GetProperty("level").GetString());
        Assert.Equal("Low disk", body.GetProperty("title").GetString());
        Assert.Equal("disk:cat-1", body.GetProperty("dedupeKey").GetString());
    }

    [Fact]
    public async Task PublishNotification_returns_false_on_non_success()
    {
        var handler = new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest));
        var client = Build(handler, CoreManaged());

        var ok = await client.PublishNotificationAsync(
            CoreNotificationLevel.Error, "x", null, null, null, cancellationToken: CancellationToken.None);

        Assert.False(ok);
    }

    [Fact]
    public async Task ListDirectoryUsers_parses_assigned_users()
    {
        var handler = new RecordingHandler((_, _) => Json(HttpStatusCode.OK, new
        {
            users = new[]
            {
                new { id = "u1", displayName = (string?)"Ann", email = (string?)"ann@example.com", hostRole = "host.admin" },
                new { id = "u2", displayName = (string?)null, email = (string?)"bob@example.com", hostRole = "host.user" },
            },
        }));
        var client = Build(handler, CoreManaged());

        var users = await client.ListDirectoryUsersAsync(CancellationToken.None);

        Assert.NotNull(users);
        Assert.Equal(2, users!.Count);
        Assert.Equal("u1", users[0].Id);
        Assert.Equal("host.admin", users[0].HostRole);
        Assert.Equal("bob@example.com", users[1].Email);
    }

    [Fact]
    public async Task ListDirectoryUsers_returns_null_on_network_failure()
    {
        var handler = new RecordingHandler((_, _) => throw new HttpRequestException("boom"));
        var client = Build(handler, CoreManaged());

        var users = await client.ListDirectoryUsersAsync(CancellationToken.None);

        Assert.Null(users);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object payload) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json"),
    };

    private sealed record CapturedRequest(HttpMethod Method, string Path, string? Authorization, JsonDocument? Body);

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            JsonDocument? body = null;
            if (request.Content is not null)
            {
                var raw = await request.Content.ReadAsStringAsync(cancellationToken);
                if (!string.IsNullOrEmpty(raw))
                {
                    body = JsonDocument.Parse(raw);
                }
            }

            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.ToString(),
                body));

            return responder(request, cancellationToken);
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler, string baseAddress) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(baseAddress),
        };
    }
}
