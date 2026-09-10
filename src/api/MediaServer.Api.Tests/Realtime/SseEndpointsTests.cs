using System.Text;
using Imposter.Abstractions;
using MediaServer.Api.Data;
using MediaServer.Api.Realtime;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.AspNetCore.Http;

[assembly: GenerateImposter(typeof(IServiceProvider))]

namespace MediaServer.Api.Tests.Realtime;

public sealed class SseEndpointsTests
{
    [Fact]
    public async Task Resolves_database_once_but_rechecks_visibility_for_each_indexing_event()
    {
        using var fixture = new JellyfinDatabase();
        var database = fixture.Context;
        var item = new MediaItem { Id = Guid.NewGuid(), PublicId = "visible", Kind = MediaKind.Movie, Title = "Film" };
        database.MediaItems.Add(item);
        await database.SaveChangesAsync();
        var resolutions = 0;
        var services = IServiceProvider.Imposter();
        services.GetService(Arg<Type>.Any()).Returns((Type type) =>
        {
            Assert.Equal(typeof(MediaServerDbContext), type);
            resolutions++;
            return database;
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var output = new ObservedOutput();
        var context = new DefaultHttpContext { RequestServices = services.Instance(), RequestAborted = timeout.Token };
        context.Response.Body = output;
        var notifier = new SseRealtimeNotifier();
        var streaming = SseEndpoints.StreamAsync(context, notifier);
        try
        {
            var source = Guid.NewGuid();
            notifier.IndexingChanged(new(item.Id, source, null, new("indexing", 10, 1)));
            await output.FirstIndex.Task.WaitAsync(timeout.Token);
            notifier.IndexingChanged(new(item.Id, source, null, new("indexing", 20, 2)));
            await output.SecondIndex.Task.WaitAsync(timeout.Token);

            item.RemovedAt = DateTimeOffset.UtcNow;
            await database.SaveChangesAsync(timeout.Token);
            notifier.IndexingChanged(new(item.Id, source, null, new("saving", null, 3)));
            await notifier.JobChangedAsync(RealtimeEvents.JobCompleted,
                new(Guid.NewGuid(), "marker", null, null, "Completed", 100, null));
            await output.Marker.Task.WaitAsync(timeout.Token);
        }
        finally
        {
            await timeout.CancelAsync();
            await streaming;
        }

        Assert.Equal(1, resolutions);
        var text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("\"percent\":10", text);
        Assert.Contains("\"percent\":20", text);
        Assert.DoesNotContain("saving", text);
    }

    private sealed class ObservedOutput : MemoryStream
    {
        public TaskCompletionSource FirstIndex { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondIndex { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Marker { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            var text = Encoding.UTF8.GetString(ToArray());
            if (text.Contains("\"percent\":10")) FirstIndex.TrySetResult();
            if (text.Contains("\"percent\":20")) SecondIndex.TrySetResult();
            if (text.Contains("event: jobCompleted")) Marker.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
