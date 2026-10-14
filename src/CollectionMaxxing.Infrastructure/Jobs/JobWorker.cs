using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Infrastructure.Jobs;

/// <summary>The hosted loop: queue due recurring jobs, run what is due, sleep when idle. Stops promptly on shutdown.</summary>
public sealed class JobWorker(
    JobRunner runner,
    RecurringJobScheduler scheduler,
    IOptions<JobOptions> options,
    TimeProvider time,
    ILogger<JobWorker> logger) : BackgroundService
{
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await scheduler.EnqueueDueAsync(stoppingToken);
                while (await runner.RunNextAsync(_workerId, stoppingToken))
                {
                }

                await Task.Delay(options.Value.PollInterval, time, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // The database may be restarting; keep the worker alive and try again after a pause.
                logger.LogError(exception, "Job worker loop failed; retrying after the poll interval.");
                try
                {
                    await Task.Delay(options.Value.PollInterval, time, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
