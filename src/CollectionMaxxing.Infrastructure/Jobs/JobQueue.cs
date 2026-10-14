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
}
