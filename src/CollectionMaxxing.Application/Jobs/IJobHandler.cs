namespace CollectionMaxxing.Application.Jobs;

/// <summary>
/// Runs one kind of job. Throw to fail the attempt: the runner retries with backoff (<see cref="JobRetryPolicy"/>).
/// Handlers may run more than once for the same job (a worker can die mid-run), so make them safe to repeat.
/// </summary>
public interface IJobHandler<in TPayload>
{
    Task HandleAsync(TPayload payload, CancellationToken cancellationToken);
}
