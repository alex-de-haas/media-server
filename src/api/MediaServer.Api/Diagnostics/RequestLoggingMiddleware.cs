using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MediaServer.Api.Diagnostics;

/// <summary>
/// Diagnostic middleware that appends one line per HTTP request — timestamp, local port, method,
/// path + redacted query, final status code, and elapsed time — to a dedicated file, separate from
/// the app's stdout log. Added to chase down client calls (e.g. Infuse) that hit routes we don't
/// expose: those surface as a 404 and are otherwise invisible in the exception log. The local port
/// distinguishes the internal surface from the public Jellyfin one. Enabled in Development and by the
/// operator's <c>PLAYBACK_DIAGNOSTICS</c> setting.
/// </summary>
public sealed class RequestLoggingMiddleware(
    RequestDelegate next, ILogger<RequestLoggingMiddleware> logger, string logFilePath)
{
    // The middleware is a singleton; serialize writes with an async gate so concurrent requests don't
    // block thread-pool threads on file I/O (which a lock + synchronous AppendAllText would).
    private static readonly SemaphoreSlim Gate = new(1, 1);

    // Jellyfin deliberately accepts a reusable access token as the `api_key`/`ApiKey` query parameter
    // for media and image URLs, which clients open without custom headers (see
    // MediaBrowserAuthorization). Logging the raw query would therefore persist working credentials in
    // an operator-readable file — and this log is no longer Development-only. Redact the value while
    // keeping the parameter name, so a trace still shows that a token was supplied. The name match is
    // deliberately broader than the two parameters we accept today: a future credential parameter
    // should be redacted by default rather than leak until someone remembers to add it here.
    private static readonly Regex SensitiveQueryValues = new(
        @"([?&][^&=]*(?:token|key|secret|password|signature)[^&=]*=)[^&]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Masks credential-bearing query values, leaving the rest of the query byte-identical so the
    /// trace stays faithful.
    /// </summary>
    internal static string RedactQuery(string? queryString) =>
        string.IsNullOrEmpty(queryString) ? string.Empty : SensitiveQueryValues.Replace(queryString, "$1***");

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
                $"{request.Path.Value}{RedactQuery(request.QueryString.Value)} -> {context.Response.StatusCode} " +
                $"({stopwatch.ElapsedMilliseconds}ms)";

            try
            {
                await Gate.WaitAsync(context.RequestAborted);
                try
                {
                    await File.AppendAllTextAsync(logFilePath, line + Environment.NewLine, context.RequestAborted);
                }
                finally
                {
                    Gate.Release();
                }
            }
            // Never let request logging break a request; surface the failure to stdout instead — but let a
            // genuine caller-requested cancellation propagate rather than swallowing it.
            catch (Exception exception) when (exception is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Failed to write request log line to {Path}.", logFilePath);
            }
        }
    }
}
