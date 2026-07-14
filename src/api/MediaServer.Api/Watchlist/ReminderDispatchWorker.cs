using MediaServer.Api.Jobs;

namespace MediaServer.Api.Watchlist;

/// <summary>
/// Runs <see cref="ReminderDispatchService"/> on a frequent timer (and once shortly after startup). The
/// tick is a pure local query — no external API — so the cadence lands a reminder close to its chosen
/// time. A quiet tick stays silent; a tick that delivered or retired something is recorded as a completed
/// job so it shows on the activity feed without 96 no-op rows a day.
/// </summary>
public sealed class ReminderDispatchWorker(IServiceScopeFactory scopeFactory, ILogger<ReminderDispatchWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(Interval);
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<ReminderDispatchService>();
            var report = await service.DispatchAsync(cancellationToken);

            if (report.Delivered > 0 || report.Retired > 0)
            {
                var jobs = scope.ServiceProvider.GetRequiredService<JobService>();
                var job = await jobs.StartAsync(ReminderDispatchService.JobType, "watchlist", null, cancellationToken);
                await jobs.CompleteAsync(job, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Reminder dispatch tick failed.");
        }
    }
}
