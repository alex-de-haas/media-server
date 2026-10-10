using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using HostySdk.App;
using Imposter.Abstractions;
using MediaServer.Api.Mcp;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Api.Tests.Mcp;

public sealed class McpScopedTokenTests : IDisposable
{
    private readonly JellyfinDatabase _database = new();
    private readonly ServiceProvider _services = new ServiceCollection().AddLogging().BuildServiceProvider();
    private readonly CoreHandler _core = new();
    private readonly HttpClient _http;
    private readonly HostyScopedTokenClient _tokens;

    public McpScopedTokenTests()
    {
        _http = new HttpClient(_core) { BaseAddress = new Uri("http://core.test") };
        var factory = IHttpClientFactory.Imposter();
        factory.CreateClient(HostyScopedTokenClient.HttpClientName).Returns(_http);
        _tokens = new(factory.Instance(), new HostyAppOptions
        {
            CoreOrigin = "http://core.test", AppId = "com.haas.media-server", ServiceToken = "app-service-secret",
        });
    }

    [Fact]
    public async Task Scoped_admin_sees_only_reads_and_each_request_revalidates_revocation()
    {
        var first = await Request("tools/list");
        Assert.Equal(200, first.Status);
        var tools = first.Body!["result"]!["tools"]!.AsArray();
        Assert.Equal(14, tools.Count);
        Assert.All(tools, tool => Assert.True(McpToolAccess.IsReadOnly(tool)));
        Assert.DoesNotContain(tools, tool => tool!["name"]!.GetValue<string>() == "scan_catalog");

        _core.Reply = "{\"active\":false}";
        Assert.Equal(401, (await Request("tools/list")).Status);
        Assert.Equal(2, _core.Requests.Count);
        Assert.All(_core.Requests, request =>
        {
            Assert.Equal("/api/internal/apps/com.haas.media-server/token/introspect", request.Path);
            Assert.Equal("Bearer app-service-secret", request.Authorization);
            Assert.Equal("client-scoped-secret", request.Body["token"]!.GetValue<string>());
            Assert.Equal("mcp", request.Body["purpose"]!.GetValue<string>());
            Assert.Null(request.Body["tool"]);
        });
    }

    public static IEnumerable<object[]> WriteTools() => McpToolInvoker.Tools()
        .Where(tool => !McpToolAccess.IsReadOnly(tool))
        .Select(tool => new object[] { tool!["name"]!.GetValue<string>() });

