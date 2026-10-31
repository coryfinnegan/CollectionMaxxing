using System.Text.Json;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Infrastructure.Jobs;

/// <summary>
/// The one place that inserts job rows. The payload column holds <c>{"key": dedupeKey|null, "data": payload}</c>:
/// the table has no dedupe column, and the key must be checked and inserted atomically, so the insert takes a
/// transaction-scoped advisory lock on the key first.
/// </summary>
internal static class JobInserter
{
    /// <returns>True when a row was inserted; false when the dedupe key matched an existing job.</returns>
    /// <param name="activeOnly">Match only pending or running jobs (user dedupe) rather than any job ever queued (recurring slots).</param>
    public static async Task<bool> InsertAsync(
        AppDbContext db,
        Guid id,
        string type,
        object? payload,
        DateTimeOffset runAfter,
        DateTimeOffset now,
        string? dedupeKey,
        bool activeOnly,
        CancellationToken cancellationToken)
    {
        var envelope = JsonSerializer.Serialize(new Envelope(dedupeKey, payload ?? new object()), JobsRegistration.Json);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (dedupeKey is not null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({dedupeKey}, 0))", cancellationToken);
        }

        // A separate statement, so it sees any row the lock holder before us just committed.
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO jobs (id, type, payload, status, attempts, run_after, created_at)
            SELECT {id}, {type}, {envelope}::jsonb, 'pending', 0, {runAfter}, {now}
            WHERE {dedupeKey}::text IS NULL
               OR NOT EXISTS (
                   SELECT 1 FROM jobs
                   WHERE payload ->> 'key' = {dedupeKey}::text
                     AND ({activeOnly}::boolean = FALSE OR status IN ('pending', 'running')))
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return inserted > 0;
    }

    /// <summary>One row of <see cref="InsertManyAsync"/>.</summary>
    public sealed record NewJob(Guid Id, object? Payload, DateTimeOffset RunAfter, DateTimeOffset CreatedAt, string? DedupeKey);

    /// <summary>
    /// Inserts <paramref name="jobs"/> of one type in one transaction and two statements, each as <see cref="InsertAsync"/> would with
    /// <c>activeOnly</c>. A key must not repeat inside <paramref name="jobs"/> (the rows are checked against the table, not against each
    /// other). The keys are locked in one fixed order (their hash, lowest first), so two batches that share keys wait for each other
    /// instead of deadlocking, and a single insert (which holds one lock) can never be part of a cycle.
    /// </summary>
    /// <returns>The ids of the rows inserted; a job whose key matched a pending or running job is missing from it.</returns>
    public static async Task<HashSet<Guid>> InsertManyAsync(
        AppDbContext db, string type, IReadOnlyList<NewJob> jobs, CancellationToken cancellationToken)
    {
        var ids = jobs.Select(j => j.Id).ToArray();
        var payloads = jobs.Select(j => JsonSerializer.Serialize(new Envelope(j.DedupeKey, j.Payload ?? new object()), JobsRegistration.Json)).ToArray();
        var runAfter = jobs.Select(j => j.RunAfter).ToArray();
        var createdAt = jobs.Select(j => j.CreatedAt).ToArray();
        var keys = jobs.Select(j => j.DedupeKey).ToArray();
        var lockedKeys = keys.OfType<string>().ToArray();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (lockedKeys.Length > 0)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT pg_advisory_xact_lock(locks.hash)
                FROM (SELECT DISTINCT hashtextextended(key, 0) AS hash FROM unnest({lockedKeys}::text[]) AS key ORDER BY hash) AS locks
                """, cancellationToken);
        }

        // A separate statement, so it sees any row the lock holders before us just committed.
        var inserted = await db.Database.SqlQuery<Guid>($"""
            INSERT INTO jobs (id, type, payload, status, attempts, run_after, created_at)
            SELECT new.id, {type}, new.payload::jsonb, 'pending', 0, new.run_after, new.created_at
            FROM unnest({ids}::uuid[], {payloads}::text[], {runAfter}::timestamptz[], {createdAt}::timestamptz[], {keys}::text[])
                AS new(id, payload, run_after, created_at, key)
            WHERE new.key IS NULL
               OR NOT EXISTS (
                   SELECT 1 FROM jobs
                   WHERE payload ->> 'key' = new.key
                     AND status IN ('pending', 'running'))
            RETURNING id AS "Value"
            """).ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return [.. inserted];
    }

    private sealed record Envelope(string? Key, object Data);
}
