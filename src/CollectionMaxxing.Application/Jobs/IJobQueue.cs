namespace CollectionMaxxing.Application.Jobs;

/// <summary>Optional settings when putting a job on the queue.</summary>
/// <param name="RunAfter">Do not run before this moment. Null means as soon as a worker is free.</param>
/// <param name="DedupeKey">
/// While a job with this key is pending or running, enqueueing the same key again creates nothing.
/// Once that job has finished (or failed) the key is free again.
/// </param>
public sealed record JobEnqueueOptions(DateTimeOffset? RunAfter = null, string? DedupeKey = null);

/// <summary>One job of a batch: its payload and the same optional settings <see cref="IJobQueue.EnqueueAsync"/> takes.</summary>
public sealed record JobToEnqueue(object? Payload, JobEnqueueOptions? Options = null);

/// <summary>The port features use to hand work to the background worker. Handlers are found by <paramref name="type"/> name.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "It is a queue of jobs, not a collection type; the name is fixed by the brief.")]
public interface IJobQueue
{
    /// <summary>Queues a job. Returns its id, or null when the dedupe key matched a job that is still waiting or running.</summary>
    Task<Guid?> EnqueueAsync(string type, object? payload, JobEnqueueOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues several jobs of one type, as if <see cref="EnqueueAsync"/> were called for each in order: the answer holds, in the same
    /// order, each job's id or null when its dedupe key matched a job that is still waiting or running (a key repeated inside the
    /// batch queues its first job only). A queue can do it in fewer round trips than one call each, which is why a feature with a
    /// list of jobs asks for the batch; the jobs also run in the order given.
    /// </summary>
    async Task<IReadOnlyList<Guid?>> EnqueueManyAsync(string type, IReadOnlyList<JobToEnqueue> jobs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        var queued = new List<Guid?>(jobs.Count);
        foreach (var job in jobs)
        {
            queued.Add(await EnqueueAsync(type, job.Payload, job.Options, cancellationToken));
        }

        return queued;
    }
}
