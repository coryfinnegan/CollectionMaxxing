using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Jobs;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Audit;

public sealed class PruneAuditEventsPayload;

/// <summary>Recurring job: audit events are kept for one year, then removed.</summary>
public static class PruneAuditEvents
{
    public const string JobType = "audit.prune";
    public static readonly TimeSpan Retention = TimeSpan.FromDays(365);
    public static readonly TimeSpan Every = TimeSpan.FromDays(1);

    public sealed class Handler(IAppDbContext db, TimeProvider clock) : IJobHandler<PruneAuditEventsPayload>
    {
        public async Task HandleAsync(PruneAuditEventsPayload payload, CancellationToken cancellationToken)
        {
            var cutoff = clock.GetUtcNow() - Retention;
            await db.AuditEvents.Where(e => e.At < cutoff).ExecuteDeleteAsync(cancellationToken);
        }
    }
}
