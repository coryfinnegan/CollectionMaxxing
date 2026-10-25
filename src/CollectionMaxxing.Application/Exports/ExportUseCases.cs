using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Jobs;
using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Application.Exports;

public static class ExportErrors
{
    /// <summary>No such export for this person: never made, somebody else's, or already cleaned up. All look the same on purpose.</summary>
    public static readonly DomainError NotFound = new(ErrorKind.NotFound, "export.not_found", "That export doesn't exist.");
}

/// <summary>Use case: start an export of everything the caller can see. Asking again while one is running returns that one.</summary>
public static class StartExport
{
    /// <summary>How often a start retries when the queue is contended. One retry always settles it; the rest is paranoia.</summary>
    private const int MaxAttempts = 3;

    public sealed class Handler(IJobQueue queue, IExportJobs jobs, IIdGenerator ids)
    {
        public async Task<ExportDto> HandleAsync(Guid userId, CancellationToken cancellationToken)
        {
            // A dedupe hit usually means "one is already running", but it may finish between the enqueue and the
            // lookup; then the key is free again and the next pass queues this export for real. Without the retry the
            // caller gets an id no job will ever build ("queued, then vanished").
            var exportId = ids.NewId();
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var queued = await queue.EnqueueAsync(
                    BuildExport.JobType, new BuildExport.Payload(exportId, userId), new JobEnqueueOptions(DedupeKey: $"export:{userId:N}"), cancellationToken);
                if (queued is not null)
                {
                    return new ExportDto(exportId, ExportStatus.Queued, null);
                }

                if (await jobs.FindActiveAsync(userId, cancellationToken) is { } existing)
                {
                    return new ExportDto(existing, ExportStatus.Queued, null);
                }
            }

            throw new InvalidOperationException("Could not start an export: the queue stayed contended.");
        }
    }
}

/// <summary>The states an export reports.</summary>
public static class ExportStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Ready = "ready";
    public const string Failed = "failed";
}

/// <summary>Use case: where is the caller's export? A ready one has no link yet: the API adds it, because the link is signed there.</summary>
public static class GetExport
{
    public sealed class Handler(IExportJobs jobs, IExportStore store)
    {
        public async Task<Result<ExportDto>> HandleAsync(Guid userId, Guid exportId, CancellationToken cancellationToken)
        {
            var state = await jobs.FindAsync(exportId, cancellationToken);
            if (state is null || state.UserId != userId)
            {
                return ExportErrors.NotFound;
            }

            switch (state.JobStatus)
            {
                case "pending":
                    return new ExportDto(exportId, ExportStatus.Queued, null);
                case "running":
                    return new ExportDto(exportId, ExportStatus.Running, null);
                case "failed":
                    return new ExportDto(exportId, ExportStatus.Failed, null);
                default:
                    return store.Exists(exportId) ? new ExportDto(exportId, ExportStatus.Ready, null) : ExportErrors.NotFound;
            }
        }
    }
}

/// <summary>Use case: open the zip of a ready export. Only its owner can.</summary>
public static class OpenExport
{
    public sealed class Handler(IExportJobs jobs, IExportStore store)
    {
        public async Task<Stream?> HandleAsync(Guid userId, Guid exportId, CancellationToken cancellationToken)
        {
            var state = await jobs.FindAsync(exportId, cancellationToken);
            return state is { JobStatus: "succeeded" } && state.UserId == userId
                ? await store.OpenReadAsync(exportId, cancellationToken)
                : null;
        }
    }
}

/// <summary>The recurring job that deletes export files once they are older than the retention time (24 hours).</summary>
public static class CleanupExports
{
    public const string JobType = "exports.cleanup";

    public sealed record Payload;

    public sealed class Handler(IExportStore store, ExportSettings settings, TimeProvider time) : IJobHandler<Payload>
    {
        public async Task HandleAsync(Payload payload, CancellationToken cancellationToken) =>
            await store.DeleteCreatedBeforeAsync(time.GetUtcNow() - settings.Retention, cancellationToken);
    }
}
