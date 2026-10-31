namespace CollectionMaxxing.Application.Jobs;

/// <summary>
/// Optional, beside <see cref="IJobHandler{TPayload}"/>: a handler that must settle what its job was working on when the job
/// fails for good (every attempt used, or abandoned on the last one), so nothing keeps waiting for work that will never come.
/// The scan resolve jobs use it to turn a row that says "Looking up…" into an error the person can act on.
/// <para>
/// The runner calls it once, after the job is marked failed, in a fresh scope (the failed run's database context may hold
/// half-made changes), with the same payload and the job's last error text. It is not called for an attempt that will be retried,
/// or when another worker has taken the job over. It must be safe to repeat. If it throws, the runner logs that and carries on:
/// the job stays failed.
/// </para>
/// </summary>
public interface IJobFailureHandler<in TPayload>
{
    Task OnFailedForGoodAsync(TPayload payload, string lastError, CancellationToken cancellationToken);
}
