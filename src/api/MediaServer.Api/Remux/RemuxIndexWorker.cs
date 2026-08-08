namespace MediaServer.Api.Remux;

/// <summary>
/// Builds missing remux indexes in the background, one at a time.
///
/// One at a time deliberately. A walk is bound by the disk it reads, and on the spinning disks this library
/// lives on, several at once would be slower in total than one after another while making everything else
/// on the same disk worse. Nothing waits on this: the point of building ahead is that playback never has
/// to.
///
/// There is no queue. The database already knows which sources exist and the store already knows which have
/// an index, so the outstanding work is a query rather than a thing to keep in sync — which also means a
/// restart resumes without remembering anything.
/// </summary>
public sealed class RemuxIndexWorker(IServiceScopeFactory scopeFactory, ILogger<RemuxIndexWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Busy = TimeSpan.FromSeconds(2);

    /// <summary>How many to build before going back to the database, so a long run still sees new work.</summary>
    private const int Batch = 8;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A moment before the first pass: startup has enough to do, and this is the least urgent thing
        // in the process.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var pruned = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            var built = 0;
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<RemuxIndexService>();

                if (!pruned)
                {
                    // Once per process: a title deleted while the server was down leaves an index behind,
                    // and nothing else will notice.
                    await service.PruneAsync(stoppingToken);
                    pruned = true;
                }

                foreach (var candidate in await service.PendingAsync(Batch, stoppingToken))
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }

                    if (await service.BuildAsync(candidate, stoppingToken))
                    {
                        built++;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Unhandled error building remux indexes.");
            }

            try
            {
                // Straight back to work while there was work; otherwise wait, because an empty query every
                // few seconds over a settled library is pure noise.
                await Task.Delay(built > 0 ? Busy : Idle, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
