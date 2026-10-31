using CollectionMaxxing.Application.Jobs;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Infrastructure.Jobs;

public sealed class PruneJobsPayload;

/// <summary>
/// Recurring job: the jobs table is a queue, not a log, so rows that finished are removed after a while. A job that
/// succeeded is kept a week; one that failed is kept a month, so a failure can still be looked at. Pending and running jobs
/// are never touched, however old. Age is how long ago the job was last due (<c>run_after</c>, which a retry moves forward),
/// because rows record no finish time; a job finishes soon after it is due.
/// <para>
/// The recurring scheduler remembers which period it already queued by finding that period's old row (any status). A
/// period can be longer than the retention, so the newest row of each recurring job is kept whatever its age: dropping it
/// would queue the same period again.
/// </para>
/// </summary>
public static class PruneJobs
{
    public const string JobType = "jobs.prune";

    public const int BatchSize = 1000;

    /// <summary>A run gives up after this many batches (a few hundred thousand rows); the rest goes the next day.</summary>
    private const int MaxBatchesPerRun = 200;

    public static readonly TimeSpan Every = TimeSpan.FromDays(1);

    public static readonly TimeSpan SucceededRetention = TimeSpan.FromDays(7);

    public static readonly TimeSpan FailedRetention = TimeSpan.FromDays(30);

    public sealed class Pruner(AppDbContext db, TimeProvider clock, ILogger<Pruner> logger) : IJobHandler<PruneJobsPayload>
    {
        public async Task HandleAsync(PruneJobsPayload payload, CancellationToken cancellationToken)
        {
            var now = clock.GetUtcNow();
            var succeededBefore = now - SucceededRetention;
            var failedBefore = now - FailedRetention;

            // The newest row of each recurring job, which the scheduler may still need. At most one per recurring job type.
            var keep = await db.Database.SqlQuery<Guid>($"""
                SELECT DISTINCT ON (type) id AS "Value" FROM jobs
                WHERE (payload ->> 'key') LIKE 'recurring:%'
                ORDER BY type, created_at DESC, id DESC
                """).ToArrayAsync(cancellationToken);

            var total = 0;
            for (var batch = 0; batch < MaxBatchesPerRun; batch++)
            {
                // Oldest first, and rows another worker holds locked are left for the next run. The run_after bounds let the
                // (status, run_after) index find the rows; created_at covers a job that was queued with a past due time.
                var deleted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                    DELETE FROM jobs WHERE id IN (
                        SELECT id FROM jobs
                        WHERE ((status = 'succeeded' AND run_after < {succeededBefore} AND created_at < {succeededBefore})
                            OR (status = 'failed' AND run_after < {failedBefore} AND created_at < {failedBefore}))
                          AND id <> ALL({keep})
                        ORDER BY run_after, id
                        LIMIT {BatchSize}
                        FOR UPDATE SKIP LOCKED)
                    """, cancellationToken);
                total += deleted;
                if (deleted < BatchSize)
                {
                    break;
                }
            }

            if (total > 0 && logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Pruned {Count} finished jobs.", total);
            }
        }
    }
}
