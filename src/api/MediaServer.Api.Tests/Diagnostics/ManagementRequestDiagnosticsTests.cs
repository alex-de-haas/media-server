using System.Data.Common;
using System.Net;
using System.Text.Encodings.Web;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Diagnostics;
using MediaServer.Api.Library;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaServer.Api.Tests.Diagnostics;

public sealed class ManagementRequestDiagnosticsTests
{
    [Fact]
    public async Task Startup_filter_observes_requests_before_implicit_WebApplication_authentication()
    {
        var log = new Recorder();
        var observer = new AuthenticationObserver();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton<ILogger<ManagementRequestDiagnostics>>(log);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(observer);
        builder.Services.AddScoped<ManagementRequestDiagnostics>();
        builder.Services.AddTransient<IStartupFilter, ManagementRequestLoggingStartupFilter>();
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, ObservingAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        app.UseRouting();
        app.UseWhen(_ => true, branch => branch.UseAuthentication());
        app.UseAuthorization();
        app.MapGet("/api/management/items", () => Results.Ok()).RequireAuthorization();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient();
            using var response = await client.GetAsync(app.Urls.Single() + "/api/management/items");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.True(observer.DiagnosticsVisible);
            Assert.True(response.Headers.Contains(ManagementRequestDiagnostics.RequestIdHeader));
            Assert.Contains(log.Lines, line => line.Contains("status=401"));
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task List_logs_each_stage_with_the_proxy_id_without_private_values()
    {
        using var database = new JellyfinDatabase();
        var log = new Recorder();
        var clock = new ManualClock();
        using var diagnostics = new ManagementRequestDiagnostics(log, clock);
        using var services = new ServiceCollection().AddSingleton(diagnostics).BuildServiceProvider();
        var context = Context(services);
        var requestId = Guid.NewGuid().ToString();
        context.Request.Headers[ManagementRequestDiagnostics.RequestIdHeader] = requestId;
        context.Request.Headers.Authorization = "Bearer private-token";
        context.Request.QueryString = new("?search=private-title&path=/private/media");
        var middleware = new ManagementRequestLoggingMiddleware(async _ =>
        {
            Assert.Contains(log.Lines, line => line.Contains("stage=authentication"));
            var service = new ManagementService(database.Context, new MediaServerSettings(), diagnostics: diagnostics);
            var result = await service.ListAsync(new(Search: "private-title", Codec: "private-codec"), default);
            Assert.Empty(result.Items);
        });

        await middleware.InvokeAsync(context);

        Assert.Equal(requestId, context.Response.Headers[ManagementRequestDiagnostics.RequestIdHeader]);
        foreach (var stage in new[] { "validate", "catalog-availability", "build-query", "count.all", "count.attention",
            "count.unavailable", "count.bluray", "count.dolby-vision", "count.active", "page" })
        {
            Assert.Contains(log.Lines, line => line.Contains($"stage {stage} started"));
            Assert.Contains(log.Lines, line => line.Contains($"stage {stage} completed"));
        }
        Assert.Contains(log.Lines, line => line.Contains("completed: stage=response, status=200"));
        Assert.All(log.Lines, line => Assert.Contains(requestId, line));
        Assert.DoesNotContain(log.Lines, line => line.Contains("private-") || line.Contains("/private/media"));
        var count = log.Lines.Count;
        clock.Advance(10_000);
        Assert.Equal(count, log.Lines.Count);
        Assert.True(clock.Timer!.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_cancelled_query_preserves_its_stage_and_stops_progress_logging(bool cancel)
    {
        using var database = new JellyfinDatabase();
        using var cancellation = new CancellationTokenSource();
        var log = new Recorder();
        var clock = new ManualClock();
        using var diagnostics = new ManagementRequestDiagnostics(log, clock);
        using var services = new ServiceCollection().AddSingleton(diagnostics).BuildServiceProvider();
        var context = Context(services);
        context.RequestAborted = cancellation.Token;
        Exception failure = cancel ? new OperationCanceledException(cancellation.Token) : new InvalidOperationException("private SQL /media/file token=secret");
        await using var reads = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>()
            .UseSqlite(database.Context.Database.GetDbConnection())
            .AddInterceptors(new InterruptQuery(() =>
            {
                clock.Advance(10_000);
                if (cancel) cancellation.Cancel();
                throw failure;
            })).Options);
        var middleware = new ManagementRequestLoggingMiddleware(async _ =>
            await new ManagementService(reads, new MediaServerSettings(), diagnostics: diagnostics).ListAsync(new(), default));

        var thrown = await Record.ExceptionAsync(() => middleware.InvokeAsync(context));

        Assert.Same(failure, thrown);
        Assert.Contains(log.Lines, line => line.Contains("still running: stage=count.all, stageElapsedMs=10000"));
        Assert.Contains(log.Lines, line => line.Contains($"{(cancel ? "cancelled" : "failed")}: stage=count.all"));
        Assert.DoesNotContain(log.Lines, line => line.Contains("stage count.all completed") || line.Contains("secret") || line.Contains("/media/file"));
        if (cancel) Assert.Contains(log.Lines, line => line.Contains("caller disconnected: stage=count.all"));
        var count = log.Lines.Count;
        clock.Advance(10_000);
        Assert.Equal(count, log.Lines.Count);
        Assert.True(clock.Timer!.Disposed);
    }

    [Fact]
    public async Task Authentication_wait_and_refusal_are_visible_and_untrusted_ids_are_replaced()
    {
        var log = new Recorder();
        var clock = new ManualClock();
        using var diagnostics = new ManagementRequestDiagnostics(log, clock);
        using var services = new ServiceCollection().AddSingleton(diagnostics).BuildServiceProvider();
        var context = Context(services);
        context.Request.Headers[ManagementRequestDiagnostics.RequestIdHeader] = "private-untrusted-id";
        await new ManagementRequestLoggingMiddleware(_ =>
        {
            clock.Advance(10_000);
            context.Response.StatusCode = 401;
            return Task.CompletedTask;
        }).InvokeAsync(context);

        Assert.True(Guid.TryParse(context.Response.Headers[ManagementRequestDiagnostics.RequestIdHeader], out _));
        Assert.Contains(log.Lines, line => line.Contains("still running: stage=authentication"));
        Assert.Contains(log.Lines, line => line.Contains("status=401"));
        Assert.DoesNotContain(log.Lines, line => line.Contains("private-untrusted-id"));
    }

    [Theory]
    [InlineData("/health", "GET")]
    [InlineData("/api/management/items", "POST")]
    public async Task Unrelated_requests_do_not_resolve_diagnostics_or_add_headers(string path, string method)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;
        var called = false;
        await new ManagementRequestLoggingMiddleware(_ => { called = true; return Task.CompletedTask; }).InvokeAsync(context);
        Assert.True(called);
        Assert.False(context.Response.Headers.ContainsKey(ManagementRequestDiagnostics.RequestIdHeader));
    }

    private static DefaultHttpContext Context(IServiceProvider services)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = "/api/management/items";
        context.Request.Method = "GET";
        return context;
    }

    private sealed class InterruptQuery(Action action) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            action();
            return ValueTask.FromResult(result);
        }
    }

    public sealed class AuthenticationObserver
    {
        public bool DiagnosticsVisible { get; set; }
    }

    public sealed class ObservingAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder, AuthenticationObserver observer) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            observer.DiagnosticsVisible = Context.Items.ContainsKey(typeof(ManagementRequestDiagnostics));
            return Task.FromResult(AuthenticateResult.NoResult());
        }
    }

    private sealed class Recorder : ILogger<ManagementRequestDiagnostics>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, error));
    }

    private sealed class ManualClock : TimeProvider
    {
        private long now;
        public ManualTimer? Timer { get; private set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            Timer = new ManualTimer(() => callback(state));
        public void Advance(long milliseconds) { now += milliseconds; Timer?.Tick(); }
    }

    private sealed class ManualTimer(Action callback) : ITimer
    {
        public bool Disposed { get; private set; }
        public void Tick() { if (!Disposed) callback(); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
