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
/// expired (its worker died) counts as due again.
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
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE jobs SET status = 'succeeded', locked_by = NULL, locked_until = NULL, last_error = NULL
                WHERE id = {claimed.Id} AND locked_by = {workerId}
                """, CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down mid-job: hand it back untouched so the next start runs it, and do not count this try.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE jobs SET status = 'pending', attempts = attempts - 1, locked_by = NULL, locked_until = NULL
                WHERE id = {claimed.Id} AND locked_by = {workerId}
                """, CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            var error = JobErrorText.From(exception);
            logger.LogWarning("Job {JobId} ({JobType}) failed on attempt {Attempt}: {Error}", claimed.Id, claimed.Type, claimed.Attempts, error);
            if (settings.Retry.ShouldRetry(claimed.Attempts))
            {
                var runAfter = time.GetUtcNow() + settings.Retry.DelayAfter(claimed.Attempts);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE jobs SET status = 'pending', run_after = {runAfter}, last_error = {error}, locked_by = NULL, locked_until = NULL
                    WHERE id = {claimed.Id} AND locked_by = {workerId}
                    """, CancellationToken.None);
            }
            else
            {
                await FailAsync(db, claimed, workerId, error, CancellationToken.None);
            }
        }

        return true;
    }

    private static Task<int> FailAsync(AppDbContext db, Claimed job, string workerId, string error, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE jobs SET status = 'failed', last_error = {error}, locked_by = NULL, locked_until = NULL
            WHERE id = {job.Id} AND locked_by = {workerId}
            """, cancellationToken);

    private sealed record Claimed(Guid Id, string Type, string? Data, int Attempts);
}
