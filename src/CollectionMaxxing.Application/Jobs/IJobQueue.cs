namespace CollectionMaxxing.Application.Jobs;

/// <summary>Optional settings when putting a job on the queue.</summary>
/// <param name="RunAfter">Do not run before this moment. Null means as soon as a worker is free.</param>
/// <param name="DedupeKey">
/// While a job with this key is pending or running, enqueueing the same key again creates nothing.
/// Once that job has finished (or failed) the key is free again.
/// </param>
public sealed record JobEnqueueOptions(DateTimeOffset? RunAfter = null, string? DedupeKey = null);

/// <summary>The port features use to hand work to the background worker. Handlers are found by <paramref name="type"/> name.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "It is a queue of jobs, not a collection type; the name is fixed by the brief.")]
public interface IJobQueue
{
    /// <summary>Queues a job. Returns its id, or null when the dedupe key matched a job that is still waiting or running.</summary>
    Task<Guid?> EnqueueAsync(string type, object? payload, JobEnqueueOptions? options = null, CancellationToken cancellationToken = default);
}
