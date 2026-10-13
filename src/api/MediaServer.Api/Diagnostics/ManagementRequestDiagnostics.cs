using MediaServer.Api.Library;

namespace MediaServer.Api.Diagnostics;

/// <summary>Request-local timings for management reads, including requests that never finish.</summary>
public sealed class ManagementRequestDiagnostics(ILogger<ManagementRequestDiagnostics> logger, TimeProvider time) : IDisposable
{
    public const string RequestIdHeader = "X-Management-Request-Id";
    private readonly object gate = new();
    private ITimer? timer;
    private bool active;
    private string requestId = "";
    private string stage = "authentication";
    private long started;
    private long stageStarted;

    internal static string? Route(PathString path) => path.Value switch
    {
        "/api/management/items" => "items",
        "/api/library/availability" => "availability",
        { } value when value.StartsWith("/api/management/items/", StringComparison.OrdinalIgnoreCase)
            && value.EndsWith("/sources", StringComparison.OrdinalIgnoreCase) => "sources",
        { } value when value.StartsWith("/api/management/items/", StringComparison.OrdinalIgnoreCase)
            && value.EndsWith("/location", StringComparison.OrdinalIgnoreCase) => "location",
        _ => null,
    };

    internal void Start(HttpContext context, string route)
    {
        lock (gate)
        {
            requestId = Guid.TryParse(context.Request.Headers[RequestIdHeader], out var supplied)
                ? supplied.ToString("D") : Guid.NewGuid().ToString("D");
            context.Response.Headers[RequestIdHeader] = requestId;
            context.Items[typeof(ManagementRequestDiagnostics)] = this;
            started = stageStarted = time.GetTimestamp();
            active = true;
            logger.LogInformation("Management request {ManagementRequestId} started: route={Route}, stage={Stage}", requestId, route, stage);
            timer = time.CreateTimer(_ => ReportWaiting(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }
    }

    public void BeginStage(string name)
    {
        lock (gate)
        {
            if (!active) return;
            if (stage != "dispatch") EndStage();
            stage = name;
            stageStarted = time.GetTimestamp();
            logger.LogInformation("Management request {ManagementRequestId} stage {Stage} started", requestId, stage);
        }
    }

    public void EndStage(int? resultCount = null)
    {
        lock (gate)
        {
            if (!active) return;
            logger.LogInformation("Management request {ManagementRequestId} stage {Stage} completed in {StageElapsedMs} ms; count={ResultCount}",
                requestId, stage, Elapsed(stageStarted), resultCount);
            stage = "dispatch";
            stageStarted = time.GetTimestamp();
        }
    }

    internal void Query(ManagementQuery query)
    {
        if (!active) return;
        // Called after validation: only bounded enums, numbers and presence flags, never filter values.
        logger.LogInformation("Management request {ManagementRequestId} query: kind={Kind}, view={View}, sort={Sort}, descending={Descending}, limit={Limit}, offset={Offset}, search={HasSearch}, sourceFilters={HasSourceFilters}, focus={HasFocus}",
            requestId, query.Kind, query.View, query.Sort, query.Descending, query.Limit, query.Offset,
            !string.IsNullOrEmpty(query.Search),
            new[] { query.SourceKind, query.Resolution, query.Codec, query.Hdr, query.DvProfile, query.State }.Any(v => !string.IsNullOrEmpty(v)),
            query.FocusId != null);
    }

    internal void IndexSnapshot(int observed, int ready, int failed)
    {
        if (!active) return;
        logger.LogInformation("Management request {ManagementRequestId} indexing snapshot: observed={Observed}, ready={Ready}, failed={Failed}",
            requestId, observed, ready, failed);
    }

    internal void ReportWaiting()
    {
        lock (gate)
        {
            if (!active) return;
            logger.LogWarning("Management request {ManagementRequestId} still running: stage={Stage}, stageElapsedMs={StageElapsedMs}, totalElapsedMs={TotalElapsedMs}",
                requestId, stage, Elapsed(stageStarted), Elapsed(started));
        }
    }

    internal void ReportAborted()
    {
        lock (gate)
        {
            if (active) logger.LogWarning("Management request {ManagementRequestId} caller disconnected: stage={Stage}, totalElapsedMs={TotalElapsedMs}",
                requestId, stage, Elapsed(started));
        }
    }

    internal void Finish(HttpContext context, Exception? error)
    {
        lock (gate)
        {
            if (!active) return;
            var cancelled = context.RequestAborted.IsCancellationRequested;
            var outcome = cancelled ? "cancelled" : error == null ? "completed" : "failed";
            // Exception messages can contain SQL parameters, URLs or paths. Record only the type.
            logger.Log(error != null && !cancelled ? LogLevel.Error : cancelled ? LogLevel.Warning : LogLevel.Information,
                "Management request {ManagementRequestId} {Outcome}: stage={Stage}, status={StatusCode}, stageElapsedMs={StageElapsedMs}, totalElapsedMs={TotalElapsedMs}, errorType={ErrorType}",
                requestId, outcome, stage, error == null || context.Response.HasStarted ? context.Response.StatusCode : (int?)null,
                Elapsed(stageStarted), Elapsed(started), error?.GetType().Name);
            active = false;
        }
        Dispose();
    }

    private long Elapsed(long timestamp) => (long)time.GetElapsedTime(timestamp).TotalMilliseconds;

    public void Dispose()
    {
        lock (gate) active = false;
        timer?.Dispose();
    }
}

/// <summary>Runs before authentication so missing handler logs can be distinguished from slow SQL.</summary>
public sealed class ManagementRequestLoggingMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var route = ManagementRequestDiagnostics.Route(context.Request.Path);
        if (!HttpMethods.IsGet(context.Request.Method) || route == null)
        {
            await next(context);
            return;
        }
        var diagnostics = context.RequestServices.GetRequiredService<ManagementRequestDiagnostics>();
        diagnostics.Start(context, route);
        using var aborted = context.RequestAborted.Register(diagnostics.ReportAborted);
        Exception? failure = null;
        try { await next(context); }
        catch (Exception error) { failure = error; throw; }
        finally { diagnostics.Finish(context, failure); }
    }
}

/// <summary>Wraps implicit WebApplication authentication as well as explicitly configured middleware.</summary>
public sealed class ManagementRequestLoggingStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        // UseWhen's authentication branch does not suppress WebApplication's automatic outer
        // authentication middleware. A startup filter observes both without changing auth behavior.
        app.UseMiddleware<ManagementRequestLoggingMiddleware>();
        next(app);
    };
}
