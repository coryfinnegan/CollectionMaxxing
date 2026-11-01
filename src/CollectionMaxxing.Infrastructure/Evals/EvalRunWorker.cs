using CollectionMaxxing.Application.Evals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Infrastructure.Evals;

/// <summary>
/// The hosted loop of the resumable run: ground the photos the phone has sent, one after another as they arrive, and score the run once the
/// phone has finished it. Its queue is the run folder itself (a stored photo without a grounded file is to do), not the database job queue: the
/// evals keep no database rows, and a run that is discarded takes its queue with it. It starts by looking at the folder, so what a restart interrupted
/// is picked up at once. Idle, it sleeps until the API wakes it (a photo arrived, the run was finished) or a few seconds pass. When the phone's run has
/// nothing for it to do, the same loop looks after the other job: grounding the last run's kept readings again after a deploy (<see cref="EvalRegrounder"/>).
/// </summary>
public sealed class EvalRunWorker(
    IGoldenSet golden,
    IEvalRunStore store,
    EvalRunCoordinator coordinator,
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<EvalRunWorker> logger) : BackgroundService
{
    /// <summary>How long the worker sleeps when nothing woke it: it looks at the folder anyway, so a missed wake-up costs a few seconds.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>How long it sleeps while the evals are off: it only needs to notice they were switched on, which takes a restart.</summary>
    private static readonly TimeSpan OffInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var progress = new EvalRunProgress();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var step = golden.IsAvailable && store.IsConfigured ? await StepAsync(progress, stoppingToken) : EvalRunStep.Idle;
                if (step == EvalRunStep.Worked)
                {
                    continue;
                }

                await coordinator.WaitForWorkAsync(golden.IsAvailable ? PollInterval : OffInterval, time, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // The database or the disk may be busy; keep the worker alive and look again after a pause. Only the type is logged: a message may name a path.
                logger.LogError("The eval run worker failed ({Failure}); retrying after a pause.", exception.GetType().Name);
                try
                {
                    await Task.Delay(PollInterval, time, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<EvalRunStep> StepAsync(EvalRunProgress progress, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var step = await scope.ServiceProvider.GetRequiredService<EvalRunProcessor>().StepAsync(progress, cancellationToken);

        // The phone's run comes first. With nothing of it to do, the same pass may ground the last run's kept readings again (see EvalRegrounder).
        return step == EvalRunStep.Idle
            ? await scope.ServiceProvider.GetRequiredService<EvalRegrounder>().StepAsync(progress, cancellationToken)
            : step;
    }
}
