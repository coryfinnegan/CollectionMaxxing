using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Jobs;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Application.Scanning.Quota;

/// <summary>
/// Recurring job (every UTC night): looks up the codes that waited for the UPC allowance, oldest first, until the allowance
/// is spent again. What it can't reach stays Pending for the next night. Each code runs as the person who scanned it
/// (<see cref="ResolvePendingCandidate"/>), in a scope of its own.
/// </summary>
public sealed class DrainQuotaBacklog(IAppDbContext db, IServiceScopeFactory scopes) : IJobHandler<DrainQuotaBacklog.Job>
{
    public const string JobType = "scan.drain_quota_queue";

    /// <summary>Recurring jobs carry no data.</summary>
    public sealed record Job;

    public async Task HandleAsync(Job payload, CancellationToken cancellationToken)
    {
        // The job has no signed-in caller, so it reads across libraries; each code is then resolved as its owner.
        var waiting = await (from c in db.ScanCandidates.IgnoreQueryFilters().AsNoTracking()
                             join s in db.ScanSessions.IgnoreQueryFilters().AsNoTracking() on c.SessionId equals s.Id
                             where c.Resolution == CandidateResolution.Pending && s.Status == ScanSessionStatus.Open
                             orderby c.CreatedAt, c.Seq
                             select new { c.Id, c.SessionId, c.LibraryId, s.UserId, c.TraceJson })
            .ToListAsync(cancellationToken);

        foreach (var candidate in waiting.Where(c => UpcQuotaWindow.IsSpent(CandidateJson.ReadTrace(c.TraceJson).Steps)))
        {
            await using var scope = scopes.CreateAsyncScope();
            var outcome = await scope.ServiceProvider.GetRequiredService<ResolvePendingCandidate>().RunAsync(
                new SubmitBarcodes.ResolveJob(candidate.LibraryId, candidate.SessionId, candidate.Id, candidate.UserId), cancellationToken);
            if (outcome == ResolveOutcome.QuotaSpent)
            {
                return; // the allowance is gone again; the rest wait for tomorrow
            }
        }
    }
}
