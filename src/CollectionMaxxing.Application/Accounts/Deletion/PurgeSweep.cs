using CollectionMaxxing.Application.Jobs;

namespace CollectionMaxxing.Application.Accounts.Deletion;

/// <summary>
/// Recurring job (daily): keeps the 30-day promise when a purge job didn't. A purge job that fails five times is left failed and never
/// runs again, so the person's email, linked identities and library would stay for good. This looks for deleted accounts that are past the 30 days
/// and still hold something to purge, and queues <see cref="PurgeAccount"/> for each. It uses the purge's own dedupe key, so an account whose
/// purge job is still waiting or running is left to it. Safe to repeat.
/// </summary>
public static class PurgeSweep
{
    public const string JobType = "account.purge.sweep";

    /// <summary>The sweep job's payload: nothing. It is queued by the recurring-job scheduler.</summary>
    public sealed record Payload;

    public sealed class Handler(IPurgeBacklog backlog, IJobQueue jobs, TimeProvider clock) : IJobHandler<Payload>
    {
        public async Task HandleAsync(Payload payload, CancellationToken cancellationToken)
        {
            var now = clock.GetUtcNow();
            foreach (var userId in await backlog.UsersAwaitingPurgeAsync(now - PurgeAccount.Retention, cancellationToken))
            {
                await jobs.EnqueueAsync(
                    PurgeAccount.JobType,
                    new PurgeAccountPayload(userId),
                    new JobEnqueueOptions(now, PurgeAccount.DedupeKey(userId)),
                    cancellationToken);
            }
        }
    }
}
