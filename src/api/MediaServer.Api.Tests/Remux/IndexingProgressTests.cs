using System.Text.Json;
using MediaServer.Api.Realtime;
using MediaServer.Api.Remux;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Remux;

public sealed class IndexingProgressTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("indexing-progress").FullName;
    private readonly SseRealtimeNotifier _notifier = new();
    private readonly Clock _clock = new();
    private readonly RemuxIndexStore _store;
    private readonly IndexingProgress _progress;
    private readonly RemuxIndexService.Candidate _source;

    public IndexingProgressTests()
    {
        _store = new(_root, NullLogger<RemuxIndexStore>.Instance);
        _progress = new(_store, _notifier, _clock);
        var path = Path.Combine(_root, "film.mkv");
        File.WriteAllBytes(path, new byte[100]);
        var id = Guid.NewGuid();
        _source = new(id, path, Guid.NewGuid(), id);
    }

    [Fact]
    public void Progress_is_throttled_monotonic_and_never_claims_completion_before_save()
    {
        using var subscriber = _notifier.Subscribe();
        Assert.Equal("waiting", Read()!.State);
        _progress.Start(_source);
        Assert.True(subscriber.Reader.TryRead(out var started));
        Assert.Equal("indexingChanged", started.Event);
        Assert.Equal(_source.ItemId, started.VisibleItemId);
        Assert.DoesNotContain(_source.AbsolutePath, started.Data);
        _progress.Report(_source.Key, 40, 100);
        Assert.False(subscriber.Reader.TryRead(out _));
        _clock.Advance();
        _progress.Report(_source.Key, 40, 100);
        var forty = Read()!;
        Assert.Equal(40, forty.Percent);
        _clock.Advance();
        _progress.Report(_source.Key, 10, 100);
        Assert.Equal(forty, Read());
        _progress.Report(_source.Key, 200, 100);
        Assert.Equal(99, Read()!.Percent);
        _progress.Finish(_source.Key, "saving");
        Assert.Equal("saving", Read()!.State);
        Assert.Null(Read()!.Percent);
        _store.Save(_source.Key, _source.AbsolutePath, new MatroskaIndex { SourceLength = 100 });
        _progress.Finish(_source.Key, "ready");
        Assert.Equal("ready", Read()!.State);
        Assert.True(Read()!.Revision > forty.Revision);
        var messages = new List<SseMessage>();
        while (subscriber.Reader.TryRead(out var message)) messages.Add(message);
        using var json = JsonDocument.Parse(messages[^1].Data);
        Assert.Equal("ready", json.RootElement.GetProperty("indexing").GetProperty("state").GetString());
    }

    [Fact]
    public void Failure_retry_cancellation_and_file_replacement_reset_the_snapshot()
    {
        _progress.Start(_source);
        _progress.Finish(_source.Key, "failed");
        var failed = Read()!;
        Assert.Equal("failed", failed.State);
        _progress.Start(_source);
        Assert.True(Read()!.Revision > failed.Revision);
        Assert.Equal(0, Read()!.Percent);
        _progress.Finish(_source.Key, "waiting");
        Assert.Equal("waiting", Read()!.State);
        _progress.Start(_source);
        using var subscriber = _notifier.Subscribe();
        File.WriteAllBytes(_source.AbsolutePath, new byte[200]);
        Assert.False(_progress.IsUnchanged(_source.Key));
        Assert.Equal("waiting", Read()!.State);
        _progress.Finish(_source.Key, "waiting");
        Assert.True(subscriber.Reader.TryRead(out var interrupted));
        Assert.Contains("waiting", interrupted.Data);
        File.Delete(_source.AbsolutePath);
        Assert.Null(Read());
        Assert.Null(_progress.Read(_source.Key, _source.AbsolutePath, "mp4"));
    }

    [Fact]
    public void External_audio_progress_does_not_change_its_video_status()
    {
        var sidecar = _source with { Key = Guid.NewGuid(), StreamId = Guid.NewGuid() };
        using var subscriber = _notifier.Subscribe();
        _progress.Start(sidecar);
        Assert.Equal("waiting", Read()!.State);
        Assert.Equal("indexing", _progress.Read(sidecar.Key, sidecar.AbsolutePath, "mka")!.State);
        Assert.True(subscriber.Reader.TryRead(out var message));
        using var json = JsonDocument.Parse(message.Data);
        Assert.Equal(sidecar.StreamId, json.RootElement.GetProperty("streamId").GetGuid());
        Assert.Equal(_source.SourceId, json.RootElement.GetProperty("sourceId").GetGuid());
    }

    [Fact]
    public void Restart_discards_live_progress_and_rediscovers_pending_work()
    {
        _progress.Start(_source);
        var restarted = new IndexingProgress(_store, _notifier, _clock);
        Assert.Equal("waiting", restarted.Read(_source.Key, _source.AbsolutePath, "mkv")!.State);
    }

    private IndexingStatus? Read() => _progress.Read(_source.Key, _source.AbsolutePath, "mkv");
    public void Dispose() => Directory.Delete(_root, true);

    private sealed class Clock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance() => _timestamp += TimeSpan.TicksPerSecond;
    }
}
