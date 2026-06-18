using System.Diagnostics;

namespace MediaServer.Api.Diagnostics;

/// <summary>
/// Diagnostic middleware that appends one line per HTTP request — timestamp, local port, method,
/// path + query, final status code, and elapsed time — to a dedicated file, separate from the app's
/// stdout log. Added to chase down client calls (e.g. Infuse) that hit routes we don't expose: those
/// surface as a 404 and are otherwise invisible in the exception log. The local port distinguishes the
/// internal surface from the public Jellyfin one. Registered only in Development; remove when no longer
/// needed.
/// </summary>
public sealed class RequestLoggingMiddleware(
    RequestDelegate next, ILogger<RequestLoggingMiddleware> logger, string logFilePath)
{
    private static readonly object Gate = new();

    public async Task Invoke(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await next(context);
        }
        finally
        {
            stopwatch.Stop();
            var request = context.Request;
            var line =
                $"{DateTimeOffset.UtcNow:O} :{context.Connection.LocalPort} {request.Method} " +
                $"{request.Path.Value}{request.QueryString.Value} -> {context.Response.StatusCode} " +
                $"({stopwatch.ElapsedMilliseconds}ms)";

            try
            {
                lock (Gate)
                {
                    File.AppendAllText(logFilePath, line + Environment.NewLine);
                }
            }
            catch (Exception exception)
            {
                // Never let request logging break a request; surface the failure to stdout instead.
                logger.LogWarning(exception, "Failed to write request log line to {Path}.", logFilePath);
            }
        }
    }
}
