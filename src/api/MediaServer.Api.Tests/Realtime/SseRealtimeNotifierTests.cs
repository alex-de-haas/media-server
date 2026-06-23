using System.Text.Json;
using MediaServer.Api.Realtime;

namespace MediaServer.Api.Tests.Realtime;

/// <summary>Coverage for the SSE fan-out notifier: serialized payloads, multi-subscriber delivery, and detach.</summary>
public sealed class SseRealtimeNotifierTests
{
    [Fact]
    public async Task Publishes_a_named_event_with_camelCase_json_to_a_subscriber()
    {
        var notifier = new SseRealtimeNotifier();
        using var subscription = notifier.Subscribe();

        await notifier.DownloadProgressAsync(new DownloadProgress(
            Guid.NewGuid(), "Downloading", 42.5, 1000, 0, 0, 3, 8_000, 12));

        var message = await ReadAsync(subscription);
        Assert.Equal(RealtimeEvents.DownloadProgress, message.Event);

        using var json = JsonDocument.Parse(message.Data);
        Assert.Equal("Downloading", json.RootElement.GetProperty("state").GetString()); // camelCase keys
        Assert.Equal(42.5, json.RootElement.GetProperty("percentComplete").GetDouble());
    }

    [Fact]
    public async Task Publishes_vpn_status_with_camelCase_json()
    {
        var notifier = new SseRealtimeNotifier();
        using var subscription = notifier.Subscribe();

        await notifier.VpnStatusChangedAsync(new VpnStatusChanged(
            Connected: true, "tun0", "10.8.0.6", "203.0.113.7", "NL", DateTimeOffset.UtcNow));

        var message = await ReadAsync(subscription);
        Assert.Equal(RealtimeEvents.VpnStatusChanged, message.Event);

        using var json = JsonDocument.Parse(message.Data);
        Assert.True(json.RootElement.GetProperty("connected").GetBoolean()); // camelCase keys
        Assert.Equal("tun0", json.RootElement.GetProperty("tunnelInterface").GetString());
        Assert.Equal("203.0.113.7", json.RootElement.GetProperty("exitIp").GetString());
        Assert.Equal("NL", json.RootElement.GetProperty("exitCountry").GetString());
    }

    [Fact]
    public async Task Fans_out_to_every_subscriber()
    {
        var notifier = new SseRealtimeNotifier();
        using var first = notifier.Subscribe();
        using var second = notifier.Subscribe();

        await notifier.JobChangedAsync(RealtimeEvents.JobStarted, new JobEvent(
            Guid.NewGuid(), "scan", null, null, "Running", 0, null));

        Assert.Equal(RealtimeEvents.JobStarted, (await ReadAsync(first)).Event);
        Assert.Equal(RealtimeEvents.JobStarted, (await ReadAsync(second)).Event);
    }

    [Fact]
    public async Task A_disposed_subscriber_stops_receiving()
    {
        var notifier = new SseRealtimeNotifier();
        var subscription = notifier.Subscribe();
        subscription.Dispose();

        // No subscribers left → publishing is a no-op and the disposed reader has completed.
        await notifier.IngestStageChangedAsync(new IngestStageChanged(
            Guid.NewGuid(), null, Guid.NewGuid(), "Identify", "Pending", null));

        Assert.False(subscription.Reader.TryRead(out _));
    }

    private static async Task<SseMessage> ReadAsync(SseRealtimeNotifier.Subscription subscription)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await subscription.Reader.ReadAsync(cts.Token);
    }
}
