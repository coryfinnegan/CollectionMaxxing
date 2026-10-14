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

    private sealed record Envelope(string? Key, object Data);
}
