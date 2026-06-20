using System.Threading.Channels;

namespace MediaServer.Api.Realtime;

/// <summary>
/// The realtime stream for the activity/downloads UI: a single Server-Sent Events endpoint at
/// <c>/api/events</c>, behind Host identity and reached through the same-origin BFF proxy. Server→client
/// only — operator actions go through REST. Replaces the former SignalR hub.
/// </summary>
public static class SseEndpoints
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    public static void MapRealtimeEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/events", StreamAsync).RequireAuthorization();
    }

    private static async Task StreamAsync(HttpContext context, SseRealtimeNotifier notifier)
    {
        var response = context.Response;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        // Ask any reverse proxy (nginx/cloudflared) not to buffer the stream.
        response.Headers["X-Accel-Buffering"] = "no";

        var token = context.RequestAborted;
        using var subscription = notifier.Subscribe();

        // Open the stream immediately so the client's fetch resolves and it knows it's connected.
        await response.WriteAsync(": connected\n\n", token);
        await response.Body.FlushAsync(token);

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!await WaitForDataOrHeartbeatAsync(subscription.Reader, response, token))
                {
                    break; // channel completed (subscriber detached)
                }

                while (subscription.Reader.TryRead(out var message))
                {
                    await response.WriteAsync($"event: {message.Event}\ndata: {message.Data}\n\n", token);
                }

                await response.Body.FlushAsync(token);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — normal teardown.
        }
    }

    /// <summary>
    /// Waits for the next message, but emits a heartbeat comment and returns true if none arrives within
    /// <see cref="HeartbeatInterval"/> (keeps idle connections alive through proxy idle timeouts). Returns
    /// false only when the channel completes. A per-wait linked CTS provides the timeout without abandoning
    /// a pending read on the single-reader channel.
    /// </summary>
    private static async Task<bool> WaitForDataOrHeartbeatAsync(
        ChannelReader<SseMessage> reader, HttpResponse response, CancellationToken token)
    {
        using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(token);
        heartbeat.CancelAfter(HeartbeatInterval);

        try
        {
            return await reader.WaitToReadAsync(heartbeat.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            await response.WriteAsync(": ping\n\n", token);
            await response.Body.FlushAsync(token);
            return true;
        }
    }
}
