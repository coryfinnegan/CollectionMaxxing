using System.Text.Json;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Infrastructure.Jobs;

/// <summary>
/// Picks up one due job and runs it. Safe to call from many workers at once: the pick is a single
/// <c>UPDATE ... FOR UPDATE SKIP LOCKED</c>, so each job is handed to exactly one caller. A job whose lock has
/// expired (its worker died) counts as due again. Every finish is fenced on the worker's lock, and a finish that
/// finds its lock gone (another worker re-claimed the job mid-run) is logged as an error, so a double-run never
/// passes silently.
/// </summary>
public sealed class JobRunner(
    IEnumerable<JobRegistration> registrations,
    IServiceScopeFactory scopes,
    IOptions<JobOptions> options,
    TimeProvider time,
    ILogger<JobRunner> logger)
{
    private readonly Dictionary<string, JobRegistration> _handlers = registrations.ToDictionary(r => r.Type, StringComparer.Ordinal);

    /// <returns>True when a job was picked up (whatever its outcome); false when nothing was due.</returns>
    public async Task<bool> RunNextAsync(string workerId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = options.Value;

        var now = time.GetUtcNow();
        var until = now + settings.LockDuration;
        var claimed = (await db.Database.SqlQuery<Claimed>($"""
            UPDATE jobs SET status = 'running', attempts = attempts + 1, locked_by = {workerId}, locked_until = {until}
            WHERE id = (
                SELECT id FROM jobs
                WHERE (status = 'pending' AND run_after <= {now}) OR (status = 'running' AND locked_until < {now})
                ORDER BY run_after, created_at, id
                LIMIT 1
                FOR UPDATE SKIP LOCKED)
            RETURNING id AS "Id", type AS "Type", (payload -> 'data')::text AS "Data", attempts AS "Attempts"
            """).ToListAsync(cancellationToken)).SingleOrDefault();
        if (claimed is null)
        {
            return false;
        }

        try
        {
            if (claimed.Attempts > settings.Retry.MaxAttempts)
            {
                // It was abandoned (worker died) on its last allowed attempt: do not run it again.
                await FailAsync(db, claimed, workerId, "Abandoned by a worker that stopped before finishing.", cancellationToken);
                return true;
            }

            if (!_handlers.TryGetValue(claimed.Type, out var handler))
            {
                throw new InvalidOperationException($"No handler is registered for job type '{claimed.Type}'.");
            }

            using var data = JsonDocument.Parse(claimed.Data ?? "{}");
            await handler.Invoke(scope.ServiceProvider, data.RootElement.Clone(), cancellationToken);
            var finished = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE jobs SET status = 'succeeded', locked_by = NULL, locked_until = NULL, last_error = NULL
                WHERE id = {claimed.Id} AND locked_by = {workerId}
                """, CancellationToken.None);
            if (finished == 0)
            {
                LockLost(claimed, workerId, "finished");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down mid-job: hand it back untouched so the next start runs it, and do not count this try.
            var handedBack = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE jobs SET status = 'pending', attempts = attempts - 1, locked_by = NULL, locked_until = NULL
                WHERE id = {claimed.Id} AND locked_by = {workerId}
                """, CancellationToken.None);
            if (handedBack == 0)
            {
                logger.LogWarning(
                    "Job {JobId} ({JobType}) was handed back on shutdown, but another worker had already re-claimed it.",
                    claimed.Id, claimed.Type);
            }

            throw;
        }
        catch (Exception exception)
        {
            var error = JobErrorText.From(exception);
            logger.LogWarning("Job {JobId} ({JobType}) failed on attempt {Attempt}: {Error}", claimed.Id, claimed.Type, claimed.Attempts, error);
            if (settings.Retry.ShouldRetry(claimed.Attempts))
            {
                var runAfter = time.GetUtcNow() + settings.Retry.DelayAfter(claimed.Attempts);
                var rescheduled = await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE jobs SET status = 'pending', run_after = {runAfter}, last_error = {error}, locked_by = NULL, locked_until = NULL
                    WHERE id = {claimed.Id} AND locked_by = {workerId}
                    """, CancellationToken.None);
                if (rescheduled == 0)
                {
                    LockLost(claimed, workerId, "failed and was due a retry");
                }
            }
            else
            {
                await FailAsync(db, claimed, workerId, error, CancellationToken.None);
            }
        }

        return true;
    }

    private async Task FailAsync(AppDbContext db, Claimed job, string workerId, string error, CancellationToken cancellationToken)
    {
        var failed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE jobs SET status = 'failed', last_error = {error}, locked_by = NULL, locked_until = NULL
            WHERE id = {job.Id} AND locked_by = {workerId}
            """, cancellationToken);
        if (failed == 0)
        {
            LockLost(job, workerId, "could not be marked failed");
            return; // another worker has the job now; it settles whatever it was for
        }

        await NotifyFailedForGoodAsync(job, error, cancellationToken);
    }

    /// <summary>
    /// Tells the handler its job failed for good, so it can settle what the job was working on (a scanned row that would
    /// otherwise say "Looking up…" for days). A fresh scope: the failed run's database context may hold half-made changes.
    /// Whatever goes wrong here is logged and never changes the outcome; the job stays failed.
    /// </summary>
    private async Task NotifyFailedForGoodAsync(Claimed job, string error, CancellationToken cancellationToken)
    {
        if (!_handlers.TryGetValue(job.Type, out var handler) || handler.OnFailedForGood is not { } onFailedForGood)
        {
            return;
        }

#pragma warning disable CA1031 // The failure hook must never turn a recorded failure into a crash of the worker.
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            using var data = JsonDocument.Parse(job.Data ?? "{}");
            await onFailedForGood(scope.ServiceProvider, data.RootElement.Clone(), error, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Job {JobId} ({JobType}) failed for good, and settling what it was for failed too: {Error}",
                job.Id, job.Type, JobErrorText.From(exception));
        }
#pragma warning restore CA1031
    }

    /// <summary>Another worker re-claimed the job while this run was still going (its lock expired): say so loudly.</summary>
    private void LockLost(Claimed job, string workerId, string outcome) =>
        logger.LogError(
            "Job {JobId} ({JobType}) {Outcome}, but its lock was already gone: worker {WorkerId} was still running it when another worker re-claimed it, so the job ran twice.",
            job.Id, job.Type, outcome, workerId);

    private sealed record Claimed(Guid Id, string Type, string? Data, int Attempts);
}
