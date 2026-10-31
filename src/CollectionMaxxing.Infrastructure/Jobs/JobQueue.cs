using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Jobs;
using CollectionMaxxing.Infrastructure.Persistence;

namespace CollectionMaxxing.Infrastructure.Jobs;

/// <summary>The Postgres-backed <see cref="IJobQueue"/>, over the <c>jobs</c> table.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "It is a queue of jobs, not a collection type.")]
internal sealed class JobQueue(AppDbContext db, IIdGenerator ids, TimeProvider time) : IJobQueue
{
    public async Task<Guid?> EnqueueAsync(string type, object? payload, JobEnqueueOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        var now = time.GetUtcNow();
        var id = ids.NewId();
        var inserted = await JobInserter.InsertAsync(
            db, id, type, payload, options?.RunAfter ?? now, now, options?.DedupeKey, activeOnly: true, cancellationToken);
        return inserted ? id : null;
    }

    public async Task<IReadOnlyList<Guid?>> EnqueueManyAsync(string type, IReadOnlyList<JobToEnqueue> jobs, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(jobs);
        if (jobs.Count == 0)
        {
            return [];
        }

        // One run time for the batch and creation times a microsecond apart: the worker takes jobs by run time, then creation time, then
        // id, so jobs queued together still run in the order given, as they do when queued one call after the other.
        var now = time.GetUtcNow();
        var rows = new List<JobInserter.NewJob>(jobs.Count);
        var rowOf = new int[jobs.Count];
        var keys = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < jobs.Count; i++)
        {
            var key = jobs[i].Options?.DedupeKey;
            if (key is not null && !keys.TryAdd(key, i))
            {
                rowOf[i] = -1; // the first job of that key is queued, so this one finds it already waiting
                continue;
            }

            var createdAt = now.AddTicks(i * TimeSpan.TicksPerMicrosecond);
            rowOf[i] = rows.Count;
            rows.Add(new JobInserter.NewJob(ids.NewId(), jobs[i].Payload, jobs[i].Options?.RunAfter ?? now, createdAt, key));
        }

        var inserted = await JobInserter.InsertManyAsync(db, type, rows, cancellationToken);
        return [.. rowOf.Select(row => row >= 0 && inserted.Contains(rows[row].Id) ? rows[row].Id : (Guid?)null)];
    }
}
