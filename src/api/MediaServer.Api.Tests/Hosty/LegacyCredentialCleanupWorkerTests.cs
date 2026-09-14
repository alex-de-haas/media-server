using System.Text.Json;
using Imposter.Abstractions;
using MediaServer.Api.Hosty;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

[assembly: GenerateImposter(typeof(IHostyCoreClient))]

namespace MediaServer.Api.Tests.Hosty;

public sealed class LegacyCredentialCleanupWorkerTests
{
    [Theory]
    [InlineData("http")]
    [InlineData("json")]
    [InlineData("cancellation")]
    public async Task UnexpectedFailure_IsLoggedWithoutSensitiveDetails_AndRetries(string failure)
    {
        var calls = 0;
        var secrets = IHostyCoreSecrets.Imposter();
        secrets.ListSecretKeysAsync(Arg<CancellationToken>.Any()).Returns((CancellationToken _) =>
        {
            if (++calls == 1)
            {
                throw failure switch
                {
                    "http" => new HttpRequestException("sensitive response"),
                    "json" => new JsonException("sensitive response"),
                    _ => new OperationCanceledException("sensitive response"),
                };
            }
            return Task.FromResult<IReadOnlyList<string>>([]);
        });
        var time = new RetryTime();
        var logger = new RecordingLogger();
        using var worker = Worker(secrets.Instance(), time, logger);
        await worker.StartAsync(default);
        try
        {
            var retry = await time.Scheduled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TimeSpan.FromMinutes(5), time.DueTime);
            Assert.Equal(1, calls);
            Assert.False(worker.ExecuteTask!.IsCompleted);
            var log = Assert.Single(logger.Messages);
            Assert.Null(log.Exception);
            Assert.DoesNotContain("sensitive response", log.Message);
            Assert.Contains("retrying later", log.Message);

            retry();
            await worker.ExecuteTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, calls);
        }
        finally
        {
            await worker.StopAsync(default);
        }
    }

    [Fact]
    public async Task Shutdown_DuringCoreRequest_CompletesWithoutWarningOrRetry()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secrets = IHostyCoreSecrets.Imposter();
        secrets.ListSecretKeysAsync(Arg<CancellationToken>.Any()).Returns(async (CancellationToken ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return (IReadOnlyList<string>)[];
        });
        var logger = new RecordingLogger();
        var time = new RetryTime();
        using var worker = Worker(secrets.Instance(), time, logger);
        await worker.StartAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Empty(logger.Messages);
        Assert.False(time.Scheduled.Task.IsCompleted);
    }

    [Fact]
    public async Task Shutdown_DuringRetryDelay_CompletesWithoutAnotherAttempt()
    {
        var calls = 0;
        var secrets = IHostyCoreSecrets.Imposter();
        secrets.ListSecretKeysAsync(Arg<CancellationToken>.Any()).Returns((CancellationToken _) =>
        {
            calls++;
            throw new CoreSecretsUnavailableException("Unavailable");
        });
        var time = new RetryTime();
        var logger = new RecordingLogger();
        using var worker = Worker(secrets.Instance(), time, logger);
        await worker.StartAsync(default);
        try
        {
            await time.Scheduled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Equal(1, calls);
        Assert.Empty(logger.Messages);
    }

    private static LegacyCredentialCleanupWorker Worker(
        IHostyCoreSecrets secrets, TimeProvider time, ILogger<LegacyCredentialCleanupWorker> logger)
    {
        var core = IHostyCoreClient.Imposter();
        core.IsEnabled.Getter().Returns(true);
        return new(new(secrets, NullLogger<LegacyCredentialCleanup>.Instance), core.Instance(), time, logger);
    }

    // A real timer kept dormant until the test releases the retry; cancellation still disposes it.
    private sealed class RetryTime : TimeProvider
    {
        public TaskCompletionSource<Action> Scheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeSpan DueTime { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            DueTime = dueTime;
            var timer = base.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            Scheduled.TrySetResult(() => callback(state));
            return timer;
        }
    }

    private sealed class RecordingLogger : ILogger<LegacyCredentialCleanupWorker>
    {
        public List<(Exception? Exception, string Message)> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add((exception, formatter(state, exception)));
    }
}