    [Theory]
    [MemberData(nameof(WriteTools))]
    public async Task Guessed_write_calls_are_refused_before_dispatch_even_for_admins(string tool)
    {
        // The invoker is deliberately absent: reaching dispatch would throw, instead of returning
        // a refusal. This pairs discovery filtering with enforcement on every write tool.
        var response = await Request("tools/call", new JsonObject { ["name"] = tool });
        Assert.Equal(200, response.Status);
        Assert.True(response.Body!["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("read-only", response.Body.ToJsonString());
        Assert.Equal(tool, Assert.Single(_core.Requests).Body["tool"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("[]", null, false, false)]
    [InlineData("[\"mcp:write\"]", null, false, false)]
    [InlineData("[\"MCP:READ\"]", null, false, false)]
    [InlineData("[\"mcp:read\"]", null, true, false)]
    [InlineData("[\"mcp:read\",\"mcp:invoke\"]", null, true, false)]
    [InlineData("[\"mcp:invoke\"]", "hosty.harness", false, false)]
    [InlineData("[\"mcp:read\"]", "hosty.harness", true, false)]
    [InlineData("[\"mcp:read\",\"mcp:invoke\"]", "hosty.harness", true, true)]
    public async Task Scopes_and_assistant_identity_determine_authority(
        string scopes, string? assistant, bool read, bool mutate)
    {
        var reply = JsonNode.Parse(_core.Reply)!;
        reply["scopes"] = JsonNode.Parse(scopes);
        reply["callerAppId"] = assistant;
        _core.Reply = reply.ToJsonString();
        await using var db = _database.Create();
        var caller = await McpCallerIdentity.ResolveAsync("Bearer client-scoped-secret", db, _tokens, default);
        Assert.NotNull(caller);
        Assert.True(caller.IsAdministrator);
        Assert.Equal(read, caller.CanRead);
        Assert.Equal(mutate, caller.CanMutate);
        Assert.Equal(read ? mutate ? 22 : 14 : 0, McpToolAccess.VisibleTools(caller).Count);
        Assert.Equal(read ? 200 : 403, (await Request("initialize")).Status);
    }

    [Fact]
    public async Task Scoped_subject_is_resolved_without_creating_or_borrowing_an_app_account()
    {
        await using var db = _database.Create();
        db.AppUsers.Add(new() { HostUserId = "other-user", DisplayName = "Other" });
        await db.SaveChangesAsync();
        var caller = await McpCallerIdentity.ResolveAsync("Bearer client-scoped-secret", db, _tokens, default);
        Assert.NotNull(caller);
        Assert.Equal("host-user", caller.HostUserId);
        Assert.Null(caller.AppUserId);

        var user = new MediaServer.Api.Data.AppUser { HostUserId = "host-user", DisplayName = "Caller" };
        db.AppUsers.Add(user);
        await db.SaveChangesAsync();
        _core.Reply = _core.Reply.Replace("host.admin", "host.user", StringComparison.Ordinal);
        caller = await McpCallerIdentity.ResolveAsync("Bearer client-scoped-secret", db, _tokens, default);
        Assert.Equal(user.Id, caller!.AppUserId);
        Assert.False(caller.IsAdministrator);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Basic abc")]
    [InlineData("Bearer ")]
    public async Task Missing_bearer_is_401_without_contacting_core(string? header)
    {
        Assert.Equal(401, (await Request("initialize", authorization: header)).Status);
        Assert.Empty(_core.Requests);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("network")]
    [InlineData("timeout")]
    [InlineData("json")]
    public async Task Unavailable_or_unreadable_introspection_is_503_not_401(string failure)
    {
        _core.Failure = failure;
        var response = await Request("initialize");
        Assert.Equal(503, response.Status);
        Assert.DoesNotContain("secret", response.Body!.ToJsonString());
    }

    [Fact]
    public async Task Caller_cancellation_is_not_misreported_as_a_core_outage()
    {
        _core.Failure = "timeout";
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Request("initialize", cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Authenticated_notification_has_202_empty_body_and_no_content_type()
    {
        var response = await Request("notifications/initialized");
        Assert.Equal(202, response.Status);
        Assert.Null(response.Body);
        Assert.Null(response.ContentType);
        Assert.Single(_core.Requests);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"annotations\":\"true\"}")]
    [InlineData("{}")]
    [InlineData("{\"readOnlyHint\":true}")]
    [InlineData("{\"annotations\":{}}")]
    [InlineData("{\"annotations\":{\"readOnlyHint\":\"true\"}}")]
    [InlineData("{\"annotations\":{\"readOnlyHint\":false}}")]
    public void Missing_or_non_boolean_annotations_fail_closed(string tool)
        => Assert.False(McpToolAccess.IsReadOnly(JsonNode.Parse(tool)));

    private async Task<(int Status, JsonNode? Body, string? ContentType)> Request(
        string method, JsonNode? parameters = null, string? authorization = "Bearer client-scoped-secret",
        CancellationToken cancellationToken = default)
    {
        await using var db = _database.Create();
        var context = new DefaultHttpContext { RequestServices = _services };
        if (authorization is not null) context.Request.Headers.Authorization = authorization;
        context.Response.Body = new MemoryStream();
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters };
        if (!method.StartsWith("notifications/", StringComparison.Ordinal)) body["id"] = 1;
        var result = await McpEndpoints.HandleAsync(body, context.Request, null!, db, _tokens, cancellationToken);
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        var text = await new StreamReader(context.Response.Body).ReadToEndAsync(cancellationToken);
        return (context.Response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text), context.Response.ContentType);
    }

    public void Dispose()
    {
        _http.Dispose();
        _services.Dispose();
        _database.Dispose();
    }

    private sealed class CoreHandler : HttpMessageHandler
    {
        public string Reply = "{\"active\":true,\"sub\":\"host-user\",\"role\":\"host.admin\",\"scopes\":[\"mcp:read\"]}";
        public string? Failure;
        public List<(string Path, string? Authorization, JsonNode Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(),
                JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!));
            if (Failure == "network") throw new HttpRequestException("offline");
            if (Failure == "timeout") throw new TaskCanceledException("timeout");
            return new HttpResponseMessage(Failure == "http" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new StringContent(Failure == "json" ? "broken-json" : Reply, Encoding.UTF8, "application/json"),
            };
        }
    }
}
