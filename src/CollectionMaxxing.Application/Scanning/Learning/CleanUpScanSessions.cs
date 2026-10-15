using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Jobs;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Scanning.Learning;

/// <summary>
/// Recurring job (daily): removes scan sessions nobody committed once they are 7 days old, with their candidates and
/// snaps. A committed session stays: its candidates say where the items came from.
/// </summary>
public sealed class CleanUpScanSessions(IAppDbContext db, TimeProvider clock) : IJobHandler<CleanUpScanSessions.Job>
{
    public const string JobType = "scan.clean_up_sessions";

    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>Recurring jobs carry no data.</summary>
    public sealed record Job;

    public async Task HandleAsync(Job payload, CancellationToken cancellationToken)
    {
        var cutoff = clock.GetUtcNow() - MaxAge;
        await db.ScanSessions.IgnoreQueryFilters()
            .Where(s => s.Status != ScanSessionStatus.Committed && s.CreatedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
